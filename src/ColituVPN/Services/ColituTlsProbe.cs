using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace v2rayN.Desktop.Services;

/// <summary>Result classes of one TLS probe (TLS-PROBE-SPEC §1).</summary>
public static class ColituTlsClass
{
    /// <summary>The handshake completed, or failed for a certificate/alert/protocol reason after the server answered.</summary>
    public const string Ok = "ok";
    /// <summary>TCP connected, then reset or closed (EOF, 0 bytes) before or during the handshake.</summary>
    public const string TlsRst = "tls_rst";
    /// <summary>TCP connected, nothing within the handshake budget.</summary>
    public const string Timeout = "timeout";
    /// <summary>TCP connect failed or timed out (not a TLS result).</summary>
    public const string TcpFail = "tcp_fail";
}

/// <summary>One TLS probe of one endpoint: its port, class and time (to the first server bytes when ok, else elapsed).</summary>
public sealed record ColituTlsResult(int Port, string Class, int Ms);

/// <summary>
/// TLS probe of one endpoint outside the tunnel (TLS-PROBE-SPEC §1): TCP connect ≤ 3 s bound to the physical
/// interface (SO_BINDTODEVICE) like the ping probes, then a ClientHello with the endpoint's SNI and ≤ 2.5 s for the
/// server's answer. With the nftables kill switch engaged the caller opens the address and port first
/// (ColituKillSwitch.AllowTlsProbesAsync).
/// Certificate validation is off for the probe only: it only asks whether the server answered TLS.
/// </summary>
public static class ColituTlsProbe
{
    public const int ConnectTimeoutMs = 3000;
    public const int HandshakeTimeoutMs = 2500;
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;

    /// <summary>The TLS-over-TCP transports the classifier probes.</summary>
    public static readonly string[] TlsProtocols = ["vless-reality", "vless-xhttp", "trojan"];

    /// <summary>
    /// The class of a failed handshake (.NET): a reset or EOF anywhere in the chain is <c>tls_rst</c>, a timeout or
    /// cancel <c>timeout</c>, any other authentication failure (alert, certificate, protocol) <c>ok</c> since the
    /// server answered TLS; anything unknown counts as <c>timeout</c> (never as a block).
    /// </summary>
    public static string ClassOf(Exception? exception)
    {
        for (var ex = exception; ex != null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case OperationCanceledException or TimeoutException:
                    return ColituTlsClass.Timeout;
                case SocketException socket when socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown or SocketError.Disconnecting:
                    return ColituTlsClass.TlsRst;
                case SocketException { SocketErrorCode: SocketError.TimedOut }:
                    return ColituTlsClass.Timeout;
            }
            var text = ex.Message.ToLowerInvariant();
            if (text.Contains("unexpected eof") || text.Contains("0 bytes") || text.Contains("closed the transport stream")
                || text.Contains("connection reset") || text.Contains("forcibly closed") || text.Contains("reset by peer"))
            {
                return ColituTlsClass.TlsRst;
            }
        }
        for (var ex = exception; ex != null; ex = ex.InnerException)
        {
            if (ex is AuthenticationException)
            {
                return ColituTlsClass.Ok;
            }
        }
        return ColituTlsClass.Timeout;
    }

    /// <summary>The SNI of an endpoint: its <c>server_name</c>, else the host when it is a name (an address sends none).</summary>
    public static string? Sni(string? sni, string? host) =>
        sni?.Trim() is { Length: > 0 } name ? name
        : host?.Trim() is { Length: > 0 } value && !IPAddress.TryParse(value, out _) ? value
        : null;

    /// <summary>
    /// Probes several endpoints in parallel: every host is resolved first, then <paramref name="beforeProbe"/> lets the
    /// address and port pairs through the kill switch in one call, then each is probed. A null result: nothing was
    /// dialled (the name did not resolve, or <paramref name="requirePin"/> was set and the socket could not be bound).
    /// </summary>
    public static async Task<List<(T Item, ColituTlsResult? Result)>> ProbeManyAsync<T>(IReadOnlyList<T> items, Func<T, string> host, Func<T, int> port,
        Func<T, string?> sni, string? device, bool requirePin, Func<IReadOnlyList<(IPAddress Address, int Port)>, Task>? beforeProbe, CancellationToken token = default)
    {
        var resolved = await Task.WhenAll(items.Select(async item =>
        {
            try
            {
                using var resolve = CancellationTokenSource.CreateLinkedTokenSource(token);
                resolve.CancelAfter(ConnectTimeoutMs);
                return await ColituNetwork.ResolveServerAsync(host(item), resolve.Token);
            }
            catch
            {
                return null;
            }
        }));
        var pairs = items.Select((item, index) => (Address: resolved[index], Port: port(item)))
            .Where(pair => pair.Address != null).Select(pair => (pair.Address!, pair.Port)).ToList();
        if (pairs.Count > 0 && beforeProbe != null)
        {
            try
            {
                await beforeProbe(pairs);
            }
            catch
            {
                // The probes still run; blocked ones end as TCP failures, which never count.
            }
        }
        var results = await Task.WhenAll(items.Select(async (item, index) =>
            resolved[index] is { } address && !token.IsCancellationRequested
                ? await ProbeAsync(address, port(item), Sni(sni(item), host(item)), device, requirePin, token)
                : null));
        return items.Select((item, index) => (item, results[index])).ToList();
    }

    /// <summary>One probe of an address (see the class summary). Cancelling <paramref name="token"/> ends it as a timeout.</summary>
    public static async Task<ColituTlsResult?> ProbeAsync(IPAddress address, int port, string? sni, string? device, bool requirePin, CancellationToken token = default)
    {
        var watch = Stopwatch.StartNew();
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var pinned = false;
        if (device.IsNotEmpty() && OperatingSystem.IsLinux())
        {
            try
            {
                // Same as ColituLatency: kernels before 5.7 need CAP_NET_RAW for it.
                socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(device + "\0"));
                pinned = true;
            }
            catch (SocketException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }
        }
        if (requirePin && !pinned)
        {
            return null;
        }
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
            connect.CancelAfter(ConnectTimeoutMs);
            await socket.ConnectAsync(new IPEndPoint(address, port), connect.Token);
        }
        catch (Exception)
        {
            return new ColituTlsResult(port, ColituTlsClass.TcpFail, Elapsed(watch));
        }
        var first = new FirstByteStream(new NetworkStream(socket, ownsSocket: false), watch);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(HandshakeTimeoutMs);
        // A stack that ignores the token still ends: the socket is closed under it.
        using var closeOnTimeout = handshake.Token.Register(() =>
        {
            try { socket.Dispose(); } catch { }
        });
        try
        {
            await using var ssl = new SslStream(first, leaveInnerStreamOpen: true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni ?? "",
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            }, handshake.Token);
            return new ColituTlsResult(port, ColituTlsClass.Ok, first.FirstByteMs ?? Elapsed(watch));
        }
        catch (Exception ex)
        {
            var kind = handshake.IsCancellationRequested ? ColituTlsClass.Timeout : ClassOf(ex);
            return new ColituTlsResult(port, kind, kind == ColituTlsClass.Ok ? first.FirstByteMs ?? Elapsed(watch) : Elapsed(watch));
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    /// <summary>
    /// The probe event of one result (TLS-PROBE-SPEC §4): outcome ok for <c>ok</c>, else fail with reason
    /// tls_rst | timeout | refused (tcp_fail); <paramref name="measure"/> true for a measurement run.
    /// </summary>
    public static ColituAcEvent Event(string? nodeId, string? protocol, ColituTlsResult result, bool measure) => new()
    {
        Kind = "probe",
        NodeId = nodeId,
        Protocol = protocol,
        Outcome = result.Class == ColituTlsClass.Ok ? "ok" : "fail",
        Reason = ReasonOf(result.Class),
        Ms = result.Ms,
        Probe = "tls",
        Port = result.Port,
        Measure = measure
    };

    /// <summary>"" for ok, tls_rst, timeout, refused (TCP failure).</summary>
    public static string ReasonOf(string tlsClass) => tlsClass switch
    {
        ColituTlsClass.Ok => "",
        ColituTlsClass.TlsRst => "tls_rst",
        ColituTlsClass.TcpFail => "refused",
        _ => "timeout"
    };

    private static int Elapsed(Stopwatch watch) => Math.Max(1, (int)watch.ElapsedMilliseconds);

    /// <summary>Passes reads and writes through and notes when the server's first bytes came.</summary>
    private sealed class FirstByteStream(Stream inner, Stopwatch watch) : Stream
    {
        public int? FirstByteMs { get; private set; }

        private int Note(int read)
        {
            if (read > 0 && FirstByteMs == null)
            {
                FirstByteMs = Math.Max(1, (int)watch.ElapsedMilliseconds);
            }
            return read;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => Note(inner.Read(buffer, offset, count));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Note(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Note(await inner.ReadAsync(buffer, cancellationToken));
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

/// <summary>
/// The ip_blocked classifier on the AC3 hinted start (no round probe there): the TLS probes run next to the first
/// transport attempt. A first attempt that carries traffic never waits for them (they are cancelled); after a failed
/// one their results are awaited, at most <see cref="WaitAfterFailure"/>. Used once per server.
/// </summary>
public sealed class ColituTlsGate
{
    public static readonly TimeSpan WaitAfterFailure = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _cancel = new();
    private readonly Task<List<(string Protocol, ColituTlsResult Result)>> _probe;

    /// <summary>Starts <paramref name="probe"/> at once (it must keep its own budget and never throw).</summary>
    public ColituTlsGate(Func<CancellationToken, Task<List<(string Protocol, ColituTlsResult Result)>>> probe)
    {
        _probe = RunAsync(probe);
    }

    private async Task<List<(string Protocol, ColituTlsResult Result)>> RunAsync(Func<CancellationToken, Task<List<(string Protocol, ColituTlsResult Result)>>> probe)
    {
        try
        {
            return await probe(_cancel.Token);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>True once the probes were cancelled (the first attempt carried traffic, or the server was left).</summary>
    public bool Cancelled => _cancel.IsCancellationRequested;

    /// <summary>Stops the probes; their results are never looked at.</summary>
    public void Cancel()
    {
        try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// The first transport attempt ended. <paramref name="attemptOk"/>: the probes are cancelled and null comes back at
    /// once (the connect never waits). Failed: the probes' results, awaited at most <paramref name="wait"/> (default
    /// <see cref="WaitAfterFailure"/>); empty when they are not done by then.
    /// </summary>
    public async Task<List<(string Protocol, ColituTlsResult Result)>?> AfterAttemptAsync(bool attemptOk, TimeSpan? wait = null)
    {
        if (attemptOk)
        {
            Cancel();
            return null;
        }
        if (await Task.WhenAny(_probe, Task.Delay(wait ?? WaitAfterFailure)) == _probe)
        {
            return await _probe;
        }
        Cancel();
        return [];
    }
}

/// <summary>One endpoint the panel asks to measure (<c>measure_targets.targets</c>).</summary>
public sealed record ColituMeasureTarget(string NodeId, string EndpointId, string Protocol, string Host, int Port, string? Sni);

/// <summary>The panel's measurement list (<c>measure_targets</c>, TLS-PROBE-SPEC §3).</summary>
public sealed record ColituMeasureTargets(int IntervalH, List<ColituMeasureTarget> Targets);

/// <summary>
/// The ip_blocked classifier (TLS-PROBE-SPEC §2) and the measurement schedule (§3): pure, fixture-tested.
/// </summary>
public static class ColituIpBlock
{
    /// <summary>At most this many endpoints of one server are TLS-probed in a round probe.</summary>
    public const int MaxRoundProbes = 2;
    /// <summary>A measurement run (every target in parallel) ends after this.</summary>
    public const int MeasureRunMs = 8000;
    public const int MaxTargets = 16;
    public const int DefaultIntervalH = 6;

    /// <summary>ip_blocked ⇔ at least two <c>tls_rst</c> results on distinct ports; timeouts and TCP failures never count.</summary>
    public static bool IsBlocked(IEnumerable<ColituTlsResult> results) =>
        results.Where(result => result.Class == ColituTlsClass.TlsRst).Select(result => result.Port).Distinct().Count() >= 2;

    /// <summary>Exactly one reset next to an answer: not blocked, only worth a log line.</summary>
    public static bool OneReset(IReadOnlyCollection<ColituTlsResult> results) =>
        results.Count(result => result.Class == ColituTlsClass.TlsRst) >= 1 && !IsBlocked(results);

    /// <summary>Up to <paramref name="max"/> endpoints of distinct ports among the TLS TCP transports, in the given order.</summary>
    public static List<T> PickEndpoints<T>(IEnumerable<T> items, Func<T, string> protocol, Func<T, int> port, int max = MaxRoundProbes)
    {
        var ports = new HashSet<int>();
        var picked = new List<T>();
        foreach (var item in items)
        {
            if (picked.Count >= max)
            {
                break;
            }
            if (Array.IndexOf(ColituTlsProbe.TlsProtocols, protocol(item)) >= 0 && port(item) is > 0 and <= 65535 && ports.Add(port(item)))
            {
                picked.Add(item);
            }
        }
        return picked;
    }

    /// <summary>
    /// A measurement run is due ⇔ the VPN is off (not connected, not connecting), there are targets, and this network
    /// never ran one or the last one is at least <paramref name="intervalH"/> hours old.
    /// </summary>
    public static bool MeasureDue(bool vpnOn, int targets, TimeSpan? sinceLastRun, int intervalH) =>
        !vpnOn && targets > 0 && (sinceLastRun is not { } age || age >= TimeSpan.FromHours(intervalH));

    /// <summary>
    /// <c>measure_targets</c>: null when absent, not an object or without a usable target (nothing to do). Targets
    /// need a node UUID, a known TLS protocol, a host name or address and a port (443 or ≥ 1024); at most 16.
    /// </summary>
    public static ColituMeasureTargets? Parse(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }
        var interval = value.TryGetProperty("interval_h", out var hours) && hours.ValueKind == JsonValueKind.Number && hours.TryGetInt32(out var h)
            ? Math.Clamp(h, 1, 168)
            : DefaultIntervalH;
        if (!value.TryGetProperty("targets", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var targets = new List<ColituMeasureTarget>();
        foreach (var item in list.EnumerateArray())
        {
            if (targets.Count >= MaxTargets)
            {
                break;
            }
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var node = ColituAcSpec.NodeId(Text(item, "node_id"));
            var protocol = ColituAcSpec.Protocol(Text(item, "protocol"));
            var host = Text(item, "host")?.Trim().ToLowerInvariant();
            var port = item.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var number) ? number : 0;
            if (node == null || protocol == null || Array.IndexOf(ColituTlsProbe.TlsProtocols, protocol) < 0
                || host is not { Length: > 0 and <= 253 } || host.Any(char.IsWhiteSpace) || !(port == 443 || port is >= 1024 and <= 65535))
            {
                continue;
            }
            var sni = Text(item, "sni")?.Trim();
            targets.Add(new ColituMeasureTarget(node, Text(item, "endpoint_id")?.Trim() ?? "", protocol, host, port,
                sni is { Length: > 0 and <= 253 } ? sni : null));
        }
        return targets.Count > 0 ? new ColituMeasureTargets(interval, targets) : null;
    }

    /// <summary><c>features.tls_probe_events</c>: true only when the panel sends a literal true (absence = false).</summary>
    public static bool ParseEventsFeature(IReadOnlyDictionary<string, JsonElement>? features) =>
        features != null && features.TryGetValue("tls_probe_events", out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.Net;
using System.Net.Sockets;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Ping of a location: the TCP connect time from this computer to the node's
/// latency probe address (the panel's latency_host / latency_port), best of two
/// tries, like the phone apps. The socket is bound to the physical interface
/// with SO_BINDTODEVICE where the kernel allows it, so the number describes this
/// computer's own path to the server even while the VPN is connected.
/// The probe address comes from the server list, so it is bounded like on the phone: at most
/// <see cref="MaxServers"/> servers, <see cref="Parallel"/> connects at a time, only
/// <see cref="AllowedPorts"/>, and never a loopback, private, link-local or CGNAT address after
/// resolving (no scan of this computer or its LAN).
/// </summary>
public static class ColituLatency
{
    private const int TimeoutMs = 2500;
    internal const int MaxServers = 200;
    internal const int Parallel = 8;

    /// <summary>Ports a node's probe may use: the transports' TLS-style ports and the usual alternatives.</summary>
    internal static readonly IReadOnlySet<int> AllowedPorts = new HashSet<int>(Global.ColituPingPorts);
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;

    /// <summary>Pings of every server with a probe address, by server id.</summary>
    public static async Task<Dictionary<string, int>> MeasureAllAsync(IEnumerable<ColituVpnServer> servers)
    {
        var device = ColituNetwork.PhysicalInterfaceName();
        using var gate = new SemaphoreSlim(Parallel);
        var probes = servers
            .Where(CanProbe)
            .GroupBy(server => server.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(MaxServers)
            .Select(async server =>
            {
                await gate.WaitAsync();
                try
                {
                    return (server.Id!, await MeasureAsync(server.Host!, server.Port!.Value, device));
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToList();
        var results = await Task.WhenAll(probes);
        return results
            .Where(result => result.Item2 != null)
            .ToDictionary(result => result.Item1, result => result.Item2!.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The public IPv4 addresses the probes of <paramref name="servers"/> dial (same resolver and
    /// filters as <see cref="MeasureAsync"/>), for the TUN ping rule (AppManager.PingRouteTargets).
    /// Whatever does not resolve within <paramref name="budget"/> is left out: those probes rely on
    /// the adapter binding alone.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveTargetsAsync(IEnumerable<ColituVpnServer> servers, TimeSpan budget, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(budget);
        using var gate = new SemaphoreSlim(Parallel);
        var hosts = servers
            .Where(CanProbe)
            .Select(server => server.Host!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxServers)
            .ToList();
        var lookups = hosts.Select(async host =>
        {
            try
            {
                await gate.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            try
            {
                return await ColituNetwork.ResolveServerAsync(host, deadline.Token);
            }
            catch
            {
                return null;
            }
            finally
            {
                gate.Release();
            }
        });
        var addresses = await Task.WhenAll(lookups);
        return addresses
            .OfType<IPAddress>()
            .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && IsPublic(address))
            .Select(address => address.ToString())
            .Distinct()
            .Take(256)
            .ToList();
    }

    /// <summary>
    /// The targets kept by <see cref="SaveTargets"/>, filtered as <see cref="ResolveTargetsAsync"/>
    /// filters (public IPv4, at most 256); empty when there is no readable file.
    /// </summary>
    public static IReadOnlyList<string> LoadTargets(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }
            var values = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [];
            return values
                .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
                .OfType<IPAddress>()
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && IsPublic(address))
                .Select(address => address.ToString())
                .Distinct()
                .Take(256)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Keeps <paramref name="targets"/> (owner-only file) for the next start; a failure only costs the rule at the next automatic connect.</summary>
    public static void SaveTargets(string path, IEnumerable<string> targets)
    {
        try
        {
            ColituHardening.WritePrivateText(path, System.Text.Json.JsonSerializer.Serialize(targets.Distinct().Take(256).ToArray()));
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ColituLatency.SaveTargets: {ex.GetType().Name}");
        }
    }

    /// <summary>The server has a probe address: a missing result from <see cref="MeasureAllAsync"/> means the ping failed.</summary>
    public static bool CanProbe(ColituVpnServer server) =>
        server.Id.IsNotEmpty() && server.Port is { } port && ProbeAllowed(server.Host, port);

    /// <summary><paramref name="port"/> is an allowed probe port and <paramref name="host"/> a name or a public literal.</summary>
    internal static bool ProbeAllowed(string? host, int port)
    {
        if (!AllowedPorts.Contains(port))
        {
            return false;
        }
        var h = host?.Trim() ?? "";
        if (h.Length is 0 or > 253 || h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return !IPAddress.TryParse(h.Trim('[', ']'), out var literal) || IsPublic(literal);
    }

    public static async Task<int?> MeasureAsync(string host, int port, string? device)
    {
        if (!ProbeAllowed(host, port))
        {
            return null;
        }
        IPAddress? address;
        try
        {
            // Resolved first (DNS over HTTPS, cached) so the lookup is not counted as ping
            // and a fake-IP answer from another VPN client is never dialled.
            using var resolveTimeout = new CancellationTokenSource(TimeoutMs);
            address = await ColituNetwork.ResolveServerAsync(host, resolveTimeout.Token);
        }
        catch
        {
            return null;
        }
        if (address == null || address.AddressFamily != AddressFamily.InterNetwork || !IsPublic(address))
        {
            return null;
        }

        int? best = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var ms = await ConnectMsAsync(address, port, device);
            if (ms != null && (best == null || ms < best))
            {
                best = ms;
            }
        }
        return best;
    }

    /// <summary>
    /// VPN nodes are on the internet: a probe address in loopback, private, CGNAT or
    /// link-local space (a bad or tampered server list) is never dialled, so the ping
    /// cannot be turned into a scan of this computer or its local network.
    /// </summary>
    internal static bool IsPublic(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        return !(b[0] is 0 or 10 or 127
            || b[0] >= 224
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168));
    }

    internal static async Task<int?> ConnectMsAsync(IPAddress address, int port, string? device)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            if (device.IsNotEmpty() && OperatingSystem.IsLinux())
            {
                try
                {
                    socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(device + "\0"));
                }
                catch (SocketException)
                {
                    // Kernels before 5.7 need CAP_NET_RAW: the measurement still works, possibly through the tunnel.
                }
            }
            using var timeout = new CancellationTokenSource(TimeoutMs);
            var watch = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            watch.Stop();
            return Math.Max(1, (int)watch.ElapsedMilliseconds);
        }
        catch
        {
            return null;
        }
    }
}

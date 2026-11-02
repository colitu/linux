using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace v2rayN.Desktop.Services;

/// <summary>
/// A display-only ping through the tunnel, for a server that the probe pinned to the physical
/// adapter could not reach while connected and that has no VPN-off ping of the last 30 minutes.
/// It goes through the core's local SOCKS port (the check inbound when there is one) to the
/// server's probe address and times the CONNECT plus a TLS handshake, so the far end has really
/// answered (a SOCKS server may confirm CONNECT before dialling). The number is "this PC → exit →
/// server", shown in the list only: it never decides reachability (<see cref="ColituReachability"/>).
/// The address is resolved here and must be public, like the ordinary ping (no scan through the exit).
/// Certificates are not checked: nothing is sent over the TLS session, only its setup is timed.
/// </summary>
public static class ColituTunnelPing
{
    public const int TimeoutMs = 4000;
    public const int MaxConcurrency = 4;
    public const int MaxTargets = 40;

    /// <summary>One result per server tried: its ms, or null when it failed.</summary>
    public static async Task<Dictionary<string, int?>> MeasureAsync(IEnumerable<ColituVpnServer> servers, int socksPort, NetworkCredential? credentials, CancellationToken token = default)
    {
        using var gate = new SemaphoreSlim(MaxConcurrency);
        var targets = servers
            .Where(ColituLatency.CanProbe)
            .GroupBy(server => server.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(MaxTargets)
            .ToList();
        var results = await Task.WhenAll(targets.Select(async server =>
        {
            await gate.WaitAsync(token);
            try
            {
                return (Id: server.Id!, Ms: await ProbeAsync(server.Host!, server.Port!.Value, socksPort, credentials, TimeoutMs, token));
            }
            finally
            {
                gate.Release();
            }
        }));
        return results.ToDictionary(result => result.Id, result => result.Ms, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<int?> ProbeAsync(string host, int port, int socksPort, NetworkCredential? credentials, int timeoutMs, CancellationToken token = default)
    {
        if (socksPort is <= 0 or > 65535)
        {
            return null;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeoutMs);
        try
        {
            var address = await ColituNetwork.ResolveServerAsync(host.Trim(), deadline.Token);
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork || !ColituLatency.IsPublic(address))
            {
                return null;
            }
            using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, socksPort, deadline.Token);
            var stream = client.GetStream();
            if (!await HandshakeAsync(stream, credentials, deadline.Token))
            {
                return null;
            }
            var watch = Stopwatch.StartNew();
            await stream.WriteAsync(ConnectRequest(address, port), deadline.Token);
            if (!await ReadConnectReplyAsync(stream, deadline.Token))
            {
                return null;
            }
            using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = IPAddress.TryParse(host.Trim(), out _) ? "" : host.Trim(),
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }, deadline.Token);
            watch.Stop();
            return Math.Max(1, (int)watch.ElapsedMilliseconds);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>SOCKS5 greeting, with username/password when the port asks for one.</summary>
    internal static async Task<bool> HandshakeAsync(Stream stream, NetworkCredential? credentials, CancellationToken token)
    {
        var method = credentials != null ? (byte)0x02 : (byte)0x00;
        await stream.WriteAsync(new byte[] { 0x05, 0x01, method }, token);
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, token);
        if (reply[0] != 0x05 || reply[1] != method)
        {
            return false;
        }
        if (credentials == null)
        {
            return true;
        }
        var user = Encoding.UTF8.GetBytes(credentials.UserName ?? "");
        var pass = Encoding.UTF8.GetBytes(credentials.Password ?? "");
        if (user.Length > 255 || pass.Length > 255)
        {
            return false;
        }
        var auth = new byte[3 + user.Length + pass.Length];
        auth[0] = 0x01;
        auth[1] = (byte)user.Length;
        user.CopyTo(auth, 2);
        auth[2 + user.Length] = (byte)pass.Length;
        pass.CopyTo(auth, 3 + user.Length);
        await stream.WriteAsync(auth, token);
        await stream.ReadExactlyAsync(reply, token);
        return reply[1] == 0x00;
    }

    /// <summary>CONNECT to an IPv4 address.</summary>
    internal static byte[] ConnectRequest(IPAddress address, int port)
    {
        var request = new byte[10];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x01;
        address.GetAddressBytes().CopyTo(request, 4);
        request[8] = (byte)(port >> 8);
        request[9] = (byte)(port & 0xFF);
        return request;
    }

    /// <summary>Reads the CONNECT reply (with its bound address); true when it succeeded.</summary>
    internal static async Task<bool> ReadConnectReplyAsync(Stream stream, CancellationToken token)
    {
        var head = new byte[4];
        await stream.ReadExactlyAsync(head, token);
        if (head[0] != 0x05 || head[1] != 0x00)
        {
            return false;
        }
        var length = head[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => -1,
            _ => -2
        };
        if (length == -2)
        {
            return false;
        }
        if (length == -1)
        {
            var size = new byte[1];
            await stream.ReadExactlyAsync(size, token);
            length = size[0];
        }
        var rest = new byte[length + 2];
        await stream.ReadExactlyAsync(rest, token);
        return true;
    }
}

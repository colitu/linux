using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Network facts the VPN needs before it starts the core: the server's real
/// address, the physical interface to send through, and whether another VPN is
/// already holding the default route. Routes come from /proc/net/route.
/// </summary>
public static class ColituNetwork
{
    private static HttpClient Doh = CreateDohClient();
    private static readonly Dictionary<string, (IPAddress Address, DateTimeOffset Until)> ResolveCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();

    /// <summary>DNS-over-HTTPS resolvers used ahead of the core; the kill switch lets them through.</summary>
    internal static readonly string[] DohEndpoints = ["https://cloudflare-dns.com/dns-query", "https://dns.google/resolve"];

    /// <summary>
    /// Resolves a server host name to a real address, asking DNS-over-HTTPS
    /// first. Other VPN clients running in "fake IP" mode (Clash, sing-box)
    /// answer every system DNS query with a private placeholder address; the
    /// core would then dial that placeholder on the physical interface and hang.
    /// Returns null when only such placeholders are available.
    /// </summary>
    public static async Task<IPAddress?> ResolveServerAsync(string host, CancellationToken token = default)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        lock (CacheGate)
        {
            if (ResolveCache.TryGetValue(host, out var cached) && cached.Until > DateTimeOffset.UtcNow)
            {
                return cached.Address;
            }
        }

        var resolved = await ResolveViaDohOrAdapterAsync(host, token) ?? await ResolveViaSystemAsync(host, token);
        if (resolved != null)
        {
            lock (CacheGate)
            {
                ResolveCache[host] = (resolved, DateTimeOffset.UtcNow.AddMinutes(10));
            }
        }
        return resolved;
    }

    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;
    private static readonly TimeSpan AdapterDnsTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DohHeadStart = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// DoH first; the physical interface's own resolvers only when DoH fails or stays silent for
    /// <see cref="DohHeadStart"/> (both DoH services are blocked in China). Asking them only then
    /// keeps the server names away from the ISP's resolver wherever DoH works (Russia), and a DoH
    /// answer that arrives before theirs still wins (an ISP resolver may hand out a block page
    /// address). They are asked straight from this process: while the kill switch is armed the
    /// system resolver (systemd-resolved) can't reach anything, and the rules let only this app's
    /// uid send UDP 53 to exactly these addresses (<see cref="ColituKillSwitch.Allowance.AdapterResolvers"/>).
    /// </summary>
    private static async Task<IPAddress?> ResolveViaDohOrAdapterAsync(string host, CancellationToken token)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var doh = ResolveViaDohAsync(host, race.Token);
            if (await Task.WhenAny(doh, Task.Delay(DohHeadStart, race.Token)) == doh && await doh is { } early)
            {
                return early;
            }
            var adapter = ResolveViaAdapterDnsAsync(host, race.Token);
            if (await Task.WhenAny(doh, adapter) == doh && await doh is { } fromDoh)
            {
                return fromDoh;
            }
            if (await adapter is { } fromAdapter)
            {
                Logging.SaveLog($"ColituNetwork | {ColituLogPrivacy.ForLog(host)} resolved through the adapter's DNS (DoH failed or silent)");
                return fromAdapter;
            }
            return await doh;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            race.Cancel();
        }
    }

    /// <summary>
    /// The IPv4 DNS servers of the physical interface (at most three): systemd-resolved's per-link
    /// servers, else the nameservers of /etc/resolv.conf when it does not point at a local stub.
    /// The kill switch lets the app's uid reach exactly these on UDP 53. Empty when none are known.
    /// </summary>
    public static IReadOnlyList<IPAddress> AdapterDnsServers()
    {
        if (!OperatingSystem.IsLinux() || PhysicalInterfaceName() is not { } device)
        {
            return [];
        }
        try
        {
            var resolvectl = ColituShell.SystemBinary("resolvectl");
            var servers = File.Exists(resolvectl) ? ParseResolvectlDns(ColituShell.Run(resolvectl, "dns", device)) : [];
            if (servers.Count == 0 && File.Exists("/etc/resolv.conf"))
            {
                servers = ParseResolvConf(File.ReadAllText("/etc/resolv.conf"));
            }
            return servers;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituNetwork.AdapterDnsServers", ex);
            return [];
        }
    }

    /// <summary>"Link 2 (wlp3s0): 192.168.1.1 fe80::1%3" → the usable IPv4 servers.</summary>
    internal static List<IPAddress> ParseResolvectlDns(string? output)
    {
        var addresses = new List<IPAddress>();
        foreach (var line in (output ?? "").Split('\n'))
        {
            var colon = line.IndexOf("):", StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }
            foreach (var word in line[(colon + 2)..].Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                // "1.2.3.4#dns.example" (DoT server name) and "1.2.3.4:53" are written by newer systemd.
                var text = word.Split('#')[0];
                if (text.Count(ch => ch == ':') == 1)
                {
                    text = text.Split(':')[0];
                }
                if (IPAddress.TryParse(text, out var address))
                {
                    addresses.Add(address);
                }
            }
        }
        return UsableDnsServers(addresses);
    }

    /// <summary>"nameserver" lines of resolv.conf → the usable IPv4 servers (a local stub such as 127.0.0.53 is none).</summary>
    internal static List<IPAddress> ParseResolvConf(string? text)
    {
        var addresses = new List<IPAddress>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var words = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && words[0] == "nameserver" && IPAddress.TryParse(words[1], out var address))
            {
                addresses.Add(address);
            }
        }
        return UsableDnsServers(addresses);
    }

    private static List<IPAddress> UsableDnsServers(IEnumerable<IPAddress> addresses) => addresses
        .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
        .Where(address => address.AddressFamily == AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(address)
            && !address.Equals(IPAddress.Any)
            && !address.Equals(IPAddress.Broadcast)
            && !IsPlaceholder(address))
        .Distinct()
        .Take(3)
        .ToList();

    /// <summary>An A record for <paramref name="host"/> from the physical interface's DNS servers, asked directly over UDP.</summary>
    internal static async Task<IPAddress?> ResolveViaAdapterDnsAsync(string host, CancellationToken token)
    {
        var device = PhysicalInterfaceName();
        var servers = AdapterDnsServers();
        if (servers.Count == 0)
        {
            return null;
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        race.CancelAfter(AdapterDnsTimeout);
        var pending = servers.Select(server => QueryUdpDnsAsync(server, host, device, race.Token)).ToList();
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);
            if (await finished is { } address)
            {
                race.Cancel();
                return address;
            }
        }
        return null;
    }

    internal static async Task<IPAddress?> QueryUdpDnsAsync(IPAddress server, string host, string? device, CancellationToken token)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (device.IsNotEmpty() && OperatingSystem.IsLinux())
            {
                try
                {
                    // Out of the physical interface even while the TUN owns the default route.
                    socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(device + "\0"));
                }
                catch (SocketException)
                {
                    // Kernels before 5.7 need CAP_NET_RAW: the query follows the routing table.
                }
            }
            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            await socket.SendToAsync(BuildDnsQuery(id, host), SocketFlags.None, new IPEndPoint(server, 53), token);
            var buffer = new byte[1500];
            while (true)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token);
                if (received.RemoteEndPoint is not IPEndPoint from || !from.Address.Equals(server))
                {
                    continue;
                }
                var reply = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                if (DnsAnswerState(reply, id) == null)
                {
                    continue;
                }
                return FirstARecord(reply) is { } address && !IsPlaceholder(address) ? address : null;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>A recursive DNS query for the A record of <paramref name="name"/>.</summary>
    internal static byte[] BuildDnsQuery(ushort id, string name)
    {
        var packet = new List<byte> { (byte)(id >> 8), (byte)(id & 0xFF), 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }
        packet.AddRange(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01 });
        return packet.ToArray();
    }

    /// <summary>
    /// For a reply to query <paramref name="id"/>: true when the resolver answered (no error or
    /// "no such name"), false when it reported a failure (SERVFAIL, refused). Null for anything else.
    /// </summary>
    internal static bool? DnsAnswerState(byte[] reply, ushort id)
    {
        if (reply.Length < 12 || reply[0] != (byte)(id >> 8) || reply[1] != (byte)(id & 0xFF) || (reply[2] & 0x80) == 0)
        {
            return null;
        }
        var rcode = reply[3] & 0x0F;
        return rcode is 0 or 3;
    }

    /// <summary>The first IPv4 address in the answer section of a DNS reply (CNAMEs skipped); null on errors.</summary>
    internal static IPAddress? FirstARecord(byte[] reply)
    {
        if (reply.Length < 12 || (reply[3] & 0x0F) != 0)
        {
            return null;
        }
        var questions = reply[4] << 8 | reply[5];
        var answers = reply[6] << 8 | reply[7];
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            if (!SkipDnsName(reply, ref offset) || offset + 4 > reply.Length)
            {
                return null;
            }
            offset += 4;
        }
        for (var i = 0; i < answers; i++)
        {
            if (!SkipDnsName(reply, ref offset) || offset + 10 > reply.Length)
            {
                return null;
            }
            var type = reply[offset] << 8 | reply[offset + 1];
            var recordClass = reply[offset + 2] << 8 | reply[offset + 3];
            var length = reply[offset + 8] << 8 | reply[offset + 9];
            offset += 10;
            if (offset + length > reply.Length)
            {
                return null;
            }
            if (type == 1 && recordClass == 1 && length == 4)
            {
                return new IPAddress(reply.AsSpan(offset, 4));
            }
            offset += length;
        }
        return null;
    }

    private static bool SkipDnsName(byte[] packet, ref int offset)
    {
        while (offset < packet.Length)
        {
            var length = packet[offset];
            if (length == 0)
            {
                offset++;
                return true;
            }
            if ((length & 0xC0) == 0xC0)
            {
                offset += 2;
                return offset <= packet.Length;
            }
            if ((length & 0xC0) != 0)
            {
                return false;
            }
            offset += 1 + length;
        }
        return false;
    }

    /// <summary>
    /// Records a server that is dialed by IP address (nothing was resolved for it), so the kill switch
    /// lets the tunnel's entry server through like any resolved one.
    /// </summary>
    internal static void RememberServerAddress(IPAddress address)
    {
        lock (CacheGate)
        {
            ResolveCache[address.ToString()] = (address, DateTimeOffset.UtcNow.AddMinutes(10));
        }
    }

    /// <summary>Every address resolved so far (VPN servers, the panel); the kill switch keeps them reachable.</summary>
    internal static IReadOnlyList<IPAddress> KnownAddresses()
    {
        lock (CacheGate)
        {
            return ResolveCache.Values.Select(item => item.Address).Distinct().ToList();
        }
    }

    /// <summary>
    /// The addresses resolved so far for every name except <paramref name="exceptHosts"/> (the panel
    /// and the DoH resolvers): the VPN servers, which the kill switch treats apart from those.
    /// </summary>
    internal static IReadOnlyList<IPAddress> KnownAddresses(IEnumerable<string> exceptHosts)
    {
        var except = new HashSet<string>(exceptHosts, StringComparer.OrdinalIgnoreCase);
        lock (CacheGate)
        {
            return ResolveCache.Where(item => !except.Contains(item.Key)).Select(item => item.Value.Address).Distinct().ToList();
        }
    }

    private static HttpClient CreateDohClient() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        // The resolvers' own names can't be looked up while the kill switch drops DNS.
        ConnectCallback = ColituPinnedHosts.ConnectAsync
    })
    // A DNS answer is a few hundred bytes: a resolver (or anything in its place) cannot fill the memory.
    { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 64 * 1024 };

    /// <summary>Drops keep-alive DoH connections opened over a route that changed with the tunnel.</summary>
    public static void ResetConnections()
    {
        var old = Interlocked.Exchange(ref Doh, CreateDohClient());
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
    }

    /// <summary>
    /// Asks every resolver at once and takes the first real answer: one of them is often slow or
    /// blocked (Russia), and asking them in turn cost up to 5 s before the next was even tried.
    /// </summary>
    private static async Task<IPAddress?> ResolveViaDohAsync(string host, CancellationToken token)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var client = Doh;
        var pending = DohEndpoints.Select(endpoint => QueryDohAsync(client, endpoint, host, race.Token)).ToList();
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);
            if (await finished is { } address)
            {
                race.Cancel();
                return address;
            }
        }
        return null;
    }

    private static async Task<IPAddress?> QueryDohAsync(HttpClient client, string endpoint, string host, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?name={Uri.EscapeDataString(host)}&type=A");
            request.Headers.TryAddWithoutValidation("accept", "application/dns-json");
            using var response = await client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!document.RootElement.TryGetProperty("Answer", out var answers))
            {
                return null;
            }
            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 1
                    && answer.TryGetProperty("data", out var data)
                    && IPAddress.TryParse(data.GetString(), out var address)
                    && !IsPlaceholder(address))
                {
                    return address;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            Logging.SaveLog($"ColituNetwork.ResolveViaDoh {endpoint}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            // Another resolver answered first, or the connection attempt was cancelled.
        }
        return null;
    }

    private static async Task<IPAddress?> ResolveViaSystemAsync(string host, CancellationToken token)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token);
            return addresses.FirstOrDefault(address => !IsPlaceholder(address));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            Logging.SaveLog($"ColituNetwork.ResolveViaSystem {ColituLogPrivacy.ForLog(host)}: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>198.18.0.0/15 is the pool fake-IP DNS resolvers hand out; it is never a real server.</summary>
    internal static bool IsPlaceholder(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
    }

    /// <summary>
    /// The physical interface (Ethernet or Wi-Fi) that carries the internet
    /// connection: up, with an IPv4 default route, lowest metric. Virtual
    /// interfaces of other VPN clients, containers and VMs are skipped so the
    /// core never sends its traffic into another tunnel.
    /// </summary>
    public static string? PhysicalInterfaceName()
    {
        try
        {
            var routes = DefaultRoutes();
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown)
                .Select(adapter => adapter.Name)
                .ToHashSet(StringComparer.Ordinal);
            return routes
                .Where(route => up.Contains(route.Interface) && !IsVirtual(route.Interface))
                .OrderBy(route => route.Metric)
                .ThenBy(route => IsWireless(route.Interface) ? 1 : 0)
                .Select(route => route.Interface)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituNetwork.PhysicalInterfaceName", ex);
            return null;
        }
    }

    /// <summary>Bytes sent and received so far on the physical interface; -1 when unknown.</summary>
    public static long PhysicalBytes()
    {
        try
        {
            if (PhysicalInterfaceName() is not { } name)
            {
                return -1;
            }
            var statistics = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(adapter => adapter.Name == name)?.GetIPStatistics();
            return statistics == null ? -1 : statistics.BytesReceived + statistics.BytesSent;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// The kind of link that carries the internet, for the Adaptive Connect network key: wifi,
    /// ethernet, cellular (a WWAN modem or a PPP dial-up link) or other.
    /// </summary>
    public static string LinkKind()
    {
        try
        {
            if (PhysicalInterfaceName() is { } device)
            {
                return IsWireless(device) ? "wifi" : IsCellular(device) ? "cellular" : "ethernet";
            }
            // PPP and WWAN links count as virtual above (no SO_BINDTODEVICE pinning there).
            return DefaultRoutes().Any(route => IsCellular(route.Interface)) ? "cellular" : "other";
        }
        catch
        {
            return "other";
        }
    }

    private static bool IsCellular(string name) =>
        name.StartsWith("wwan", StringComparison.Ordinal) || name.StartsWith("ppp", StringComparison.Ordinal)
        || name.StartsWith("rmnet", StringComparison.Ordinal) || name.StartsWith("wwp", StringComparison.Ordinal);

    /// <summary>True once any interface has an IPv4 default route (the network is usable).</summary>
    public static bool HasDefaultRoute()
    {
        try
        {
            return DefaultRoutes().Count > 0;
        }
        catch
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
    }

    /// <summary>Name of another VPN client's interface that currently owns a default route, if any.</summary>
    public static string? CompetingVpnAdapter()
    {
        try
        {
            return DefaultRoutes()
                .Select(route => route.Interface)
                .Where(IsVirtual)
                // Colitu's own TUN interfaces (Xray and sing-box) are not "another VPN".
                .FirstOrDefault(name => !IsOwnTun(name));
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsOwnTun(string name) =>
        name.Contains("xray_tun", StringComparison.Ordinal) || name.Contains("singbox_tun", StringComparison.Ordinal);

    private static readonly string[] VirtualPrefixes =
    [
        "lo", "tun", "tap", "wg", "ppp", "utun", "docker", "br-", "veth", "virbr", "vmnet", "vboxnet",
        "zt", "tailscale", "nordlynx", "proton", "mullvad", "cni", "flannel", "kube", "lxc", "lxd", "podman", "ipsec", "vti", "gre", "sit"
    ];

    internal static bool IsVirtual(string name)
    {
        if (VirtualPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) || IsOwnTun(name))
        {
            return true;
        }
        // Physical devices have a "device" link in sysfs; virtual ones do not.
        try
        {
            return Directory.Exists("/sys/class/net") && !Directory.Exists($"/sys/class/net/{name}/device");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWireless(string name)
    {
        try
        {
            return Directory.Exists($"/sys/class/net/{name}/wireless");
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct DefaultRoute(string Interface, int Metric);

    /// <summary>IPv4 default routes from /proc/net/route (destination and mask 0).</summary>
    private static List<DefaultRoute> DefaultRoutes()
    {
        var result = new List<DefaultRoute>();
        const string table = "/proc/net/route";
        if (!File.Exists(table))
        {
            return result;
        }
        foreach (var line in File.ReadLines(table).Skip(1))
        {
            // Iface Destination Gateway Flags RefCnt Use Metric Mask MTU Window IRTT
            var cols = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 8 || cols[1] != "00000000" || cols[7] != "00000000")
            {
                continue;
            }
            var flags = Convert.ToInt32(cols[3], 16);
            if ((flags & 0x1) == 0)
            {
                continue; // RTF_UP
            }
            result.Add(new DefaultRoute(cols[0], int.TryParse(cols[6], out var metric) ? metric : int.MaxValue));
        }
        return result;
    }
}

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Known addresses of the few hosts the app itself talks to while the kill switch is armed: the
/// panel API and the DNS-over-HTTPS resolvers. The nftables kill switch lets through only known
/// addresses and the local network, so a lookup through systemd-resolved to a public upstream
/// resolver is dropped while it is engaged. The addresses are resolved before the rules go in
/// (and allowed by them), and <see cref="ConnectAsync"/> dials them directly. TLS still runs against the host
/// name (SNI and certificate validation are unchanged): only the socket connect is pinned.
/// </summary>
public static class ColituPinnedHosts
{
    private static readonly object Gate = new();

    // DoH resolvers: these anycast addresses are part of the service and their certificates name
    // the host, so they work without a lookup (the kill switch also permits them for the cores).
    private static readonly Dictionary<string, IReadOnlyList<IPAddress>> Pinned = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cloudflare-dns.com"] = Parse("1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"),
        ["dns.google"] = Parse("8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844"),
    };

    /// <summary>Time for one pinned address before the next one is tried (the last one gets the rest).</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Time to wait for the system resolver; it hangs while the kill switch is armed.</summary>
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SystemGraceAfterDoh = TimeSpan.FromSeconds(1);

    /// <summary>The pinned addresses of <paramref name="host"/>, IPv4 first; empty when unknown.</summary>
    public static IReadOnlyList<IPAddress> AddressesOf(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return [];
        }
        lock (Gate)
        {
            return Pinned.TryGetValue(host, out var addresses) ? addresses : [];
        }
    }

    /// <summary>Stores the addresses of <paramref name="host"/>; false (and the last good list kept) when none is usable.</summary>
    internal static bool Set(string host, IEnumerable<IPAddress?> addresses)
    {
        var ordered = Order(addresses);
        if (string.IsNullOrWhiteSpace(host) || ordered.Count == 0)
        {
            return false;
        }
        bool changed;
        lock (Gate)
        {
            changed = !Pinned.TryGetValue(host, out var old) || !old.SequenceEqual(ordered);
            Pinned[host] = ordered;
        }
        if (changed)
        {
            Save();
        }
        return true;
    }

    // ── Saved addresses ─────────────────────────────────────────────────────
    // While the nftables kill switch is engaged a lookup through a public upstream resolver is
    // dropped: a restart in that state had no address for the panel (Windows lost about 20 s to
    // resolver timeouts in the same case). The addresses are not secret, and TLS still checks the host name and pins.

    /// <summary>Saved addresses older than this are not loaded.</summary>
    internal static readonly TimeSpan SavedMaxAge = TimeSpan.FromDays(30);

    private static string? _storePath;

    /// <summary>
    /// Loads the addresses saved by an earlier run from <paramref name="path"/> (hosts already
    /// pinned in this run keep theirs) and saves every later change there. Call once at start-up.
    /// </summary>
    public static void LoadSaved(string path)
    {
        _storePath = path;
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            var saved = JsonSerializer.Deserialize<SavedHosts>(File.ReadAllText(path));
            if (saved?.Hosts == null || DateTimeOffset.UtcNow - saved.SavedAt > SavedMaxAge)
            {
                return;
            }
            var loaded = 0;
            foreach (var (host, list) in saved.Hosts)
            {
                var ordered = Order((list ?? []).Select(text => IPAddress.TryParse(text, out var ip) ? ip : null));
                if (string.IsNullOrWhiteSpace(host) || ordered.Count == 0 || IsBuiltIn(host))
                {
                    continue;
                }
                lock (Gate)
                {
                    if (!Pinned.ContainsKey(host))
                    {
                        Pinned[host] = ordered;
                        loaded++;
                    }
                }
            }
            Logging.SaveLog($"ColituPinnedHosts | {loaded} saved host address list(s) loaded");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logging.SaveLog($"ColituPinnedHosts | saved addresses unreadable: {ex.GetType().Name}");
        }
    }

    private static void Save()
    {
        var path = _storePath;
        if (path == null)
        {
            return;
        }
        try
        {
            Dictionary<string, List<string>> hosts;
            lock (Gate)
            {
                hosts = Pinned.Where(item => !IsBuiltIn(item.Key))
                    .ToDictionary(item => item.Key, item => item.Value.Select(ip => ip.ToString()).ToList(), StringComparer.OrdinalIgnoreCase);
            }
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new SavedHosts { SavedAt = DateTimeOffset.UtcNow, Hosts = hosts }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.SaveLog($"ColituPinnedHosts | addresses not saved: {ex.GetType().Name}");
        }
    }

    /// <summary>Tests only: forgets the store and every pin of <paramref name="host"/>.</summary>
    internal static void ResetForTest(string? host = null)
    {
        _storePath = null;
        if (host != null)
        {
            lock (Gate)
            {
                Pinned.Remove(host);
            }
        }
    }

    private static bool IsBuiltIn(string host) => host is "cloudflare-dns.com" or "dns.google";

    private sealed class SavedHosts
    {
        public DateTimeOffset SavedAt { get; set; }
        public Dictionary<string, List<string>>? Hosts { get; set; }
    }

    /// <summary>Tests only: pins addresses as given (loopback included).</summary>
    internal static void PinForTest(string host, IReadOnlyList<IPAddress> addresses)
    {
        lock (Gate)
        {
            Pinned[host] = addresses;
        }
    }

    /// <summary>Usable addresses, without duplicates or fake-IP placeholders, IPv4 before IPv6 (order kept otherwise).</summary>
    internal static IReadOnlyList<IPAddress> Order(IEnumerable<IPAddress?> addresses)
    {
        var usable = addresses
            .Where(address => address != null)
            .Select(address => address!.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Where(address => !IPAddress.Any.Equals(address) && !IPAddress.IPv6Any.Equals(address)
                && !IPAddress.IsLoopback(address) && !ColituNetwork.IsPlaceholder(address))
            .Distinct()
            .ToList();
        return usable.Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Concat(usable.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6))
            .ToList();
    }

    /// <summary>
    /// Where a connection to <paramref name="endpoint"/> is attempted, in order: the pinned
    /// addresses, then the host name itself (the default behaviour, for when they are stale).
    /// </summary>
    internal static IReadOnlyList<EndPoint> Candidates(DnsEndPoint endpoint)
    {
        if (IPAddress.TryParse(endpoint.Host, out _))
        {
            return [endpoint];
        }
        var candidates = AddressesOf(endpoint.Host)
            .Select(address => (EndPoint)new IPEndPoint(address, endpoint.Port))
            .ToList();
        candidates.Add(endpoint);
        return candidates;
    }

    /// <summary>
    /// Resolves <paramref name="hosts"/> (DoH first, then the system resolver for the other
    /// addresses) and keeps the result. Call it before the kill switch is armed; while it is armed
    /// the system resolver is blocked and the last good addresses are kept.
    /// </summary>
    public static Task RefreshAsync(IEnumerable<string?> hosts, CancellationToken token = default) =>
        // All at once: one after the other, a blocked resolver cost 4 s per host (12 s for three).
        Task.WhenAll(hosts.Where(item => !string.IsNullOrWhiteSpace(item) && !IPAddress.TryParse(item, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(host => RefreshOneAsync(host, token)));

    private static async Task RefreshOneAsync(string host, CancellationToken token)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(ResolveBudget);
            var system = ResolveViaSystemAsync(host, budget.Token);
            var doh = await ColituNetwork.ResolveServerAsync(host, budget.Token);
            if (doh != null)
            {
                // DoH answered: the system resolver gets a moment more for the other addresses.
                await Task.WhenAny(system, Task.Delay(SystemGraceAfterDoh, budget.Token));
            }
            else
            {
                await Task.WhenAny(system, Task.Delay(System.Threading.Timeout.Infinite, budget.Token));
            }
            var others = system.IsCompletedSuccessfully ? system.Result : [];
            budget.Cancel();
            if (Set(host, [doh, .. others]))
            {
                Logging.SaveLog($"ColituPinnedHosts | {ColituLogPrivacy.ForLog(host)} pinned to {ColituLogPrivacy.ForLog(AddressesOf(host))}");
            }
            else
            {
                Logging.SaveLog($"ColituPinnedHosts | {ColituLogPrivacy.ForLog(host)} could not be resolved; keeping {(AddressesOf(host).Count > 0 ? ColituLogPrivacy.ForLog(AddressesOf(host)) : "no addresses")}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ColituPinnedHosts | {ColituLogPrivacy.ForLog(host)}: {ex.GetType().Name}");
        }
    }

    private static async Task<IPAddress[]> ResolveViaSystemAsync(string host, CancellationToken token)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, token);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectCallback"/>: connects to the pinned addresses of the
    /// request's host (IPv4 first), then to the host name as the default handler would.
    /// </summary>
    public static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token) =>
        ConnectToAsync(context.DnsEndPoint, token);

    internal static async ValueTask<Stream> ConnectToAsync(DnsEndPoint endpoint, CancellationToken token)
    {
        var candidates = Candidates(endpoint);
        var pinnedCount = candidates.Count(candidate => candidate is IPEndPoint);
        Exception? last = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var target = candidates[i];
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (target is IPEndPoint && i < pinnedCount - 1)
            {
                attempt.CancelAfter(AttemptTimeout);
            }
            var socket = target is IPEndPoint ip
                ? new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                : new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            try
            {
                await socket.ConnectAsync(target, attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static IReadOnlyList<IPAddress> Parse(params string[] addresses) => addresses.Select(IPAddress.Parse).ToList();
}

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Adaptive Connect 4 around the model (<see cref="ColituLm"/>): the rollout switch, the candidates
/// built from the server list, and the server order a decision gives. Pure logic, no I/O.
/// </summary>
public static class ColituLmPlanner
{
    /// <summary><c>features.ac_engine_4</c> of <c>/servers</c>; null when absent or not a boolean.</summary>
    internal static bool? ParseFeature(IReadOnlyDictionary<string, JsonElement>? features) =>
        features != null && features.TryGetValue("ac_engine_4", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True
            : null;

    /// <summary>
    /// Entry and exit country of a list entry. A multihop route: its two ends. A bridge node
    /// ("Finlandiya (Köprü)"): its entry address is in Russia, its country is the exit's. Anything
    /// else: its own country twice. Upper case; empty when unknown.
    /// </summary>
    public static (string Entry, string Exit) Countries(ColituVpnServer server)
    {
        var own = Upper(server.CountryCode);
        if (server.IsMultihop)
        {
            var exit = Upper(server.Exit?.Country);
            return (Upper(server.Entry?.Country), exit.Length > 0 ? exit : own);
        }
        if (own != "RU" && own.Length > 0 && (ColituServerNames.IsBridgeName(server.Name) || ColituServerNames.IsBridgeName(server.DisplayName)))
        {
            return ("RU", own);
        }
        return (own, own);
    }

    /// <summary>
    /// A bridge: a node named with the bridge suffix ("Finlandiya (Köprü)", "… (Мост)", "… (Bridge)"),
    /// or a multihop route that enters in the device's network country and leaves elsewhere.
    /// </summary>
    public static bool IsBridge(ColituVpnServer server, string? networkCountry = null)
    {
        var (entry, exit) = Countries(server);
        if (server.IsMultihop)
        {
            var country = Upper(networkCountry);
            return country.Length == 2 && entry == country && exit.Length > 0 && exit != entry;
        }
        return entry.Length > 0 && exit.Length > 0 && entry != exit;
    }

    /// <summary>Usable as an automatic target at all (online, not locked, not a CSL alpha node).</summary>
    private static bool Usable(ColituVpnServer server) =>
        !string.IsNullOrWhiteSpace(server.Id) && server.Available && !server.Locked && !server.Alpha;

    /// <summary>
    /// A multihop route that joins the whitelist candidates: flagged whitelist_ok and a bridge from the
    /// network country. Routes are never candidates outside whitelist mode.
    /// </summary>
    public static bool WhitelistRoute(ColituVpnServer server, string? networkCountry) =>
        server.IsMultihop && server.WhitelistOk && IsBridge(server, networkCountry);

    /// <summary>Its traffic leaves in Russia (a plain RU server, or a route exiting there): never chosen automatically.</summary>
    public static bool ExitsInRussia(ColituVpnServer server) => Countries(server).Exit == "RU";

    private static string Upper(string? value) => (value ?? "").Trim().ToUpperInvariant();

    /// <summary>
    /// The model's candidates: every usable server × each protocol it lists (one candidate with no
    /// protocol when it lists none, which uses the server-level prior). Multihop routes join only in
    /// whitelist mode, and only bridge routes flagged whitelist_ok (<see cref="WhitelistRoute"/>).
    /// </summary>
    public static List<(ColituLmCandidate Candidate, ColituVpnServer Server)> Candidates(
        IEnumerable<ColituVpnServer> servers,
        Func<ColituVpnServer, int?> latencyMs,
        IReadOnlyCollection<string>? hintedServers,
        Func<string, string, List<ColituLmOutcome>> history,
        bool whitelistMode = false,
        string? networkCountry = null)
    {
        var result = new List<(ColituLmCandidate, ColituVpnServer)>();
        foreach (var server in servers
            .Where(server => Usable(server) && (!server.IsMultihop || (whitelistMode && WhitelistRoute(server, networkCountry))))
            .GroupBy(server => server.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            var (entry, exit) = Countries(server);
            var hinted = hintedServers?.Contains(server.Id!, StringComparer.OrdinalIgnoreCase) == true;
            var ms = latencyMs(server);
            var protocols = server.Protocols.Count > 0 ? server.Protocols : [""];
            foreach (var protocol in protocols)
            {
                result.Add((new ColituLmCandidate
                {
                    NodeId = server.Id!,
                    Protocol = protocol,
                    EntryCountry = entry,
                    ExitCountry = exit,
                    WhitelistOk = server.WhitelistOk,
                    Recommended = server.Recommended,
                    HintBlocked = hinted,
                    Load = server.LoadLevel,
                    LatencyMs = ms,
                    History = history(server.Id!, protocol)
                }, server));
            }
        }
        return result;
    }

    /// <summary>
    /// The model when it was built for the device's network country, otherwise null (AC 3 rules until
    /// the right one comes): another country's priors would miss and every server would score alike.
    /// </summary>
    public static ColituLmModel? ModelFor(ColituLmModel? model, string? networkCountry) =>
        model != null && string.Equals(Upper(model.Country), Upper(networkCountry), StringComparison.Ordinal) ? model : null;

    /// <summary>
    /// The servers in the order a decision tries them: the chosen candidate's server first, then the
    /// ranking's servers (each once). <c>Lead</c> is the chosen protocol (null when the server lists none).
    /// </summary>
    public static (List<ColituVpnServer> Servers, string? Lead) Order(ColituLmDecision decision, IReadOnlyList<(ColituLmCandidate Candidate, ColituVpnServer Server)> pairs)
    {
        var servers = new List<ColituVpnServer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexes = (decision.Choice is { } choice ? new[] { choice } : Array.Empty<int>()).Concat(decision.Ranking ?? []);
        foreach (var index in indexes)
        {
            if (index >= 0 && index < pairs.Count && seen.Add(pairs[index].Server.Id!))
            {
                servers.Add(pairs[index].Server);
            }
        }
        var lead = decision.Choice is { } chosen && chosen >= 0 && chosen < pairs.Count && pairs[chosen].Candidate.Protocol.Length > 0
            ? pairs[chosen].Candidate.Protocol
            : null;
        return (servers, lead);
    }

    /// <summary>
    /// Whitelist mode while no model is there yet (AC 3 rules): the whitelist_ok servers only, bridges
    /// first, then by ping. Empty when none is flagged (the usual order then stays).
    /// </summary>
    public static List<ColituVpnServer> WhitelistOrder(IEnumerable<ColituVpnServer> servers, Func<ColituVpnServer, int?> latencyMs, string? networkCountry = null) =>
        servers
            .Where(server => server.WhitelistOk && Usable(server) && (!server.IsMultihop || WhitelistRoute(server, networkCountry)))
            .Select((server, index) => (Server: server, Index: index))
            .OrderBy(item => IsBridge(item.Server, networkCountry) ? 0 : 1)
            .ThenBy(item => latencyMs(item.Server) ?? int.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Server)
            .ToList();
}

/// <summary>
/// Servers located in Russia (owner, 2026-11-02): listed only when the app speaks Russian or the
/// device's network is in Russia. A plain RU server is never chosen automatically (that rule lives
/// in <see cref="ColituAdaptiveConnect.AutoExcluded"/> and in the model); bridges are not RU servers.
/// </summary>
public static class ColituRuServers
{
    public static bool Visible(ColituVpnServer server, string? appLanguage, string? networkCountry) =>
        !string.Equals(server.CountryCode?.Trim(), "RU", StringComparison.OrdinalIgnoreCase)
        || string.Equals(appLanguage, "ru", StringComparison.OrdinalIgnoreCase)
        || string.Equals(networkCountry?.Trim(), "RU", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// This device's own outcomes per (network, server, protocol), for the model's own feature and Beta
/// posterior. The network is kept only as a hash of the Adaptive Connect network key; the file is
/// local and never sent. Old outcomes fade (half-life 72 h) and are dropped after 21 days.
/// Thread-safe; without a path (tests) it lives in memory only.
/// </summary>
public sealed class ColituLmHistory
{
    public const int MaxPerKey = 24;
    public const int MaxRecords = 3000;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(21);

    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _byKey = new(StringComparer.Ordinal);

    public sealed class Entry
    {
        [JsonPropertyName("n")] public string Network { get; set; } = "";
        [JsonPropertyName("s")] public string Server { get; set; } = "";
        [JsonPropertyName("p")] public string Protocol { get; set; } = "";
        [JsonPropertyName("t")] public long At { get; set; }
        [JsonPropertyName("ok")] public bool Ok { get; set; }
    }

    public ColituLmHistory(string? path)
    {
        _path = path;
        try
        {
            if (path != null && File.Exists(path))
            {
                foreach (var record in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [])
                {
                    if (record?.Network is { Length: > 0 } && record.Server is { Length: > 0 } && record.Protocol != null)
                    {
                        List(Key(record.Network, record.Server, record.Protocol)).Add(record);
                    }
                }
                Prune(DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituLmHistory.Load", ex);
        }
    }

    /// <summary>The network key as stored: 16 hex characters of its SHA-256.</summary>
    public static string NetworkHash(string? networkKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(networkKey ?? ""))).Substring(0, 16).ToLowerInvariant();

    private static string Key(string networkHash, string server, string protocol) =>
        $"{networkHash}|{server.Trim().ToLowerInvariant()}|{protocol.Trim().ToLowerInvariant()}";

    private List<Entry> List(string key)
    {
        if (!_byKey.TryGetValue(key, out var list))
        {
            _byKey[key] = list = [];
        }
        return list;
    }

    public void Record(string networkKey, string? server, string? protocol, bool ok, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(server) || protocol == null)
        {
            return;
        }
        var hash = NetworkHash(networkKey);
        lock (_gate)
        {
            List(Key(hash, server, protocol)).Add(new Entry
            {
                Network = hash,
                Server = server.Trim().ToLowerInvariant(),
                Protocol = protocol.Trim().ToLowerInvariant(),
                At = now.ToUnixTimeSeconds(),
                Ok = ok
            });
            Prune(now);
            Save();
        }
    }

    /// <summary>The outcomes on this network with the server and protocol, ages in hours.</summary>
    public List<ColituLmOutcome> Outcomes(string networkKey, string? server, string? protocol, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(server) || protocol == null)
        {
            return [];
        }
        lock (_gate)
        {
            if (!_byKey.TryGetValue(Key(NetworkHash(networkKey), server, protocol), out var list))
            {
                return [];
            }
            var seconds = now.ToUnixTimeSeconds();
            return list.Select(record => new ColituLmOutcome { AgeH = Math.Max(0, seconds - record.At) / 3600.0, Ok = record.Ok }).ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byKey.Values.Sum(list => list.Count);
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var oldest = (now - MaxAge).ToUnixTimeSeconds();
        var newest = (now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds();
        foreach (var key in _byKey.Keys.ToList())
        {
            var list = _byKey[key];
            list.RemoveAll(record => record.At < oldest || record.At > newest);
            if (list.Count > MaxPerKey)
            {
                list.RemoveRange(0, list.Count - MaxPerKey);
            }
            if (list.Count == 0)
            {
                _byKey.Remove(key);
            }
        }
        var total = _byKey.Values.Sum(list => list.Count);
        if (total > MaxRecords)
        {
            // The oldest outcomes go first.
            var cut = _byKey.Values.SelectMany(list => list).OrderBy(record => record.At).Skip(total - MaxRecords).First().At;
            foreach (var key in _byKey.Keys.ToList())
            {
                _byKey[key].RemoveAll(record => record.At < cut);
                if (_byKey[key].Count == 0)
                {
                    _byKey.Remove(key);
                }
            }
        }
    }

    private void Save()
    {
        if (_path == null)
        {
            return;
        }
        var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_byKey.Values.SelectMany(list => list).ToList()));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituLmHistory.Save", ex);
            try { File.Delete(temp); } catch { }
        }
    }
}

/// <summary>
/// The last <c>/client/ac-model</c> answer and when it came. Refreshed every hour (the panel rebuilds
/// the priors hourly); the last one is still used offline while a refetch is tried, at most every
/// 10 minutes. Kept on disk across restarts. The decision's "stale" mark stays at 6 h (shared vectors).
/// </summary>
public sealed class ColituLmModelStore
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(10);

    private readonly string? _path;
    private readonly object _gate = new();
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    private sealed class Saved
    {
        [JsonPropertyName("fetched_at")] public DateTimeOffset FetchedAt { get; set; }
        [JsonPropertyName("model")] public ColituLmModel? Model { get; set; }
    }

    public ColituLmModelStore(string? path)
    {
        _path = path;
        try
        {
            if (path != null && File.Exists(path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is { Model: not null } saved)
            {
                Model = saved.Model;
                FetchedAt = saved.FetchedAt;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituLmModelStore.Load", ex);
        }
    }

    public ColituLmModel? Model { get; private set; }
    public DateTimeOffset? FetchedAt { get; private set; }

    /// <summary>Hours since the model was fetched; 0 without a model.</summary>
    public double AgeH(DateTimeOffset now) => FetchedAt is { } at ? Math.Max(0, (now - at).TotalHours) : 0;

    /// <summary>
    /// A fetch is due: no model, a stale one, or one of another country than the device's network
    /// now; never more often than <see cref="RetryEvery"/>. Marks the attempt when it says yes.
    /// </summary>
    public bool TakeFetchSlot(DateTimeOffset now, string? country)
    {
        lock (_gate)
        {
            var due = Model == null || FetchedAt is not { } at || now - at >= FreshFor || at > now + TimeSpan.FromMinutes(5)
                || (country is { Length: 2 } && Model.Country is { Length: 2 } modelCountry && !string.Equals(modelCountry, country, StringComparison.OrdinalIgnoreCase));
            if (!due || now - _lastAttempt < RetryEvery)
            {
                return false;
            }
            _lastAttempt = now;
            return true;
        }
    }

    public void Set(ColituLmModel model, DateTimeOffset now)
    {
        lock (_gate)
        {
            Model = model;
            FetchedAt = now;
            if (_path == null)
            {
                return;
            }
            var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(new Saved { FetchedAt = now, Model = model }));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ColituLmModelStore.Save", ex);
                try { File.Delete(temp); } catch { }
            }
        }
    }
}

/// <summary>A host and TCP port the whitelist check dials.</summary>
public sealed record ColituWhitelistTarget(string Host, int Port);

/// <summary>One whitelist check: how many hosts of each set answered on TCP 443, and how long it took.</summary>
public sealed record ColituWhitelistCheck(int ForeignOk, int ForeignTotal, int DomesticOk, int DomesticTotal, int Ms)
{
    /// <summary>The detection rule: foreign ≤ 1 of 5 and domestic ≥ 2 of 4.</summary>
    public bool Whitelist => ColituWhitelist.IsWhitelist(ForeignOk, DomesticOk);

    /// <summary>"on:f1/5:d3/4" as the panel stores it.</summary>
    public string Flag(bool on) => $"{(on ? "on" : "off")}:f{ForeignOk}/{ForeignTotal}:d{DomesticOk}/{DomesticTotal}";
}

/// <summary>
/// Whitelist mode (mobile operators that let only allowed addresses through): detection with the
/// VPN off and after two connection failures in a row, a re-check every 5 minutes while it holds.
/// </summary>
public static class ColituWhitelist
{
    public const int ForeignMaxOk = 1;
    public const int DomesticMinOk = 2;
    public const int Port = 443;
    public const int MaxHosts = 8;
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(5);

    /// <summary>Neutral foreign hosts of the foreign set (with two Colitu nodes: five in all).</summary>
    public static readonly string[] NeutralForeignHosts = ["www.google.com", "www.wikipedia.org", "www.cloudflare.com"];

    /// <summary>Used while the panel sends no <c>whitelist_probe</c> (same as the panel's default).</summary>
    public static readonly IReadOnlyDictionary<string, List<string>> DefaultProbe =
        new Dictionary<string, List<string>> { ["RU"] = ["ya.ru", "vk.com", "gosuslugi.ru", "mail.ru"] };

    public static bool IsWhitelist(int foreignOk, int domesticOk) => foreignOk <= ForeignMaxOk && domesticOk >= DomesticMinOk;

    /// <summary>
    /// The mode after a check: it starts when the rule holds and ends only when the foreign set works
    /// again (nothing answering at all is no network, not the end of the whitelist).
    /// </summary>
    public static bool NextState(bool current, ColituWhitelistCheck check) =>
        current ? check.ForeignOk <= ForeignMaxOk : check.Whitelist;

    /// <summary><c>whitelist_probe</c>: country → hosts; bad countries and host names are dropped (at most 20 countries, 8 hosts each).</summary>
    public static Dictionary<string, List<string>> ParseProbe(JsonElement? raw)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (raw is not { ValueKind: JsonValueKind.Object } value)
        {
            return result;
        }
        foreach (var property in value.EnumerateObject().Take(20))
        {
            var country = property.Name.Trim().ToUpperInvariant();
            if (country.Length != 2 || !country.All(c => c is >= 'A' and <= 'Z') || property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            var hosts = property.Value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim().ToLowerInvariant())
                .Where(ValidHost)
                .Distinct()
                .Take(MaxHosts)
                .ToList();
            if (hosts.Count > 0)
            {
                result[country] = hosts;
            }
        }
        return result;
    }

    internal static bool ValidHost(string host) =>
        host.Length is > 0 and <= 253 && host.Contains('.') && !host.StartsWith('.') && !host.EndsWith('.') && !host.StartsWith('-')
        && host.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    /// <summary>The domestic set of <paramref name="country"/>: the panel's list, else the default; empty when none.</summary>
    public static List<string> DomesticSet(IReadOnlyDictionary<string, List<string>>? probe, string? country)
    {
        var cc = country?.Trim().ToUpperInvariant() ?? "";
        if (cc.Length != 2)
        {
            return [];
        }
        var source = probe is { Count: > 0 } ? probe : DefaultProbe;
        return source.TryGetValue(cc, out var hosts) ? hosts.Take(MaxHosts).ToList() : [];
    }

    /// <summary>
    /// The foreign set: the neutral hosts on TCP 443 plus two Colitu nodes outside Russia that are not
    /// whitelist servers or bridges, on their own ping address (latency_host:latency_port, as in the
    /// ping round), the fastest first.
    /// </summary>
    public static List<ColituWhitelistTarget> ForeignSet(IEnumerable<ColituVpnServer> servers, Func<ColituVpnServer, int?> ping)
    {
        var nodes = servers
            .Where(server => !server.IsMultihop && !server.WhitelistOk && ColituLatency.CanProbe(server)
                && !ColituLmPlanner.ExitsInRussia(server) && !ColituLmPlanner.IsBridge(server))
            .Select((server, index) => (Server: server, Index: index))
            .OrderBy(item => ping(item.Server) ?? int.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => new ColituWhitelistTarget(item.Server.Host!.Trim(), item.Server.Port!.Value))
            .DistinctBy(target => target.Host.ToLowerInvariant())
            .Take(2);
        return NeutralForeignHosts.Select(host => new ColituWhitelistTarget(host, Port)).Concat(nodes).ToList();
    }

    /// <summary>
    /// With a <c>beforeProbe</c> hook (the Linux kill switch is engaged): the names may need the DoH head
    /// start plus the adapter's resolvers (1.5 s + 3 s), and the whole check stays within 6 s.
    /// </summary>
    public static readonly TimeSpan GuardedResolveBudget = TimeSpan.FromMilliseconds(4500);
    public static readonly TimeSpan GuardedBudget = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan HookBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// One check: every target of the foreign set (its own port) and every domestic host (TCP 443) in
    /// parallel, within <see cref="Budget"/>. First every name is resolved, then
    /// <paramref name="beforeProbe"/> (if any) gets the resolved addresses once (the Linux kill switch
    /// lets the probes out to exactly them), then the probes run. Null when
    /// <paramref name="requirePin"/> is set (the VPN is up) and the sockets cannot be pinned to the
    /// physical interface: the check would measure the tunnel.
    /// </summary>
    public static Task<ColituWhitelistCheck?> CheckAsync(IReadOnlyList<ColituWhitelistTarget> foreign, IReadOnlyList<string> domestic, string? device, bool requirePin,
        CancellationToken token = default, Func<IReadOnlyList<IPAddress>, Task>? beforeProbe = null) =>
        CheckAsync(foreign, domestic, device, requirePin, ColituNetwork.ResolveServerAsync,
            (address, port, timeoutMs) => ColituLatency.ProbeAsync(address, port, device, requirePin, timeoutMs), beforeProbe, token);

    internal static async Task<ColituWhitelistCheck?> CheckAsync(IReadOnlyList<ColituWhitelistTarget> foreign, IReadOnlyList<string> domestic, string? device, bool requirePin,
        Func<string, CancellationToken, Task<IPAddress?>> resolve, Func<IPAddress, int, int, Task<(int? Ms, bool Unpinned)>> probe,
        Func<IReadOnlyList<IPAddress>, Task>? beforeProbe, CancellationToken token)
    {
        if (requirePin && string.IsNullOrEmpty(device))
        {
            return null;
        }
        var budget = beforeProbe == null ? Budget : GuardedBudget;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(budget);
        using var resolveDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        resolveDeadline.CancelAfter(beforeProbe == null ? Budget : GuardedResolveBudget);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // 1. Resolve every name (public IPv4 only, as the probes dial).
        async Task<IPAddress?> Resolve(string host)
        {
            try
            {
                var address = await resolve(host, resolveDeadline.Token);
                return address != null && address.AddressFamily == AddressFamily.InterNetwork && ColituLatency.IsPublic(address) ? address : null;
            }
            catch
            {
                return null;
            }
        }

        var foreignTargets = foreign.Take(10).ToList();
        var domesticHosts = domestic.Take(10).ToList();
        var foreignNames = foreignTargets.Select(target => Resolve(target.Host)).ToList();
        var domesticNames = domesticHosts.Select(Resolve).ToList();
        await Task.WhenAll(foreignNames.Concat(domesticNames));
        var foreignAddresses = foreignNames.Select(task => task.Result).ToList();
        var domesticAddresses = domesticNames.Select(task => task.Result).ToList();

        // 2. The hook, once, before any probe; it never fails the check.
        var resolved = foreignAddresses.Concat(domesticAddresses).OfType<IPAddress>().Distinct().ToList();
        if (beforeProbe != null && resolved.Count > 0)
        {
            try
            {
                await beforeProbe(resolved).WaitAsync(HookBudget, deadline.Token);
            }
            catch
            {
                // The probes run anyway; blocked ones simply fail.
            }
        }

        // 3. The probes, in what is left of the budget.
        var unpinned = 0;
        async Task<bool> Reachable(IPAddress? address, int port)
        {
            if (address == null)
            {
                return false;
            }
            var left = (int)(budget - watch.Elapsed).TotalMilliseconds;
            if (left <= 50)
            {
                return false;
            }
            try
            {
                var (ms, notPinned) = await probe(address, port, left);
                if (notPinned)
                {
                    Interlocked.Increment(ref unpinned);
                }
                return ms != null;
            }
            catch
            {
                return false;
            }
        }

        var foreignTasks = foreignTargets.Select((target, index) => Reachable(foreignAddresses[index], target.Port)).ToList();
        var domesticTasks = domesticAddresses.Select(address => Reachable(address, Port)).ToList();
        var foreignResults = await Task.WhenAll(foreignTasks);
        var domesticResults = await Task.WhenAll(domesticTasks);
        if (unpinned > 0)
        {
            return null;
        }
        return new ColituWhitelistCheck(foreignResults.Count(ok => ok), foreignResults.Length, domesticResults.Count(ok => ok), domesticResults.Length,
            (int)Math.Min(watch.ElapsedMilliseconds, ColituAcSpec.MaxMs));
    }
}

/// <summary>Why a server failed in a connect attempt; only <see cref="Server"/> blames the network or the server.</summary>
public enum ColituWhitelistFailure
{
    Server,
    Auth,
    Quota,
    Crash,
    NetworkChange,
    NoNetwork,
    Cancel
}

/// <summary>
/// The whitelist trigger (owner 2026-11-17, WHITELIST-TRIGGER-SPEC): the distinct servers that failed in
/// connect attempts on the current network, with their entry country. Three servers in two countries make
/// a whitelist check due (T1); a ping round where no foreign server answered does too (T2). At most one
/// such check per network a minute; after a "not whitelist" result one more server must fail. Pure, no I/O.
/// </summary>
public sealed class ColituWhitelistLedger
{
    public const int MinServers = 3;
    public const int MinCountries = 2;
    public const int MinPingServers = 4;
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _servers = new(StringComparer.OrdinalIgnoreCase);
    private string? _network;
    private int _countAtLastCheck;
    private DateTimeOffset? _lastCheck;

    /// <summary>Distinct failed servers on the current network.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _servers.Count;
            }
        }
    }

    /// <summary>A server failed in a connect on <paramref name="network"/>; true when it was added (a new server, a failure that counts).</summary>
    public bool Note(string network, string? serverId, string? country, ColituWhitelistFailure kind)
    {
        lock (_gate)
        {
            Track(network);
            if (kind != ColituWhitelistFailure.Server || string.IsNullOrWhiteSpace(serverId) || _servers.ContainsKey(serverId))
            {
                return false;
            }
            _servers[serverId] = (country ?? "").Trim().ToUpperInvariant();
            return true;
        }
    }

    /// <summary>T1: at least three distinct servers in at least two countries, and a new one since the last "not whitelist" check.</summary>
    public bool ServersDue(string network)
    {
        lock (_gate)
        {
            Track(network);
            return _servers.Count >= MinServers && _servers.Count > _countAtLastCheck
                && _servers.Values.Where(country => country.Length > 0).Distinct().Count() >= MinCountries;
        }
    }

    /// <summary>The 60 s debounce of T1/T2 per network: true (and the slot taken) when a check may run now.</summary>
    public bool TryTakeCheck(string network, DateTimeOffset now)
    {
        lock (_gate)
        {
            Track(network);
            if (_lastCheck is { } last && now - last < Debounce && now >= last)
            {
                return false;
            }
            _lastCheck = now;
            return true;
        }
    }

    /// <summary>A check ended: whitelist mode clears the ledger, "not whitelist" needs one more server for T1.</summary>
    public void CheckDone(string network, bool whitelist)
    {
        lock (_gate)
        {
            Track(network);
            if (whitelist)
            {
                Clear();
            }
            else
            {
                _countAtLastCheck = _servers.Count;
            }
        }
    }

    /// <summary>A connect succeeded (traffic verified).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Clear();
        }
    }

    private void Track(string network)
    {
        if (!string.Equals(_network, network, StringComparison.Ordinal))
        {
            _network = network;
            Clear();
            _lastCheck = null;
        }
    }

    private void Clear()
    {
        _servers.Clear();
        _countAtLastCheck = 0;
    }

    /// <summary>
    /// The kind of a server failure in a connect: the same exclusions as the connect-failure counter
    /// (account, plan or quota, a paused device, no internet, cancel), plus refused credentials, a core
    /// that did not start and a network that changed during the attempt. Only "no transport carried
    /// traffic" (<see cref="ColituConnectException"/>, not offline) blames the server or the network.
    /// </summary>
    public static ColituWhitelistFailure Classify(Exception ex, bool credentialsRefused, bool networkChanged, bool hasNetwork)
    {
        if (ex is OperationCanceledException)
        {
            return ColituWhitelistFailure.Cancel;
        }
        if (ex is ColituPlanRequiredException || ColituUpsell.IsQuotaError(ex)
            || ex is ColituApiException { ErrorCode: "ENTITLEMENT_INACTIVE" or "ENTITLEMENT_EXPIRED" })
        {
            return ColituWhitelistFailure.Quota;
        }
        if (credentialsRefused || ex is ColituDevicePausedException or ColituApiException)
        {
            return ColituWhitelistFailure.Auth;
        }
        if (networkChanged)
        {
            return ColituWhitelistFailure.NetworkChange;
        }
        if (!hasNetwork || ex is ColituConnectException { Offline: true })
        {
            return ColituWhitelistFailure.NoNetwork;
        }
        return ex is ColituConnectException ? ColituWhitelistFailure.Server : ColituWhitelistFailure.Crash;
    }

    /// <summary>
    /// T2: a ping round with the VPN off where at least four servers outside Russia (entry country; no
    /// multihop routes) were pinged, none answered, and they span at least two countries.
    /// </summary>
    public static bool PingsDue(IEnumerable<ColituVpnServer> servers, IReadOnlyDictionary<string, int?> dialled)
    {
        var pinged = servers
            .Where(server => !server.IsMultihop && server.Id is { Length: > 0 } && dialled.ContainsKey(server.Id))
            .Select(server => (server.Id!, Country: ColituLmPlanner.Countries(server).Entry))
            .Where(item => item.Country != "RU")
            .DistinctBy(item => item.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return pinged.Count >= MinPingServers
            && pinged.All(item => dialled[item.Item1] == null)
            && pinged.Select(item => item.Country).Where(country => country.Length > 0).Distinct().Count() >= MinCountries;
    }
}

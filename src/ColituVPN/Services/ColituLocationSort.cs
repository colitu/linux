namespace v2rayN.Desktop.Services;

/// <summary>
/// Order of the locations list, as on Android and iOS: by ping, by name or by load. The list
/// starts from <see cref="ColituServerGroups.Build"/> (by country name, then city); this only
/// reorders it. Countries with several cities count with their best city; servers without a
/// measurement (or without a load) go last, in name order. The "best server" row is not part of
/// the groups and stays on top.
/// </summary>
public static class ColituLocationSort
{
    public const string Ping = "ping";
    public const string Name = "name";
    public const string Load = "load";

    public static readonly string[] Modes = [Ping, Name, Load];

    /// <summary>A known mode; anything else (an older saved state) sorts by name, the previous order.</summary>
    public static string Normalize(string? mode) =>
        Modes.FirstOrDefault(known => string.Equals(known, mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Name;

    public static List<ColituServerGroup> Order(IReadOnlyList<ColituServerGroup> groups, string? mode, Func<ColituVpnServer, int?> ping)
    {
        Func<ColituVpnServer, double?>? metric = Normalize(mode) switch
        {
            Ping => server => ping(server) is > 0 and var ms ? ms : null,
            Load => server => server.Load is >= 0 and var load ? load : null,
            _ => null
        };
        if (metric == null)
        {
            return groups.Select(RecommendedFirst).ToList();
        }

        // OrderBy is stable: equal (or missing) values keep the name order of Build.
        return groups
            .Select(group => RecommendedFirst(new ColituServerGroup
            {
                Key = group.Key,
                Name = group.Name,
                CountryCode = group.CountryCode,
                Servers = group.Servers.OrderBy(server => metric(server) is null).ThenBy(server => metric(server) ?? 0).ToList()
            }))
            .OrderBy(group => Best(group, metric) is null)
            .ThenBy(group => Best(group, metric) ?? 0)
            .ToList();
    }

    /// <summary>Owner rule: for clients in Russia the whitelist server ("Beyaz Liste", whitelist_ok with the vless-ws protocol) sits at the top of the list.</summary>
    public static bool IsWhitelistServer(ColituVpnServer server) =>
        server.WhitelistOk && server.Protocols.Any(ColituApiClient.IsWhitelistProtocol);

    /// <summary>
    /// The servers that get an extra "Beyaz Liste" row: only for a client whose last known country is RU,
    /// each server flagged whitelist_ok that lists vless-ws. The normal row of the server stays in its
    /// country group; other countries (and unknown) get no virtual row. Display only.
    /// </summary>
    /// <summary>Words that find every "Beyaz Liste" row in the search, whatever the app language.</summary>
    public static readonly string[] WhitelistSearchWords = ["beyaz liste", "whitelist", "белый список", "白名单", "لیست سفید"];

    public static List<ColituVpnServer> WhitelistRows(IEnumerable<ColituVpnServer> servers, string? clientCountry) =>
        string.Equals(clientCountry?.Trim(), "RU", StringComparison.OrdinalIgnoreCase)
            ? servers.Where(server => !server.IsMultihop && server.Available && IsWhitelistServer(server)).ToList()
            : [];

    /// <summary>
    /// The panel's blocked_servers as they apply to ordinary rows and automatic choice. A whitelist row and
    /// whitelist mode dial the relay / CDN host, never the node's own address, so there the whitelist servers
    /// are not demoted by an address block.
    /// </summary>
    public static IReadOnlyCollection<string> AddressBlocked(IEnumerable<ColituVpnServer> servers, IReadOnlyCollection<string> blocked, bool whitelistMode)
    {
        if (blocked.Count == 0 || !whitelistMode)
        {
            return blocked;
        }
        var exempt = servers.Where(server => !server.IsMultihop && server.Available && IsWhitelistServer(server))
            .Select(server => server.Id).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return blocked.Where(id => !exempt.Contains(id)).ToList();
    }

    /// <summary>
    /// Ids the local reachability check (pings to the node's own address) never hides: in whitelist mode
    /// every whitelist server, which is reached through the relay / CDN host.
    /// </summary>
    public static List<string?> WhitelistKeepVisible(IEnumerable<ColituVpnServer> servers, bool whitelistMode) =>
        whitelistMode
            ? servers.Where(server => !server.IsMultihop && IsWhitelistServer(server)).Select(server => (string?)server.Id).ToList()
            : [];

    /// <summary>Selection key of the virtual row ("whitelist:&lt;node id&gt;"), distinct from the node's normal row.</summary>
    public static string WhitelistKey(string nodeId) => "whitelist:" + nodeId;

    public static string? WhitelistNodeOf(string? key) =>
        key != null && key.StartsWith("whitelist:", StringComparison.Ordinal) && key.Length > "whitelist:".Length ? key["whitelist:".Length..] : null;

    /// <summary>Servers the panel recommends come first inside their country; the rest keep their order (stable).</summary>
    internal static ColituServerGroup RecommendedFirst(ColituServerGroup group) =>
        group.IsSingle || !group.Servers.Any(server => server.Recommended)
            ? group
            : new ColituServerGroup
            {
                Key = group.Key,
                Name = group.Name,
                CountryCode = group.CountryCode,
                Servers = group.Servers.OrderBy(server => server.Recommended ? 0 : 1).ToList()
            };

    /// <summary>The group's best (lowest) value; null when none of its servers has one.</summary>
    private static double? Best(ColituServerGroup group, Func<ColituVpnServer, double?> metric)
    {
        double? best = null;
        foreach (var server in group.Servers)
        {
            if (metric(server) is { } value && (best == null || value < best))
            {
                best = value;
            }
        }
        return best;
    }
}

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
            return [.. groups];
        }

        // OrderBy is stable: equal (or missing) values keep the name order of Build.
        return groups
            .Select(group => new ColituServerGroup
            {
                Key = group.Key,
                Name = group.Name,
                CountryCode = group.CountryCode,
                Servers = group.Servers.OrderBy(server => metric(server) is null).ThenBy(server => metric(server) ?? 0).ToList()
            })
            .OrderBy(group => Best(group, metric) is null)
            .ThenBy(group => Best(group, metric) ?? 0)
            .ToList();
    }

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

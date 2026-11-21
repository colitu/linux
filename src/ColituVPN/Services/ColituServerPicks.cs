namespace v2rayN.Desktop.Services;

/// <summary>
/// Favourite and recently used servers (ids kept in the local preferences): the Favourites and
/// Recently used sections of Locations, the server shortcuts on Home and the tray menu.
/// Ids of servers the panel no longer lists are kept but never shown.
/// </summary>
public static class ColituServerPicks
{
    /// <summary>Recently used: the last three servers connected to.</summary>
    public const int MaxRecent = 3;

    public const int MaxFavorites = 50;

    /// <summary>Home and the tray offer at most three servers.</summary>
    public const int MaxShortcuts = 3;

    /// <summary>Trimmed, without empty and repeated ids (case-insensitive), at most <paramref name="max"/>.</summary>
    public static List<string> Normalize(IEnumerable<string?>? ids, int max)
    {
        var result = new List<string>();
        foreach (var id in ids ?? [])
        {
            var value = id?.Trim();
            if (string.IsNullOrEmpty(value) || result.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            result.Add(value);
            if (result.Count >= max)
            {
                break;
            }
        }
        return result;
    }

    /// <summary>The server just connected to goes first; the oldest drops off after three.</summary>
    public static List<string> AddRecent(IEnumerable<string?>? recent, string? id) =>
        string.IsNullOrWhiteSpace(id) ? Normalize(recent, MaxRecent) : Normalize(new[] { id }.Concat(recent ?? []), MaxRecent);

    /// <summary>Adds the server (first) or removes it; <paramref name="added"/> says which.</summary>
    public static List<string> ToggleFavorite(IEnumerable<string?>? favorites, string id, out bool added)
    {
        var list = Normalize(favorites, int.MaxValue);
        var index = list.FindIndex(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));
        added = index < 0;
        if (added)
        {
            list.Insert(0, id.Trim());
        }
        else
        {
            list.RemoveAt(index);
        }
        return Normalize(list, MaxFavorites);
    }

    public static bool Contains(IEnumerable<string>? ids, string? id) =>
        id is { Length: > 0 } && (ids ?? []).Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The ids that still name a server, in order, as the servers themselves (deleted ones are left out).</summary>
    public static List<T> Resolve<T>(IEnumerable<string>? ids, Func<string, T?> lookup) where T : class
    {
        var result = new List<T>();
        foreach (var id in Normalize(ids, int.MaxValue))
        {
            if (lookup(id) is { } item && !result.Contains(item))
            {
                result.Add(item);
            }
        }
        return result;
    }

    /// <summary>
    /// The servers Home and the tray offer: favourites first, then the recently used ones, each once,
    /// only those that can be picked (<paramref name="usable"/>: still listed and online), at most three.
    /// </summary>
    public static List<string> Shortcuts(IEnumerable<string>? favorites, IEnumerable<string>? recent, Func<string, bool> usable, int max = MaxShortcuts) =>
        Normalize((favorites ?? []).Concat(recent ?? []).Where(id => !string.IsNullOrWhiteSpace(id) && usable(id.Trim())), max);
}

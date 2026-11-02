namespace v2rayN.Desktop.Services;

/// <summary>
/// Adaptive Connect 4, phase 1 ("honest server list"): which servers the list and the automatic
/// choice may show, and which ping a row shows. Pure logic (no UI, no sockets) so it can be tested.
/// <list type="bullet">
/// <item>Only rounds measured with the VPN off decide reachability. A server is unreachable after
/// <see cref="FailedRoundsToHide"/> consecutive failed rounds (each round already retries once) and
/// reachable again after one successful measurement.</item>
/// <item>Safeguards: a round in which every server, or half of them or more, failed says the network
/// is the problem: it does not count, and while the last such round stands nothing is hidden. Nothing
/// is hidden either when half of the known servers or more would be. The servers in <c>keep</c> (the
/// connected one, the one being connected to) are never hidden; favourites are kept by the view, dimmed.</item>
/// <item>With the VPN on: a ping measured outside the tunnel is shown; otherwise the last one measured
/// with the VPN off, if it is at most <see cref="OffCacheFor"/> old; else one measured through the
/// tunnel (<see cref="RecordTunnel"/>, display only), if at most <see cref="OffCacheFor"/> old;
/// otherwise none ("— ms"). Nothing measured with the VPN on hides a server.</item>
/// </list>
/// </summary>
public sealed class ColituReachability
{
    public const int FailedRoundsToHide = 2;

    /// <summary>How long a ping measured with the VPN off stands in for one while connected.</summary>
    public static readonly TimeSpan OffCacheFor = TimeSpan.FromMinutes(30);

    private sealed class Entry
    {
        public int FailedRounds;
        public int? LiveMs;
        public DateTimeOffset LiveAt;
        public int? OffMs;
        public DateTimeOffset OffAt;
        /// <summary>Display only: measured through the tunnel; never part of the hiding decision.</summary>
        public int? TunnelMs;
        public DateTimeOffset TunnelAt;
        public int TunnelRounds;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _networkSuspect;

    /// <summary>
    /// Records one ping round. <paramref name="results"/>: every server the round actually dialled,
    /// with its best ping or null when it failed (servers not dialled are left out). <paramref name="vpnOff"/>:
    /// measured with the VPN off; only such rounds change reachability and the VPN-off cache.
    /// </summary>
    public void RecordRound(IReadOnlyDictionary<string, int?> results, bool vpnOff, DateTimeOffset at)
    {
        lock (_gate)
        {
            var failed = results.Count(result => result.Value is not > 0);
            var suspect = results.Count == 0 || failed * 2 >= results.Count;
            foreach (var (id, ms) in results)
            {
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                if (!_entries.TryGetValue(id, out var entry))
                {
                    entry = new Entry();
                    _entries[id] = entry;
                }
                entry.LiveMs = ms is > 0 ? ms : null;
                entry.LiveAt = at;
                if (!vpnOff)
                {
                    continue;
                }
                if (ms is > 0)
                {
                    entry.FailedRounds = 0;
                    entry.OffMs = ms;
                    entry.OffAt = at;
                }
                else if (!suspect)
                {
                    entry.FailedRounds++;
                }
            }
            if (vpnOff && results.Count > 0)
            {
                _networkSuspect = suspect;
            }
        }
    }

    /// <summary>
    /// Records pings measured through the tunnel ("tunnel" mark): shown in the list when nothing better
    /// is there, counted in <see cref="TunnelRounds"/>, but they never change <c>FailedRounds</c>, the
    /// VPN-off cache or the safeguards, so they never hide or unhide a server.
    /// </summary>
    public void RecordTunnel(IReadOnlyDictionary<string, int?> results, DateTimeOffset at)
    {
        lock (_gate)
        {
            foreach (var (id, ms) in results)
            {
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                if (!_entries.TryGetValue(id, out var entry))
                {
                    entry = new Entry();
                    _entries[id] = entry;
                }
                entry.TunnelMs = ms is > 0 ? ms : null;
                entry.TunnelAt = at;
                entry.TunnelRounds++;
            }
        }
    }

    /// <summary>How many tunnel measurements the server had (diagnostics).</summary>
    public int TunnelRounds(string? id)
    {
        lock (_gate)
        {
            return id != null && _entries.TryGetValue(id, out var entry) ? entry.TunnelRounds : 0;
        }
    }

    /// <summary>A successful VPN-off ping of the server at most <see cref="OffCacheFor"/> old.</summary>
    public bool HasRecentOffPing(string? id, DateTimeOffset now)
    {
        lock (_gate)
        {
            return id != null && _entries.TryGetValue(id, out var entry) && entry.OffMs is > 0 && Within(entry.OffAt, now);
        }
    }

    /// <summary>The last successful tunnel ping, at most <see cref="OffCacheFor"/> old; null otherwise.</summary>
    public int? TunnelPing(string? id, DateTimeOffset now)
    {
        lock (_gate)
        {
            return id != null && _entries.TryGetValue(id, out var entry) && entry.TunnelMs is > 0 && Within(entry.TunnelAt, now) ? entry.TunnelMs : null;
        }
    }

    /// <summary>
    /// While connected, the server needs a tunnel ping: the pinned probe did not measure it (failed or
    /// could not be pinned) and there is no VPN-off ping of the last <see cref="OffCacheFor"/>.
    /// </summary>
    public bool NeedsTunnelPing(string? id, bool pinnedOk, DateTimeOffset now) =>
        !string.IsNullOrEmpty(id) && !pinnedOk && !HasRecentOffPing(id, now);

    /// <summary>The server failed <see cref="FailedRoundsToHide"/> VPN-off rounds in a row (before the safeguards).</summary>
    public bool IsUnreachable(string? id)
    {
        lock (_gate)
        {
            return id != null && _entries.TryGetValue(id, out var entry) && entry.FailedRounds >= FailedRoundsToHide;
        }
    }

    /// <summary>
    /// Ids among <paramref name="ids"/> to hide from the list and the automatic choice, after the
    /// safeguards; never one of <paramref name="keep"/>.
    /// </summary>
    public HashSet<string> Hidden(IEnumerable<string?> ids, IEnumerable<string?>? keep = null)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = ids.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (_gate)
        {
            if (_networkSuspect || all.Count == 0)
            {
                return hidden;
            }
            var unreachable = all.Where(id => _entries.TryGetValue(id, out var entry) && entry.FailedRounds >= FailedRoundsToHide).ToList();
            if (unreachable.Count * 2 >= all.Count)
            {
                // Half of the servers or more: the network is the problem, not the servers.
                return hidden;
            }
            var kept = new HashSet<string>((keep ?? []).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!), StringComparer.OrdinalIgnoreCase);
            foreach (var id in unreachable.Where(id => !kept.Contains(id)))
            {
                hidden.Add(id);
            }
        }
        return hidden;
    }

    /// <summary><paramref name="items"/> without the hidden ones (see <see cref="Hidden"/>), in the same order.</summary>
    public List<T> Visible<T>(IReadOnlyCollection<T> items, Func<T, string?> id, IEnumerable<string?>? keep = null)
    {
        var hidden = Hidden(items.Select(id), keep);
        return hidden.Count == 0 ? items.ToList() : items.Where(item => id(item) is not { } value || !hidden.Contains(value)).ToList();
    }

    /// <summary>
    /// The ping a row shows: the last measured one (with the VPN on only when at most
    /// <see cref="OffCacheFor"/> old), else while connected the last VPN-off one at most
    /// <see cref="OffCacheFor"/> old; null ("— ms") otherwise.
    /// </summary>
    public int? DisplayPing(string? id, bool connected, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return null;
            }
            if (entry.LiveMs is > 0 && (!connected || Within(entry.LiveAt, now)))
            {
                return entry.LiveMs;
            }
            if (connected && entry.OffMs is > 0 && Within(entry.OffAt, now))
            {
                return entry.OffMs;
            }
            return connected && entry.TunnelMs is > 0 && Within(entry.TunnelAt, now) ? entry.TunnelMs : null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _networkSuspect = false;
        }
    }

    private static bool Within(DateTimeOffset at, DateTimeOffset now) =>
        now - at <= OffCacheFor && at <= now + TimeSpan.FromMinutes(1);
}

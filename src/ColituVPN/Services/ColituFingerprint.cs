using System.Text;
using System.Text.Json;

namespace v2rayN.Desktop.Services;

/// <summary>The panel's <c>fingerprint_policy</c> (FP-SPEC §2): try order, names never to use here, how many devices spread over.</summary>
public sealed record ColituFpPolicy
{
    public List<string> Order { get; init; } = [];
    public List<string> Blocked { get; init; } = [];
    public int Spread { get; init; } = ColituFingerprint.DefaultSpread;
    public string? Scope { get; init; }
}

/// <summary>One fingerprint's outcomes on one network (FP-SPEC §3).</summary>
public sealed record ColituFpRecord
{
    public DateTimeOffset? OkAt { get; init; }
    public DateTimeOffset? FailAt { get; init; }
    public int Fails { get; init; }
}

/// <summary>
/// Learned uTLS fingerprint per network (FP-SPEC §1–5): the names, the choice, the retry rule and the share-link
/// rewrite. Pure; the per-network memory is <see cref="ColituFpMemory"/>.
/// </summary>
public static class ColituFingerprint
{
    public const int DefaultSpread = 3;
    public const int MaxRetriesPerConnect = 2;
    public static readonly TimeSpan FailureMemory = TimeSpan.FromHours(24);

    /// <summary>uTLS names Xray accepts (§1); anything else in a policy is ignored.</summary>
    public static readonly string[] Names = ["chrome", "firefox", "safari", "ios", "android", "edge", "360", "qq", "random", "randomized"];

    /// <summary>The transports that take a fingerprint (§1). Hysteria2, Shadowsocks and the relay transport do not.</summary>
    public static readonly string[] Transports = ["vless-reality", "vless-xhttp", "trojan", "vless-ws"];

    /// <summary>A known uTLS name, lower case; null for anything else.</summary>
    public static string? Name(string? value)
    {
        var name = value?.Trim().ToLowerInvariant();
        return name != null && Array.IndexOf(Names, name) >= 0 ? name : null;
    }

    public static bool TakesFingerprint(string? protocol) =>
        protocol != null && Array.IndexOf(Transports, protocol.Trim().ToLowerInvariant()) >= 0;

    /// <summary>FNV-1a 32 bit over the UTF-8 bytes.</summary>
    public static uint Fnv1a32(string value)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(value ?? ""))
        {
            hash ^= b;
            hash = unchecked(hash * 16777619u);
        }
        return hash;
    }

    /// <summary>Locally failed (§3): the last outcome here was a fingerprint-type failure less than 24 h ago.</summary>
    public static bool LocallyFailed(ColituFpRecord? record, DateTimeOffset now) =>
        record?.FailAt is { } failed && now - failed < FailureMemory && (record.OkAt is not { } ok || failed >= ok);

    /// <summary>The fingerprint to use on this network (§4); "" when the policy names none.</summary>
    public static string Choose(ColituFpPolicy policy, string deviceId, string networkKey, IReadOnlyDictionary<string, ColituFpRecord>? state, DateTimeOffset now)
    {
        var blocked = new HashSet<string>(policy.Blocked.Select(Name).OfType<string>());
        var candidates = policy.Order.Select(Name).OfType<string>().Where(name => !blocked.Contains(name)).Distinct().ToList();
        if (candidates.Count == 0)
        {
            return "";
        }
        ColituFpRecord? Of(string name) => state != null && state.TryGetValue(name, out var record) ? record : null;
        // Sticky: a working choice is kept (the latest success among the candidates that are not failed here).
        var sticky = candidates
            .Select((name, index) => (name, index, record: Of(name)))
            .Where(item => item.record?.OkAt != null && !LocallyFailed(item.record, now))
            .OrderByDescending(item => item.record!.OkAt)
            .ThenBy(item => item.index)
            .FirstOrDefault();
        if (sticky.name != null)
        {
            return sticky.name;
        }
        var working = candidates.Where(name => !LocallyFailed(Of(name), now)).ToList();
        if (working.Count == 0)
        {
            working = candidates;
        }
        var pool = working.Take(Math.Max(1, policy.Spread)).ToList();
        return pool[(int)(Fnv1a32($"{deviceId}|{networkKey}") % (uint)pool.Count)];
    }

    /// <summary>
    /// A fingerprint-type failure (§5): a TLS transport failed with TCP reachable and the TLS probe (when it ran)
    /// said ok. A reset (tls_rst, ip_blocked) or any other probe class is not one.
    /// </summary>
    public static bool IsFingerprintFailure(string? protocol, bool tcpOk, string? probe) =>
        TakesFingerprint(protocol) && tcpOk && (probe == null || probe == ColituTlsClass.Ok);

    /// <summary>§5: retry the same transport on the same server with the next fingerprint (at most 2 per connect).</summary>
    public static bool ShouldRetry(string? protocol, bool tcpOk, string? probe, int retriesUsed) =>
        IsFingerprintFailure(protocol, tcpOk, probe) && retriesUsed < MaxRetriesPerConnect;

    /// <summary><c>fingerprint_policy</c> of GET /servers; null when absent (an old panel: the endpoint's own fingerprint stays).</summary>
    public static ColituFpPolicy? ParsePolicy(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } policy)
        {
            return null;
        }
        static List<string> Read(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!.Trim().ToLowerInvariant()).Where(item => item.Length is > 0 and <= 32).Take(32).ToList()
                : [];
        var spread = policy.TryGetProperty("spread", out var spreadValue) && spreadValue.ValueKind == JsonValueKind.Number && spreadValue.TryGetInt32(out var parsed)
            ? Math.Clamp(parsed, 1, Names.Length)
            : DefaultSpread;
        var scope = policy.TryGetProperty("scope", out var scopeValue) && scopeValue.ValueKind == JsonValueKind.String ? scopeValue.GetString() : null;
        return new ColituFpPolicy { Order = Read(policy, "order"), Blocked = Read(policy, "blocked"), Spread = spread, Scope = scope };
    }

    /// <summary><c>features.fp_events</c>: true only when the panel sends a literal true (absence = false).</summary>
    public static bool ParseEventsFeature(Dictionary<string, JsonElement>? features) =>
        features != null && features.TryGetValue("fp_events", out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// <paramref name="link"/> (a share link of <paramref name="protocol"/>) with <c>fp=</c> set to <paramref name="fingerprint"/>;
    /// unchanged for a transport without a fingerprint or an unknown name.
    /// </summary>
    public static string WithFingerprint(string link, string? protocol, string? fingerprint)
    {
        var name = Name(fingerprint);
        if (name == null || !TakesFingerprint(protocol))
        {
            return link;
        }
        var hash = link.IndexOf('#');
        var fragment = hash >= 0 ? link[hash..] : "";
        var body = hash >= 0 ? link[..hash] : link;
        var mark = body.IndexOf('?');
        if (mark < 0)
        {
            return $"{body}?fp={name}{fragment}";
        }
        var parts = body[(mark + 1)..].Split('&').Where(part => part.Length > 0).ToList();
        var at = parts.FindIndex(part => part.StartsWith("fp=", StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            parts[at] = $"fp={name}";
        }
        else
        {
            parts.Add($"fp={name}");
        }
        return $"{body[..mark]}?{string.Join("&", parts)}{fragment}";
    }

    /// <summary>
    /// The raw config (one share link per line) with the chosen fingerprint on the links of the four TLS transports;
    /// lines of other transports (and lines that are no candidate) stay as they were.
    /// </summary>
    public static string Apply(string rawConfig, IEnumerable<ColituConfigCandidate> candidates, string? fingerprint)
    {
        if (Name(fingerprint) == null)
        {
            return rawConfig;
        }
        var rewritten = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (TakesFingerprint(candidate.Protocol))
            {
                rewritten[candidate.ShareLink.Trim()] = WithFingerprint(candidate.ShareLink.Trim(), candidate.Protocol, fingerprint);
            }
        }
        var lines = rawConfig.Split('\n').Select(line => line.TrimEnd('\r'));
        return string.Join(Environment.NewLine, lines.Select(line => rewritten.TryGetValue(line.Trim(), out var link) ? link : line));
    }
}

/// <summary>
/// Per-network fingerprint outcomes (FP-SPEC §3), kept across restarts in the app state: at most 8 networks × 10 names,
/// the least recently used network goes first. Thread-safe.
/// </summary>
public sealed class ColituFpMemory
{
    public const int MaxNetworks = 8;
    public const int MaxNames = 10;

    private readonly object _gate = new();
    private Dictionary<string, Dictionary<string, ColituFpRecord>> _networks = new(StringComparer.Ordinal);

    public void Load(Dictionary<string, Dictionary<string, ColituFpRecord>>? saved)
    {
        lock (_gate)
        {
            _networks = new(StringComparer.Ordinal);
            foreach (var (network, names) in saved ?? [])
            {
                if (string.IsNullOrEmpty(network) || names == null) continue;
                var kept = names.Where(item => ColituFingerprint.Name(item.Key) != null && item.Value != null)
                    .OrderByDescending(item => Latest(item.Value))
                    .Take(MaxNames)
                    .ToDictionary(item => ColituFingerprint.Name(item.Key)!, item => item.Value, StringComparer.Ordinal);
                if (kept.Count > 0) _networks[network] = kept;
            }
            TrimLocked();
        }
    }

    public Dictionary<string, Dictionary<string, ColituFpRecord>> Snapshot()
    {
        lock (_gate)
        {
            return _networks.ToDictionary(item => item.Key, item => new Dictionary<string, ColituFpRecord>(item.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        }
    }

    public IReadOnlyDictionary<string, ColituFpRecord> Of(string network)
    {
        lock (_gate)
        {
            return _networks.TryGetValue(network, out var names) ? new Dictionary<string, ColituFpRecord>(names, StringComparer.Ordinal) : new Dictionary<string, ColituFpRecord>();
        }
    }

    public string Choose(ColituFpPolicy policy, string deviceId, string network, DateTimeOffset now) =>
        ColituFingerprint.Choose(policy, deviceId, network, Of(network), now);

    /// <summary>A fingerprint-type failure of <paramref name="fingerprint"/> on this network.</summary>
    public void MarkFailed(string network, string? fingerprint, DateTimeOffset now) =>
        Update(network, fingerprint, record => record with { FailAt = now, Fails = record.Fails + 1 });

    /// <summary>Traffic went through with <paramref name="fingerprint"/> here: a success clears the failure.</summary>
    public void MarkOk(string network, string? fingerprint, DateTimeOffset now) =>
        Update(network, fingerprint, record => record with { OkAt = now, FailAt = null, Fails = 0 });

    private void Update(string network, string? fingerprint, Func<ColituFpRecord, ColituFpRecord> change)
    {
        var name = ColituFingerprint.Name(fingerprint);
        if (name == null || string.IsNullOrEmpty(network)) return;
        lock (_gate)
        {
            if (!_networks.TryGetValue(network, out var names))
            {
                names = new(StringComparer.Ordinal);
            }
            // Re-inserted: the network becomes the most recently used one.
            _networks.Remove(network);
            names[name] = change(names.TryGetValue(name, out var record) ? record : new ColituFpRecord());
            while (names.Count > MaxNames)
            {
                names.Remove(names.OrderBy(item => Latest(item.Value)).First().Key);
            }
            _networks[network] = names;
            TrimLocked();
        }
    }

    private void TrimLocked()
    {
        while (_networks.Count > MaxNetworks)
        {
            _networks.Remove(_networks.OrderBy(item => item.Value.Values.Select(Latest).DefaultIfEmpty(DateTimeOffset.MinValue).Max()).First().Key);
        }
    }

    private static DateTimeOffset Latest(ColituFpRecord record) =>
        new[] { record.OkAt ?? DateTimeOffset.MinValue, record.FailAt ?? DateTimeOffset.MinValue }.Max();
}

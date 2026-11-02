using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace v2rayN.Desktop.Services;

/// <summary>
/// The optional <c>engine</c> section of the silent support report (AC4 phase 4, openapi
/// <c>DiagnosticsEngine</c>): engine decisions, ping round summaries, hidden servers, whitelist
/// checks, the model version and the feature flags. Every item is masked like the report (sites and
/// foreign addresses hidden, Colitu hosts readable) and the limits are applied on the device: at most
/// 300 decisions, 60 ping rounds, 200 hidden servers, 100 whitelist checks and 64 flags, each item
/// at most 4 KiB of JSON, the whole at most 192 KiB; the oldest go first. Arrays are newest first.
/// </summary>
public static class ColituDiagnosticsEngine
{
    public const int MaxDecisions = 300;
    public const int MaxPingRounds = 60;
    public const int MaxHiddenServers = 200;
    public const int MaxWhitelistChecks = 100;
    public const int MaxFlags = 64;
    public const int MaxItemBytes = 4 * 1024;
    public const int MaxTotalBytes = 192 * 1024;
    public const int MaxModelVersion = 64;

    /// <summary>
    /// Builds the section from the journal (oldest first, as <see cref="ColituEngineJournal.Snapshot"/>
    /// gives it). <paramref name="hiddenNow"/>: the servers the list hides right now. Null when there is
    /// nothing to send.
    /// </summary>
    public static JsonObject? Build(
        IReadOnlyList<ColituEngineJournalEntry> journal,
        IEnumerable<object>? hiddenNow,
        string? modelVersion,
        IReadOnlyDictionary<string, object?>? flags,
        IEnumerable<string?>? serverHosts)
    {
        var hosts = (serverHosts ?? []).Where(host => !string.IsNullOrWhiteSpace(host)).ToList();
        var newestFirst = journal.Reverse().ToList();

        var decisions = Items(newestFirst.Where(e => e.Kind is ColituEngineJournal.Decision or ColituEngineJournal.Outcome), MaxDecisions, hosts, compact: true);
        var pingRounds = Items(newestFirst.Where(e => e.Kind == ColituEngineJournal.PingRound), MaxPingRounds, hosts);
        var hidden = new List<string>();
        foreach (var server in hiddenNow ?? [])
        {
            if (hidden.Count >= MaxHiddenServers) break;
            if (Fit(JsonSerializer.SerializeToNode(server), hosts) is { } item) hidden.Add(item);
        }
        hidden.AddRange(Items(newestFirst.Where(e => e.Kind == ColituEngineJournal.Hidden), MaxHiddenServers - hidden.Count, hosts));
        var whitelist = Items(newestFirst.Where(e => e.Kind == ColituEngineJournal.Whitelist), MaxWhitelistChecks, hosts);

        var flagObject = new JsonObject();
        foreach (var (key, value) in (flags ?? new Dictionary<string, object?>()).Take(MaxFlags))
        {
            flagObject[key] = value == null ? null : JsonSerializer.SerializeToNode(value);
        }
        var flagsJson = Mask(flagObject.ToJsonString(), hosts);
        if (Encoding.UTF8.GetByteCount(flagsJson) > MaxItemBytes)
        {
            flagsJson = "{}";
        }
        var version = modelVersion is { Length: > 0 } v ? (v.Length > MaxModelVersion ? v[..MaxModelVersion] : v) : null;

        if (decisions.Count + pingRounds.Count + hidden.Count + whitelist.Count == 0 && version == null && flagObject.Count == 0)
        {
            return null;
        }

        // The whole at most 192 KiB: the oldest decisions go first, then the oldest of the rest.
        int Size() => 256 + Encoding.UTF8.GetByteCount(flagsJson) + (version?.Length ?? 0)
            + new[] { decisions, pingRounds, hidden, whitelist }.Sum(list => list.Sum(item => Encoding.UTF8.GetByteCount(item) + 1));
        var size = Size();
        foreach (var list in new[] { decisions, pingRounds, whitelist, hidden })
        {
            while (size > MaxTotalBytes && list.Count > 0)
            {
                size -= Encoding.UTF8.GetByteCount(list[^1]) + 1;
                list.RemoveAt(list.Count - 1);
            }
        }

        var result = new JsonObject();
        if (version != null) result["model_version"] = version;
        result["flags"] = JsonNode.Parse(flagsJson);
        result["decisions"] = Array(decisions);
        result["ping_rounds"] = Array(pingRounds);
        result["hidden_servers"] = Array(hidden);
        result["whitelist_checks"] = Array(whitelist);
        return result;
    }

    private static JsonArray Array(List<string> items) => new(items.Select(item => JsonNode.Parse(item)).ToArray());

    private static List<string> Items(IEnumerable<ColituEngineJournalEntry> entries, int max, List<string> hosts, bool compact = false)
    {
        var result = new List<string>();
        foreach (var entry in entries)
        {
            if (result.Count >= max) break;
            var item = new JsonObject { ["at"] = entry.At.UtcDateTime.ToString("o"), ["kind"] = entry.Kind };
            if (entry.Data is JsonObject data)
            {
                foreach (var (key, value) in data)
                {
                    if (key is "at" or "kind") continue;
                    item[key] = value?.DeepClone();
                }
            }
            if (compact && entry.Kind == ColituEngineJournal.Decision)
            {
                CompactCandidates(item);
            }
            if (Fit(item, hosts) is { } json) result.Add(json);
        }
        return result;
    }

    /// <summary>
    /// A decision's candidates as short records in ranking order (eligible first), so the most useful
    /// ones survive when the item has to be cut to 4 KiB.
    /// </summary>
    private static void CompactCandidates(JsonObject item)
    {
        if (item["candidates"] is not JsonArray candidates)
        {
            return;
        }
        var ranking = item["ranking"] is JsonArray r ? r.Select(n => n?.GetValue<int>() ?? -1).ToList() : [];
        var order = ranking.Where(i => i >= 0 && i < candidates.Count)
            .Concat(Enumerable.Range(0, candidates.Count).Where(i => !ranking.Contains(i)))
            .ToList();
        var compact = new JsonArray();
        foreach (var index in order)
        {
            if (candidates[index] is not JsonObject c) continue;
            var scored = c["scored"] as JsonObject;
            var features = scored?["features"] as JsonObject;
            var row = new JsonObject
            {
                ["i"] = index,
                ["node_id"] = c["node_id"]?.DeepClone(),
                ["protocol"] = c["protocol"]?.DeepClone(),
                ["entry"] = c["entry"]?.DeepClone(),
                ["exit"] = c["exit"]?.DeepClone(),
                ["ms"] = c["latency_ms"]?.DeepClone(),
                ["n"] = c["history"]?.DeepClone()
            };
            if (c["latency_source"] is { } source)
            {
                row["ms_src"] = source.DeepClone();
            }
            if (c["scope"] is { } scope)
            {
                row["scope"] = scope.DeepClone();
            }
            if (scored != null)
            {
                row["score"] = Round(scored["score"]);
                row["alpha"] = Round(scored["alpha"]);
                row["beta"] = Round(scored["beta"]);
                row["eligible"] = scored["eligible"]?.DeepClone();
                row["tier"] = scored["tier"]?.DeepClone();
                if (scored["theta"] is { } theta) row["theta"] = Round(theta);
            }
            if (features != null)
            {
                row["f"] = new JsonArray(new[] { "latency", "own", "prior", "hint", "recommended", "load", "protocol_fit", "whitelist" }
                    .Select(key => (JsonNode?)Round(features[key])).ToArray());
            }
            compact.Add(row);
        }
        item["candidates_total"] = candidates.Count;
        item["candidates"] = compact;
    }

    private static JsonNode? Round(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<double>(out var d) ? JsonValue.Create(Math.Round(d, 4)) : value?.DeepClone();

    /// <summary>The masked item as JSON, cut to 4 KiB (its candidate list halved while too big); null when it cannot fit.</summary>
    internal static string? Fit(JsonNode? item, List<string> hosts)
    {
        if (item is not JsonObject obj)
        {
            return null;
        }
        obj = (JsonObject)obj.DeepClone();
        MaskValues(obj, hosts);
        while (true)
        {
            var json = Mask(obj.ToJsonString(), hosts);
            if (json != null && Encoding.UTF8.GetByteCount(json) <= MaxItemBytes)
            {
                return json;
            }
            if (json == null || obj["candidates"] is not JsonArray list || list.Count == 0)
            {
                return null;
            }
            var keep = list.Count / 2;
            var cut = new JsonArray(list.Take(keep).Select(n => n?.DeepClone()).ToArray());
            obj["candidates"] = cut;
            obj["candidates_cut"] = true;
        }
    }

    /// <summary>
    /// String values that are a host name or an IP address on their own: masked by the report's rules
    /// (a Colitu host or an allowed check host stays readable, anything else becomes &lt;site&gt; / &lt;ip&gt;).
    /// </summary>
    internal static void MaskValues(JsonNode? node, List<string> hosts)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && MaskValue(text, hosts) is { } masked)
                    {
                        obj[key] = masked;
                    }
                    else
                    {
                        MaskValues(obj[key], hosts);
                    }
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && MaskValue(text, hosts) is { } masked)
                    {
                        array[i] = masked;
                    }
                    else
                    {
                        MaskValues(array[i], hosts);
                    }
                }
                break;
        }
    }

    /// <summary>The masked form of a bare host or address; null when the value is neither or stays readable.</summary>
    internal static string? MaskValue(string value, List<string> hosts)
    {
        var text = value.Trim();
        var ipv4 = System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{1,3}(\.\d{1,3}){3}$");
        var ipv6 = text.Contains(':') && System.Net.IPAddress.TryParse(text, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var host = System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?=.{4,253}$)([a-z0-9-]+\.)+[a-z]{2,63}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!ipv4 && !ipv6 && !host)
        {
            return null;
        }
        var probe = $"https://{(ipv6 ? $"[{text}]" : text)}";
        var masked = ColituSiteRedaction.Apply(probe, hosts);
        if (masked == probe)
        {
            return null;
        }
        return ipv4 || ipv6 ? "<ip>" : "<site>";
    }

    /// <summary>The report's masking on the item's JSON; null when the result is no longer a JSON object.</summary>
    internal static string? Mask(string json, List<string> hosts)
    {
        var masked = ColituSiteRedaction.Apply(json, hosts);
        if (masked == json)
        {
            return json;
        }
        try
        {
            return JsonNode.Parse(masked) is JsonObject ? masked : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

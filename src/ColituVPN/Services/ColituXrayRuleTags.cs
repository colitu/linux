using System.Text.Json;
using System.Text.Json.Nodes;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Xray 26.10 refuses to start ("app/router: duplicate ruleTag ...") when two routing rules share a
/// <c>ruleTag</c>. The app's generator does not write one today, but a full config template, a
/// routing rule set or a later stage may; so every Xray config gets unique tags in the copy handed
/// to the core: the first rule keeps its tag, later ones get <c>-2</c>, <c>-3</c>, ... (as on
/// Android, XrayRuleTags.kt).
/// </summary>
public static class ColituXrayRuleTags
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The config with unique rule tags; the same string when nothing repeats or it is not readable.</summary>
    public static string Unique(string config)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(config) as JsonObject;
        }
        catch (JsonException)
        {
            return config;
        }
        if (root?["routing"]?["rules"] is not JsonArray rules)
        {
            return config;
        }
        var tagged = rules.OfType<JsonObject>().Select(rule => (Rule: rule, Tag: TagOf(rule))).Where(item => item.Tag != null).ToList();
        if (tagged.Select(item => item.Tag).Distinct(StringComparer.Ordinal).Count() == tagged.Count)
        {
            return config;
        }
        var taken = new HashSet<string>(tagged.Select(item => item.Tag!), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rule, tag) in tagged)
        {
            if (seen.Add(tag!))
            {
                continue;
            }
            var n = 2;
            while (taken.Contains($"{tag}-{n}"))
            {
                n++;
            }
            var renamed = $"{tag}-{n}";
            taken.Add(renamed);
            rule["ruleTag"] = renamed;
        }
        return root.ToJsonString(Indented);
    }

    private static string? TagOf(JsonObject rule) =>
        rule["ruleTag"] is JsonValue value && value.TryGetValue<string>(out var tag) && tag.Length > 0 ? tag : null;
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// The shared Adaptive Connect test vectors (canonical copy in the colitu-csl repo,
/// engine/testdata/ac-vectors; the same files run in the Android, iOS and Linux apps and in the
/// Go engine). Every case must give exactly its "expect".
/// </summary>
public class ColituAcVectorsTests
{
    private static JsonElement Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ac-vectors", name))).RootElement;

    private static string? Text(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [Fact]
    public void Ranking()
    {
        var doc = Load("ranking.json");
        var now = DateTimeOffset.FromUnixTimeMilliseconds(doc.GetProperty("now_ms").GetInt64());
        var network = doc.GetProperty("network").GetString()!;
        var failures = new List<string>();
        foreach (var c in doc.GetProperty("cases").EnumerateArray())
        {
            var servers = c.GetProperty("servers").EnumerateArray().Select(s => new ColituVpnServer
            {
                Id = s.GetProperty("id").GetString(),
                Name = s.GetProperty("id").GetString(),
                CountryCode = Text(s, "country"),
                Available = !s.TryGetProperty("available", out var available) || available.GetBoolean()
            }).ToList();
            var pings = new Dictionary<string, ColituPingSample>();
            foreach (var p in c.GetProperty("pings").EnumerateObject())
            {
                var ms = p.Value.GetProperty("ms");
                pings[p.Name] = new ColituPingSample(ms.ValueKind == JsonValueKind.Null ? null : ms.GetInt32(),
                    now - TimeSpan.FromSeconds(p.Value.GetProperty("age_s").GetInt64()), Text(p.Value, "network") ?? network);
            }
            var memory = new ColituAdaptiveMemory();
            if (Text(c, "last_good") is { } lastGood)
            {
                memory.RememberGoodServer(network, lastGood, now - TimeSpan.FromMinutes(5));
            }
            foreach (var pen in c.GetProperty("penalized").EnumerateArray())
            {
                memory.Penalize(network, pen.GetProperty("id").GetString(), now - TimeSpan.FromSeconds(pen.GetProperty("age_s").GetInt64()));
            }
            var got = ColituAdaptiveConnect.Rank(servers, pings, memory, network, Text(c, "client_country"), now).Select(s => s.Id).ToList();
            var expect = c.GetProperty("expect").EnumerateArray().Select(e => e.GetString()).ToList();
            if (!got.SequenceEqual(expect))
            {
                failures.Add($"{c.GetProperty("name").GetString()}: expected [{string.Join(",", expect)}], got [{string.Join(",", got)}]");
            }
        }
        failures.Should().BeEmpty(string.Join(" || ", failures));
    }

    private static List<string> Strings(JsonElement list) =>
        list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(e => e.GetString()!).ToList() : [];

    private static string Show(IEnumerable<string>? items) => items == null ? "null" : $"[{string.Join(",", items)}]";

    private static bool IsNull(JsonElement value) => value.ValueKind == JsonValueKind.Null;

    /// <summary>Base order of the shared vectors: TransportRank with latency 0 (reachable), stable.</summary>
    private static List<string> BaseOrder(IEnumerable<string> offered, ICollection<string> stalled) =>
        offered.OrderBy(protocol => ColituVpnService.TransportRank(protocol, 0, stalled.Contains(protocol))).ToList();

    [Fact]
    public void Transport()
    {
        var doc = Load("transport.json");
        var failures = new List<string>();
        foreach (var c in doc.GetProperty("base_order").EnumerateArray())
        {
            var got = BaseOrder(Strings(c.GetProperty("offered")), Strings(c.GetProperty("stalled")));
            var expect = Strings(c.GetProperty("expect"));
            if (!got.SequenceEqual(expect))
            {
                failures.Add($"base_order {c.GetProperty("name").GetString()}: expected {Show(expect)}, got {Show(got)}");
            }
        }
        foreach (var c in doc.GetProperty("hinted_start").EnumerateArray())
        {
            var offered = Strings(c.GetProperty("offered"));
            var stalled = Strings(c.GetProperty("stalled"));
            var got = ColituNetworkHintsPolicy.HintedStart(offered, protocol => protocol, Strings(c.GetProperty("preferred")), stalled.Contains,
                Text(c, "last_good"), rest => BaseOrder(rest, stalled));
            var expect = IsNull(c.GetProperty("expect")) ? null : Strings(c.GetProperty("expect"));
            if (!(got == null ? expect == null : expect != null && got.SequenceEqual(expect)))
            {
                failures.Add($"hinted_start {c.GetProperty("name").GetString()}: expected {Show(expect)}, got {Show(got)}");
            }
        }
        foreach (var c in doc.GetProperty("marks_ignored").EnumerateArray())
        {
            var stalled = Strings(c.GetProperty("stalled"));
            var got = ColituTransportOrder.MarksCoverAlmostAll(Strings(c.GetProperty("offered")), stalled.Contains);
            if (got != c.GetProperty("expect").GetBoolean())
            {
                failures.Add($"marks_ignored {c.GetProperty("name").GetString()}: expected {c.GetProperty("expect").GetBoolean()}, got {got}");
            }
        }
        failures.Should().BeEmpty(string.Join(" || ", failures));
    }

    [Fact]
    public void Hints()
    {
        var doc = Load("hints.json");
        var failures = new List<string>();
        foreach (var c in doc.GetProperty("parse").EnumerateArray())
        {
            // The real path: the raw server-list JSON through the DTO, then the parsers.
            var json = "{\"servers\":[],\"network_hints\":{\"blocked\":" + c.GetProperty("blocked").GetRawText()
                + ",\"preferred\":" + c.GetProperty("preferred").GetRawText() + "}}";
            List<string> blocked, preferred;
            try
            {
                var dto = JsonSerializer.Deserialize<ColituServerListDto>(json)!;
                blocked = ColituNetworkHintsPolicy.ParseProtocols(dto.NetworkHints?.Blocked);
                preferred = ColituNetworkHintsPolicy.ParsePreferred(dto.NetworkHints?.Preferred, dto.NetworkHints?.Blocked);
            }
            catch (Exception ex)
            {
                failures.Add($"{c.GetProperty("name").GetString()}: {ex.GetType().Name}");
                continue;
            }
            var expectBlocked = Strings(c.GetProperty("expect_blocked"));
            var expectPreferred = Strings(c.GetProperty("expect_preferred"));
            if (!blocked.SequenceEqual(expectBlocked) || !preferred.SequenceEqual(expectPreferred))
            {
                failures.Add($"{c.GetProperty("name").GetString()}: expected blocked {Show(expectBlocked)} preferred {Show(expectPreferred)}, got blocked {Show(blocked)} preferred {Show(preferred)}");
            }
        }
        failures.Should().BeEmpty(string.Join(" || ", failures));
    }

    private static string Iso(DateTimeOffset value) => "\"" + value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\"";

    /// <summary>A recovery-set JSON in which every listed id (null: none) has a valid envelope.</summary>
    private static string SetJson(string generatedAt, string recoveryUntil, JsonElement servers)
    {
        var configs = servers.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String
            ? "{\"server\":{\"id\":" + JsonSerializer.Serialize(e.GetString()) + "},\"profile\":{}}"
            : "{\"server\":{},\"profile\":{}}");
        return "{\"generated_at\":" + generatedAt + ",\"recovery_until\":" + recoveryUntil + ",\"configs\":[" + string.Join(",", configs) + "]}";
    }

    [Fact]
    public void Recovery()
    {
        var doc = Load("recovery.json");
        var now = DateTimeOffset.Parse(doc.GetProperty("now").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var one = JsonDocument.Parse("[\"a\"]").RootElement;
        var failures = new List<string>();
        foreach (var c in doc.GetProperty("decide").EnumerateArray())
        {
            var automatic = c.GetProperty("automatic").GetBoolean();
            var multihop = c.GetProperty("multihop").GetBoolean();
            var until = c.GetProperty("expired").GetBoolean() ? now.AddMinutes(-1) : now.AddDays(13);
            var expired = ColituRecoverySet.Parse(SetJson(Iso(now.AddDays(-1)), Iso(until), one))!.IsExpired(now);
            var use = automatic && !multihop
                && ColituRecoverySet.ShouldUse(automatic, c.GetProperty("api_unreachable").GetBoolean(), c.GetProperty("cache_usable").GetBoolean());
            var got = !use ? "none" : expired ? "expired" : "use";
            if (got != c.GetProperty("expect").GetString())
            {
                failures.Add($"decide {c.GetProperty("name").GetString()}: expected {c.GetProperty("expect").GetString()}, got {got}");
            }
        }
        foreach (var c in doc.GetProperty("refresh_due").EnumerateArray())
        {
            DateTimeOffset? Age(string key) => IsNull(c.GetProperty(key)) ? null : now - TimeSpan.FromMinutes(c.GetProperty(key).GetInt64());
            var got = ColituRecoverySet.RefreshDue(Age("generated_age_min"), Age("attempt_age_min"), now);
            if (got != c.GetProperty("expect").GetBoolean())
            {
                failures.Add($"refresh_due {c.GetProperty("name").GetString()}: expected {c.GetProperty("expect").GetBoolean()}, got {got}");
            }
        }
        foreach (var c in doc.GetProperty("set_parse").EnumerateArray())
        {
            var set = ColituRecoverySet.Parse(SetJson(c.GetProperty("generated_at").GetRawText(), c.GetProperty("recovery_until").GetRawText(), c.GetProperty("servers")));
            var got = set?.Configs.Select(item => item.Server!.Id!).ToList();
            var expect = IsNull(c.GetProperty("expect")) ? null : Strings(c.GetProperty("expect"));
            if (!(got == null ? expect == null : expect != null && got.SequenceEqual(expect)))
            {
                failures.Add($"set_parse {c.GetProperty("name").GetString()}: expected {Show(expect)}, got {Show(got)}");
            }
        }
        foreach (var c in doc.GetProperty("next_server").EnumerateArray())
        {
            var set = ColituRecoverySet.Parse(SetJson(Iso(now), Iso(now.AddDays(14)), c.GetProperty("servers")));
            var got = set?.NextServer(Strings(c.GetProperty("failed")))?.Server?.Id;
            var expect = IsNull(c.GetProperty("expect")) ? null : c.GetProperty("expect").GetString();
            if (got != expect)
            {
                failures.Add($"next_server {c.GetProperty("name").GetString()}: expected {expect ?? "null"}, got {got ?? "null"}");
            }
        }
        failures.Should().BeEmpty(string.Join(" || ", failures));
    }
}

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
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// The Go decision engine sidecar. The request it gets for the automatic order must give the same
/// order as the app for every shared ranking vector. Runs against a real engine binary when
/// COLITU_ENGINE points to one (colitu-csl: sh engine/build.sh); otherwise only the request is checked.
/// </summary>
public class ColituEngineTests
{
    [Fact]
    public void RankRequest_CarriesWhatTheRankingUses()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
        var memory = new ColituAdaptiveMemory();
        memory.RememberGoodServer("wifi|X", "a", now.AddMinutes(-1));
        memory.Penalize("wifi|X", "b", now.AddMinutes(-2));
        var request = ColituEngine.RankRequest(
            [new ColituVpnServer { Id = "a", CountryCode = "DE", Available = true }, new ColituVpnServer { Id = "b", CountryCode = "NL", Available = true, Locked = true }],
            new Dictionary<string, ColituPingSample> { ["a"] = new(40, now.AddSeconds(-30), "wifi|X") },
            memory, "wifi|X", "TR", now);

        request["last_good"]!.GetValue<string>().Should().Be("a");
        request["servers"]!.AsArray()[1]!["available"]!.GetValue<bool>().Should().BeFalse();
        request["pings"]!["a"]!["ms"]!.GetValue<int>().Should().Be(40);
        request["penalized"]!.AsArray().Single()!["id"]!.GetValue<string>().Should().Be("b");
    }

    [Fact]
    public void RankRequest_LeavesAlphaServersOut()
    {
        var request = ColituEngine.RankRequest(
            [new ColituVpnServer { Id = "csl", CountryCode = "SE", Available = true, Alpha = true }, new ColituVpnServer { Id = "a", CountryCode = "DE", Available = true }],
            new Dictionary<string, ColituPingSample>(), new ColituAdaptiveMemory(), "wifi|X", "TR", DateTimeOffset.UtcNow);

        request["servers"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).Should().Equal("a");
    }

    [Fact]
    public async Task RealEngine_AgreesWithTheAppOnEveryRankingVector()
    {
        var exe = Environment.GetEnvironmentVariable("COLITU_ENGINE");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            return;
        }
        ColituEngine.PathOverride = exe;
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ac-vectors", "ranking.json"))).RootElement;
        var now = DateTimeOffset.FromUnixTimeMilliseconds(doc.GetProperty("now_ms").GetInt64());
        var network = doc.GetProperty("network").GetString()!;
        var failures = new List<string>();
        foreach (var c in doc.GetProperty("cases").EnumerateArray())
        {
            var servers = c.GetProperty("servers").EnumerateArray().Select(s => new ColituVpnServer
            {
                Id = s.GetProperty("id").GetString(),
                CountryCode = s.GetProperty("country").GetString(),
                Available = !s.TryGetProperty("available", out var a) || a.GetBoolean()
            }).ToList();
            var pings = new Dictionary<string, ColituPingSample>();
            foreach (var p in c.GetProperty("pings").EnumerateObject())
            {
                var ms = p.Value.GetProperty("ms");
                pings[p.Name] = new ColituPingSample(ms.ValueKind == JsonValueKind.Null ? null : ms.GetInt32(),
                    now - TimeSpan.FromSeconds(p.Value.GetProperty("age_s").GetInt64()),
                    p.Value.TryGetProperty("network", out var n) ? n.GetString()! : network);
            }
            var memory = new ColituAdaptiveMemory();
            if (c.GetProperty("last_good").ValueKind == JsonValueKind.String)
            {
                memory.RememberGoodServer(network, c.GetProperty("last_good").GetString(), now - TimeSpan.FromMinutes(5));
            }
            foreach (var pen in c.GetProperty("penalized").EnumerateArray())
            {
                memory.Penalize(network, pen.GetProperty("id").GetString(), now - TimeSpan.FromSeconds(pen.GetProperty("age_s").GetInt64()));
            }
            var app = ColituAdaptiveConnect.Rank(servers, pings, memory, network, c.GetProperty("client_country").GetString(), now).Select(s => s.Id).ToList();
            var engine = await ColituEngine.Instance.CallAsync("rank", ColituEngine.RankRequest(servers, pings, memory, network, c.GetProperty("client_country").GetString(), now));
            var engineIds = engine?.AsArray().Select(x => x!.GetValue<string>()).ToList();
            if (engineIds == null || !engineIds.SequenceEqual(app))
            {
                failures.Add($"{c.GetProperty("name").GetString()}: app [{string.Join(",", app)}], engine [{string.Join(",", engineIds ?? [])}]");
            }
        }
        failures.Should().BeEmpty(string.Join(" || ", failures));
    }
    private static List<ColituVpnServer> Servers(params (string Id, string Country)[] items) =>
        items.Select(s => new ColituVpnServer { Id = s.Id, CountryCode = s.Country, Available = true }).ToList();

    [Fact]
    public async Task Decide_WithoutAnEngine_UsesTheAppOrder()
    {
        ColituEngine.PathOverride = Path.Combine(Path.GetTempPath(), "no-such-engine-" + Guid.NewGuid().ToString("N"), "colitu-engine.exe");
        try
        {
            var app = Servers(("a", "DE"), ("b", "NL"));
            var (order, source) = await ColituEngine.Instance.DecideOrderAsync("rank", new JsonObject(), app, s => s.Id);
            order.Should().BeSameAs(app);
            source.Should().StartWith("app");
        }
        finally
        {
            ColituEngine.PathOverride = null;
        }
    }

    [Fact]
    public async Task Decide_RealEngine_DecidesTheOrder_ButNeverAddsOrDropsAServer()
    {
        var exe = Environment.GetEnvironmentVariable("COLITU_ENGINE");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            return;
        }
        ColituEngine.PathOverride = exe;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var all = Servers(("de", "DE"), ("nl", "NL"), ("fi", "FI"));
            var pings = new Dictionary<string, ColituPingSample>
            {
                ["de"] = new(200, now, "wifi"), ["nl"] = new(40, now, "wifi"), ["fi"] = new(90, now, "wifi")
            };
            var request = ColituEngine.RankRequest(all, pings, new ColituAdaptiveMemory(), "wifi", "TR", now);
            var app = all.ToList();
            var (order, source) = await ColituEngine.Instance.DecideOrderAsync("rank", request, app, s => s.Id);
            source.Should().Be("engine");
            order.Select(s => s.Id).Should().BeEquivalentTo(app.Select(s => s.Id));

            // The app left "fi" out (a rule only it knows): the engine's answer still names it, so the app order stays.
            var fewer = all.Where(s => s.Id != "fi").ToList();
            var (kept, why) = await ColituEngine.Instance.DecideOrderAsync("rank", request, fewer, s => s.Id);
            kept.Should().BeSameAs(fewer);
            why.Should().Be("app (engine set differs)");
        }
        finally
        {
            ColituEngine.PathOverride = null;
        }
    }
}

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
/// Adaptive Connect 4 "Colitu LM": the shared vectors (colitu-panel docs/ac-model-vectors.json; the
/// same file runs in the Go reference and every app) must come out to 1e-9, plus the whitelist rule,
/// the Russian-server rules and the AC 3 fallback.
/// </summary>
public class ColituLmTests
{
    private static JsonElement Vectors() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ac-model-vectors.json"))).RootElement;

    private static double Tolerance(JsonElement doc) => doc.GetProperty("tolerance").GetDouble();

    [Fact]
    public void RngVectors()
    {
        var doc = Vectors();
        var count = 0;
        foreach (var v in doc.GetProperty("rng").EnumerateArray())
        {
            var seed = v.GetProperty("seed").GetUInt32();
            var rng = new ColituLmRng(seed);
            foreach (var want in v.GetProperty("next").EnumerateArray())
            {
                rng.Next().Should().Be(want.GetUInt32(), $"seed {seed} next");
            }
            rng = new ColituLmRng(seed);
            foreach (var want in v.GetProperty("floats").EnumerateArray())
            {
                rng.NextFloat().Should().Be(want.GetDouble(), $"seed {seed} float");
            }
            count++;
        }
        count.Should().Be(3);
    }

    [Fact]
    public void BetaSampleVectors()
    {
        var doc = Vectors();
        var tolerance = Tolerance(doc);
        var count = 0;
        foreach (var v in doc.GetProperty("beta_samples").EnumerateArray())
        {
            var rng = new ColituLmRng(v.GetProperty("seed").GetUInt32());
            var alpha = v.GetProperty("alpha").GetDouble();
            var beta = v.GetProperty("beta").GetDouble();
            foreach (var want in v.GetProperty("expected").EnumerateArray())
            {
                ColituLm.SampleBeta(ref rng, alpha, beta).Should().BeApproximately(want.GetDouble(), tolerance);
                count++;
            }
        }
        count.Should().Be(16);
    }

    [Fact]
    public void DecisionVectors()
    {
        var doc = Vectors();
        var tolerance = Tolerance(doc);
        var failures = new List<string>();
        var cases = doc.GetProperty("cases").EnumerateArray().ToList();
        cases.Should().HaveCount(29);
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            var input = c.GetProperty("input").Deserialize<ColituLmInput>()!;
            var got = ColituLm.Decide(input);
            Compare(name, got, c.GetProperty("expected"), tolerance, failures);
        }
        failures.Should().BeEmpty(string.Join(" || ", failures));
    }

    private static void Compare(string name, ColituLmDecision got, JsonElement want, double tolerance, List<string> failures)
    {
        void Check(bool ok, string what)
        {
            if (!ok) failures.Add($"{name}: {what}");
        }
        bool Near(double a, double b) => Math.Abs(a - b) <= tolerance;

        Check(got.Engine == want.GetProperty("engine").GetString(), $"engine {got.Engine}");
        Check(got.Stale == want.GetProperty("stale").GetBoolean(), "stale");
        Check(got.Explored == want.GetProperty("explored").GetBoolean(), "explored");
        Check(got.WhitelistNone == want.GetProperty("whitelist_none").GetBoolean(), "whitelist_none");

        var wantRanking = want.GetProperty("ranking") is { ValueKind: JsonValueKind.Array } r ? r.EnumerateArray().Select(e => e.GetInt32()).ToList() : [];
        Check((got.Ranking ?? []).SequenceEqual(wantRanking), $"ranking [{string.Join(",", got.Ranking ?? [])}] want [{string.Join(",", wantRanking)}]");
        var wantChoice = want.GetProperty("choice") is { ValueKind: JsonValueKind.Number } ch ? ch.GetInt32() : (int?)null;
        Check(got.Choice == wantChoice, $"choice {got.Choice} want {wantChoice}");

        var wantCandidates = want.GetProperty("candidates") is { ValueKind: JsonValueKind.Array } wc ? wc.EnumerateArray().ToList() : [];
        var gotCandidates = got.Candidates ?? [];
        Check(gotCandidates.Count == wantCandidates.Count, $"candidates {gotCandidates.Count} want {wantCandidates.Count}");
        for (var i = 0; i < Math.Min(gotCandidates.Count, wantCandidates.Count); i++)
        {
            var g = gotCandidates[i];
            var w = wantCandidates[i];
            var wf = w.GetProperty("features");
            var features = new (string Key, double Value)[]
            {
                ("latency", g.Features.Latency), ("own", g.Features.Own), ("prior", g.Features.Prior), ("hint", g.Features.Hint),
                ("recommended", g.Features.Recommended), ("load", g.Features.Load), ("protocol_fit", g.Features.ProtocolFit),
                ("whitelist", g.Features.Whitelist)
            };
            foreach (var (key, value) in features)
            {
                Check(Near(value, wf.GetProperty(key).GetDouble()), $"[{i}] feature {key} {value:R} want {wf.GetProperty(key).GetDouble():R}");
            }
            Check(Near(g.Alpha, w.GetProperty("alpha").GetDouble()), $"[{i}] alpha {g.Alpha:R}");
            Check(Near(g.Beta, w.GetProperty("beta").GetDouble()), $"[{i}] beta {g.Beta:R}");
            Check(Near(g.LogOdds, w.GetProperty("log_odds").GetDouble()), $"[{i}] log_odds {g.LogOdds:R}");
            Check(Near(g.Score, w.GetProperty("score").GetDouble()), $"[{i}] score {g.Score:R}");
            Check(g.Eligible == w.GetProperty("eligible").GetBoolean(), $"[{i}] eligible");
            Check(g.Tier == w.GetProperty("tier").GetInt32(), $"[{i}] tier");
            var theta = w.GetProperty("theta");
            Check(theta.ValueKind == JsonValueKind.Null ? g.Theta == null : g.Theta is { } t && Near(t, theta.GetDouble()),
                $"[{i}] theta {g.Theta} want {theta}");
        }
    }

    // ── Whitelist rule ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 4, true)]
    [InlineData(1, 2, true)]
    [InlineData(2, 4, false)]
    [InlineData(1, 1, false)]
    [InlineData(0, 0, false)]
    [InlineData(5, 4, false)]
    public void WhitelistRule(int foreignOk, int domesticOk, bool whitelist)
    {
        ColituWhitelist.IsWhitelist(foreignOk, domesticOk).Should().Be(whitelist);
        new ColituWhitelistCheck(foreignOk, 5, domesticOk, 4, 100).Whitelist.Should().Be(whitelist);
    }

    [Fact]
    public void WhitelistModeEndsOnlyWhenTheForeignSetWorksAgain()
    {
        ColituWhitelist.NextState(false, new ColituWhitelistCheck(0, 5, 3, 4, 900)).Should().BeTrue();
        // Nothing answers at all: no network, the mode stays.
        ColituWhitelist.NextState(true, new ColituWhitelistCheck(0, 5, 0, 4, 4000)).Should().BeTrue();
        ColituWhitelist.NextState(true, new ColituWhitelistCheck(1, 5, 4, 4, 300)).Should().BeTrue();
        ColituWhitelist.NextState(true, new ColituWhitelistCheck(4, 5, 4, 4, 300)).Should().BeFalse();
        ColituWhitelist.NextState(false, new ColituWhitelistCheck(5, 5, 4, 4, 300)).Should().BeFalse();
        new ColituWhitelistCheck(1, 5, 3, 4, 10).Flag(true).Should().Be("on:f1/5:d3/4");
    }

    [Fact]
    public void WhitelistProbeParsesAndFallsBackToTheDefault()
    {
        var raw = JsonDocument.Parse("""{"ru":["ya.ru","VK.com","bad host","-x.ru"],"XYZ":["a.b"],"TR":[]}""").RootElement;
        var probe = ColituWhitelist.ParseProbe(raw);
        probe.Keys.Should().BeEquivalentTo(["RU"]);
        probe["RU"].Should().Equal("ya.ru", "vk.com");
        ColituWhitelist.ParseProbe(null).Should().BeEmpty();
        ColituWhitelist.DomesticSet(probe, "ru").Should().Equal("ya.ru", "vk.com");
        ColituWhitelist.DomesticSet(null, "RU").Should().Equal("ya.ru", "vk.com", "gosuslugi.ru", "mail.ru");
        ColituWhitelist.DomesticSet(null, "TR").Should().BeEmpty();
        ColituWhitelist.DomesticSet(probe, null).Should().BeEmpty();
    }

    [Fact]
    public void ForeignSetIsThreeNeutralHostsAndTwoForeignColituNodes()
    {
        var servers = new List<ColituVpnServer>
        {
            Server("ru", "RU", host: "ru.example"),
            Server("bridge", "FI", name: "Finlandiya (Köprü)", host: "bridge.example"),
            Server("wl", "DE", host: "wl.example", whitelist: true),
            Server("nl", "NL", host: "nl.example"),
            Server("de", "DE", host: "de.example", port: 3443),
            Server("se", "SE", host: "se.example"),
            Server("noport", "PL", host: "pl.example", port: null)
        };
        var pings = new Dictionary<string, int?> { ["nl"] = 90, ["de"] = 40, ["noport"] = 5 };
        var set = ColituWhitelist.ForeignSet(servers, server => pings.GetValueOrDefault(server.Id!));
        set.Should().HaveCount(5);
        // Neutral hosts on 443; the Colitu nodes on their own ping port (latency_port), as in the ping round.
        set.Take(3).Should().Equal(ColituWhitelist.NeutralForeignHosts.Select(host => new ColituWhitelistTarget(host, 443)));
        set.Skip(3).Should().Equal(new ColituWhitelistTarget("de.example", 3443), new ColituWhitelistTarget("nl.example", 2053));
    }

    [Fact]
    public void RouteIsABridgeWhenItEntersInTheNetworkCountry()
    {
        var route = Route("r", entry: "RU", exit: "FI", whitelist: true);
        ColituLmPlanner.IsBridge(route, "RU").Should().BeTrue();
        ColituLmPlanner.IsBridge(route, "ru").Should().BeTrue();
        ColituLmPlanner.IsBridge(route, "TR").Should().BeFalse();
        ColituLmPlanner.IsBridge(route, null).Should().BeFalse();
        ColituLmPlanner.IsBridge(Route("s", entry: "RU", exit: "RU", whitelist: true), "RU").Should().BeFalse();
        ColituLmPlanner.IsBridge(Server("b", "FI", name: "Finlandiya (Köprü)"), "TR").Should().BeTrue();
        ColituLmPlanner.WhitelistRoute(route, "RU").Should().BeTrue();
        ColituLmPlanner.WhitelistRoute(Route("u", entry: "RU", exit: "FI", whitelist: false), "RU").Should().BeFalse();
    }

    [Fact]
    public void BridgeRoutesJoinOnlyTheWhitelistCandidates()
    {
        var servers = new List<ColituVpnServer>
        {
            Server(Ru, "RU", whitelist: true, protocols: ["vless-reality"]),
            Route(RouteId, entry: "RU", exit: "FI", whitelist: true),
            Route("00000000-0000-4000-8000-0000000000cc", entry: "DE", exit: "NL", whitelist: true),
            Server(De, "DE", protocols: ["vless-reality"])
        };
        var pings = new Dictionary<string, int?> { [Ru] = 20, [RouteId] = 300, [De] = 30 };
        // Normal mode: routes are never candidates.
        ColituLmPlanner.Candidates(servers, server => pings.GetValueOrDefault(server.Id!), null, (_, _) => [], whitelistMode: false, networkCountry: "RU")
            .Should().NotContain(pair => pair.Server.IsMultihop);
        var (order, decision) = Decide(servers, pings, whitelistMode: true);
        decision.WhitelistNone.Should().BeFalse();
        order.Select(server => server.Id).Should().Equal(RouteId, Ru);
        ColituLmPlanner.WhitelistOrder(servers, server => pings.GetValueOrDefault(server.Id!), "RU").Select(server => server.Id).Should().Equal(RouteId, Ru);
        ColituLmPlanner.WhitelistOrder(servers, server => pings.GetValueOrDefault(server.Id!), "TR").Select(server => server.Id).Should().Equal(Ru);
    }

    [Fact]
    public void WhitelistEventIsNormalized()
    {
        var ok = ColituAcSpec.Normalize(new ColituAcEvent { Kind = "whitelist", State = "ON", ForeignOk = 7, ForeignTotal = 5, DomesticOk = 3, DomesticTotal = 4, Ms = 1200, NodeId = "00000000-0000-4000-8000-0000000000de" });
        ok.Should().NotBeNull();
        ok!.State.Should().Be("on");
        ok.ForeignOk.Should().Be(5);
        ok.NodeId.Should().BeNull();
        var json = JsonSerializer.Serialize(ok);
        json.Should().Contain("\"kind\":\"whitelist\"").And.Contain("\"domestic_total\":4").And.NotContain("node_id");
        ColituAcSpec.Normalize(new ColituAcEvent { Kind = "whitelist", State = "maybe", ForeignOk = 0, ForeignTotal = 5, DomesticOk = 0, DomesticTotal = 4 }).Should().BeNull();
        ColituAcSpec.Normalize(new ColituAcEvent { Kind = "whitelist", State = "off", ForeignOk = 0, ForeignTotal = 0, DomesticOk = 0, DomesticTotal = 4 }).Should().BeNull();
    }

    // ── Russian servers ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("ru", null, true)]
    [InlineData("tr", "RU", true)]
    [InlineData("tr", "TR", false)]
    [InlineData("en", null, false)]
    [InlineData("zh", "ru", true)]
    public void RussianServersAreListedOnlyForRussianLanguageOrNetwork(string language, string? networkCountry, bool visible)
    {
        ColituRuServers.Visible(Server("ru", "RU"), language, networkCountry).Should().Be(visible);
        // Bridges and other countries are always listed.
        ColituRuServers.Visible(Server("fi", "FI", name: "Finlandiya (Köprü)"), language, networkCountry).Should().BeTrue();
        ColituRuServers.Visible(Server("de", "DE"), language, networkCountry).Should().BeTrue();
    }

    [Fact]
    public void BridgeIsRecognisedFromItsNameOrRouteEnds()
    {
        ColituServerNames.IsBridgeName("Finlandiya (Köprü)").Should().BeTrue();
        ColituServerNames.IsBridgeName("Латвия (Мост)").Should().BeTrue();
        ColituServerNames.IsBridgeName("Latvia (Bridge)").Should().BeTrue();
        ColituServerNames.IsBridgeName("Almanya").Should().BeFalse();
        ColituServerNames.IsBridgeName("Almanya (Frankfurt)").Should().BeFalse();

        ColituLmPlanner.Countries(Server("b", "FI", name: "Finlandiya (Köprü)")).Should().Be(("RU", "FI"));
        ColituLmPlanner.Countries(Server("ru", "RU")).Should().Be(("RU", "RU"));
        ColituLmPlanner.IsBridge(Server("de", "DE")).Should().BeFalse();
        var route = new ColituVpnServer { Id = "r", CountryCode = "DE", IsMultihop = true, Entry = new ColituRouteEndpoint { Country = "RU" }, Exit = new ColituRouteEndpoint { Country = "DE" } };
        ColituLmPlanner.Countries(route).Should().Be(("RU", "DE"));
        ColituLmPlanner.ExitsInRussia(Server("ru", "RU")).Should().BeTrue();
        ColituLmPlanner.ExitsInRussia(Server("b", "FI", name: "Finlandiya (Köprü)")).Should().BeFalse();
    }

    [Fact]
    public void Ac3NeverPicksAPlainRussianServerButMayPickABridge()
    {
        var servers = new List<ColituVpnServer> { Server(Ru, "RU"), Server(Bridge, "FI", name: "Finlandiya (Köprü)") };
        var ranked = ColituAdaptiveConnect.Rank(servers, new Dictionary<string, ColituPingSample>(), new ColituAdaptiveMemory(), "wifi|RU-AS1", "RU", DateTimeOffset.UtcNow);
        ranked.Select(server => server.Id).Should().Equal(Bridge);
    }

    [Fact]
    public void Ac4NeverPicksAPlainRussianServerButMayPickABridge()
    {
        var servers = new List<ColituVpnServer> { Server(Ru, "RU", protocols: ["vless-reality"]), Server(Bridge, "FI", name: "Finlandiya (Köprü)", protocols: ["vless-reality"]) };
        // The RU server is much faster; it still never comes first.
        var pings = new Dictionary<string, int?> { [Ru] = 10, [Bridge] = 300 };
        var (order, decision) = Decide(servers, pings, whitelistMode: false);
        decision.Engine.Should().Be("ac4");
        order.Select(server => server.Id).Should().Equal(Bridge);

        var (ruOnly, ruDecision) = Decide([Server(Ru, "RU", protocols: ["vless-reality"])], pings, whitelistMode: false);
        ruOnly.Should().BeEmpty();
        ruDecision.Choice.Should().BeNull();
    }

    [Fact]
    public void WhitelistModeUsesOnlyFlaggedServersBridgesFirst()
    {
        var servers = new List<ColituVpnServer>
        {
            Server(Ru, "RU", whitelist: true, protocols: ["vless-reality"]),
            Server(Bridge, "FI", name: "Finlandiya (Köprü)", whitelist: true, protocols: ["vless-reality"]),
            Server(De, "DE", protocols: ["vless-reality"])
        };
        var pings = new Dictionary<string, int?> { [Ru] = 20, [Bridge] = 400, [De] = 30 };
        var (order, decision) = Decide(servers, pings, whitelistMode: true);
        decision.WhitelistNone.Should().BeFalse();
        order.Select(server => server.Id).Should().Equal(Bridge, Ru);

        // No model yet (AC 3 rules): the same whitelist order.
        ColituLmPlanner.WhitelistOrder(servers, server => pings.GetValueOrDefault(server.Id!)).Select(server => server.Id).Should().Equal(Bridge, Ru);

        // Nothing flagged: normal rules, the notice says so.
        var (normal, none) = Decide([Server(Ru, "RU", protocols: ["vless-reality"]), Server(De, "DE", protocols: ["vless-reality"])], pings, whitelistMode: true);
        none.WhitelistNone.Should().BeTrue();
        normal.Select(server => server.Id).Should().Equal(De);
    }

    // ── Flag off / no model: the old behaviour ──────────────────────────────

    [Fact]
    public void FlagOffOrNoModelKeepsTheAc3Rules()
    {
        var candidates = new List<ColituLmCandidate> { new() { NodeId = De, Protocol = "vless-reality", EntryCountry = "DE", ExitCountry = "DE" } };
        ColituLm.Decide(new ColituLmInput { Model = Model(), EngineEnabled = false, Candidates = candidates }).Engine.Should().Be("ac3");
        ColituLm.Decide(new ColituLmInput { Model = null, EngineEnabled = true, Candidates = candidates }).Engine.Should().Be("ac3");
        ColituLm.Decide(new ColituLmInput { Model = new ColituLmModel { Version = 2 }, EngineEnabled = true, Candidates = candidates }).Engine.Should().Be("ac3");
        ColituLm.Decide(new ColituLmInput { Model = Model(), EngineEnabled = true, Candidates = candidates }).Engine.Should().Be("ac4");
    }

    [Fact]
    public void FeatureSwitchIsReadOnlyFromABoolean()
    {
        static Dictionary<string, JsonElement> Features(string json) =>
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        ColituLmPlanner.ParseFeature(null).Should().BeNull();
        ColituLmPlanner.ParseFeature(Features("""{"adaptive_connect_3":true}""")).Should().BeNull();
        ColituLmPlanner.ParseFeature(Features("""{"ac_engine_4":"yes"}""")).Should().BeNull();
        ColituLmPlanner.ParseFeature(Features("""{"ac_engine_4":false}""")).Should().BeFalse();
        ColituLmPlanner.ParseFeature(Features("""{"ac_engine_4":true}""")).Should().BeTrue();
        // A saved state without the switch: off.
        new ColituVpnSession().AcEngine4Remote.Should().BeNull();
    }

    // ── History, model store, journal ───────────────────────────────────────

    [Fact]
    public void HistoryIsKeptPerNetworkServerAndProtocolAndAges()
    {
        var history = new ColituLmHistory(null);
        var now = DateTimeOffset.UtcNow;
        // Recorded 22 days ago: dropped once newer outcomes come (older than 21 days).
        history.Record("wifi|TR-AS1", Ru, "trojan", ok: true, now - TimeSpan.FromDays(22));
        history.Record("wifi|TR-AS1", De, "vless-reality", ok: true, now - TimeSpan.FromHours(10));
        history.Record("wifi|TR-AS1", De, "vless-reality", ok: false, now - TimeSpan.FromHours(2));
        history.Record("wifi|TR-AS1", De, "hysteria2", ok: false, now);
        history.Record("cellular|TR-AS2", De, "vless-reality", ok: true, now);
        var outcomes = history.Outcomes("wifi|TR-AS1", De.ToUpperInvariant(), "vless-reality", now);
        outcomes.Select(o => (Math.Round(o.AgeH), o.Ok)).Should().Equal((10.0, true), (2.0, false));
        history.Outcomes("cellular|TR-AS2", De, "vless-reality", now).Should().ContainSingle();
        history.Outcomes("other|", De, "vless-reality", now).Should().BeEmpty();
        history.Outcomes("wifi|TR-AS1", Ru, "trojan", now).Should().BeEmpty();
        ColituLmHistory.NetworkHash("wifi|TR-AS1").Should().HaveLength(16).And.NotContain("TR");
    }

    [Fact]
    public void HistoryIsSavedAndLoaded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"colitu-lm-history-{Guid.NewGuid():N}.json");
        try
        {
            var now = DateTimeOffset.UtcNow;
            new ColituLmHistory(path).Record("wifi|X", De, "vless-reality", ok: true, now);
            File.ReadAllText(path).Should().NotContain("wifi|X");
            new ColituLmHistory(path).Outcomes("wifi|X", De, "vless-reality", now).Should().ContainSingle(o => o.Ok);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ModelOfAnotherCountryLeavesTheAc3Rules()
    {
        var russia = new ColituLmModel { Version = 1, Country = "RU" };
        ColituLmPlanner.ModelFor(russia, "RU").Should().BeSameAs(russia);
        ColituLmPlanner.ModelFor(russia, " ru ").Should().BeSameAs(russia);
        ColituLmPlanner.ModelFor(russia, "FI").Should().BeNull();
        ColituLmPlanner.ModelFor(russia, null).Should().BeNull();
        ColituLmPlanner.ModelFor(new ColituLmModel { Version = 1, Country = "" }, "RU").Should().BeNull("asked through the tunnel it has no priors");
        ColituLmPlanner.ModelFor(null, "RU").Should().BeNull();
        ColituLm.Decide(new ColituLmInput { Model = ColituLmPlanner.ModelFor(russia, "FI"), EngineEnabled = true, Candidates = [] }).Engine.Should().Be(ColituLm.EngineAc3);
    }

    [Fact]
    public void ModelStoreFetchesWhenMissingStaleOrForAnotherCountry()
    {
        var store = new ColituLmModelStore(null);
        var now = DateTimeOffset.UtcNow;
        store.TakeFetchSlot(now, "TR").Should().BeTrue();
        store.TakeFetchSlot(now + TimeSpan.FromMinutes(1), "TR").Should().BeFalse("at most every 10 minutes");
        store.Set(new ColituLmModel { Version = 1, Country = "TR" }, now);
        store.TakeFetchSlot(now + TimeSpan.FromMinutes(30), "TR").Should().BeFalse();
        store.TakeFetchSlot(now + TimeSpan.FromMinutes(30), "RU").Should().BeTrue();
        store.Set(new ColituLmModel { Version = 1, Country = "RU" }, now);
        store.TakeFetchSlot(now + TimeSpan.FromMinutes(50), "RU").Should().BeFalse();
        // Refreshed every hour (the panel rebuilds the priors hourly).
        store.TakeFetchSlot(now + TimeSpan.FromMinutes(61), "RU").Should().BeTrue();
        store.AgeH(now + TimeSpan.FromHours(7)).Should().BeApproximately(7, 1e-6);
        // Offline: the last model is still there; the decision calls it stale only after 6 h (vectors).
        store.Model.Should().NotBeNull();
        ColituLm.Decide(new ColituLmInput { Model = Model(), ModelAgeH = 2, EngineEnabled = true, Candidates = [] }).Stale.Should().BeFalse();
        ColituLm.Decide(new ColituLmInput { Model = Model(), ModelAgeH = 7, EngineEnabled = true, Candidates = [] }).Stale.Should().BeTrue();
    }

    [Fact]
    public void CityScopeIsReadForTheJournalOnly()
    {
        var model = JsonSerializer.Deserialize<ColituLmModel>("""
            {"version":1,"country":"RU","city":"Moscow","priors":{},"weights":{},
             "prior_scope":{"RU":{"00000000-0000-4000-8000-00000000004e":{"scope":"city","city":"Moscow"},
                                  "00000000-0000-4000-8000-0000000000de":{"scope":"country"}}}}
            """)!;
        model.City.Should().Be("Moscow");
        model.ScopeOf("ru", "00000000-0000-4000-8000-00000000004E").Should().Be("city:Moscow");
        model.ScopeOf("RU", "00000000-0000-4000-8000-0000000000de").Should().Be("country");
        model.ScopeOf("RU", "00000000-0000-4000-8000-0000000000ff").Should().BeNull();
        model.ScopeOf("TR", "00000000-0000-4000-8000-00000000004e").Should().BeNull();
        model.ScopeCounts("RU").Should().Be((1, 1));
        new ColituLmModel().ScopeCounts("RU").Should().Be((0, 0));
    }

    [Fact]
    public void JournalKeepsTheLast300RecordsAsJson()
    {
        var journal = new ColituEngineJournal();
        for (var i = 0; i < 310; i++)
        {
            journal.Add(ColituEngineJournal.Decision, new { index = i, choice = (string?)null });
        }
        journal.Count.Should().Be(300);
        var snapshot = journal.Snapshot();
        snapshot[0].Data!["index"]!.GetValue<int>().Should().Be(10);
        snapshot[^1].Data!["index"]!.GetValue<int>().Should().Be(309);
        var json = JsonDocument.Parse(journal.ToJson()).RootElement;
        json.GetArrayLength().Should().Be(300);
        json[0].GetProperty("kind").GetString().Should().Be("decision");
        json[0].GetProperty("data").TryGetProperty("choice", out _).Should().BeFalse();
        journal.Clear();
        journal.Count.Should().Be(0);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private const string Ru = "00000000-0000-4000-8000-0000000000aa";
    private const string Bridge = "00000000-0000-4000-8000-0000000000bb";
    private const string De = "00000000-0000-4000-8000-0000000000de";

    private static ColituLmModel Model() => new()
    {
        Version = 1,
        Country = "RU",
        Weights = new ColituLmWeights(),
        Priors = [],
        ProtocolFit = [],
        Explore = 0
    };

    private const string RouteId = "00000000-0000-4000-8000-0000000000bf";

    private static ColituVpnServer Server(string id, string country, string? name = null, string? host = null, bool whitelist = false, List<string>? protocols = null, int? port = 2053) => new()
    {
        Id = id,
        Name = name ?? id,
        DisplayName = name ?? id,
        CountryCode = country,
        Host = host,
        Port = port,
        WhitelistOk = whitelist,
        Protocols = protocols ?? [],
        Available = true
    };

    private static ColituVpnServer Route(string id, string entry, string exit, bool whitelist) => new()
    {
        Id = id,
        Name = id,
        CountryCode = exit,
        IsMultihop = true,
        Entry = new ColituRouteEndpoint { Country = entry },
        Exit = new ColituRouteEndpoint { Country = exit },
        WhitelistOk = whitelist,
        Protocols = ["vless-reality"],
        Available = true
    };

    private static (List<ColituVpnServer> Order, ColituLmDecision Decision) Decide(List<ColituVpnServer> servers, Dictionary<string, int?> pings, bool whitelistMode)
    {
        var pairs = ColituLmPlanner.Candidates(servers, server => pings.GetValueOrDefault(server.Id!), null, (_, _) => [], whitelistMode, "RU");
        var decision = ColituLm.Decide(new ColituLmInput
        {
            Model = Model(),
            EngineEnabled = true,
            Country = "RU",
            NetworkType = "cellular",
            WhitelistMode = whitelistMode,
            Seed = 1,
            Candidates = pairs.Select(pair => pair.Candidate).ToList()
        });
        return (ColituLmPlanner.Order(decision, pairs).Servers, decision);
    }
}

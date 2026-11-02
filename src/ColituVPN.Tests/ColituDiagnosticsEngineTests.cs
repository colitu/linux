using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>The AC4 <c>engine</c> section of the support report: schema, device-side limits and masking.</summary>
public class ColituDiagnosticsEngineTests
{
    private static readonly string[] ColituHosts = ["fi4.node.example", "203.0.113.42"];

    private static List<ColituEngineJournalEntry> Journal(Action<ColituEngineJournal> fill)
    {
        var journal = new ColituEngineJournal();
        fill(journal);
        return journal.Snapshot();
    }

    private static object Decision(int index, int candidates = 3) => new
    {
        engine = "ac4",
        reason = "score",
        index,
        candidates = Enumerable.Range(0, candidates).Select(i => new
        {
            node_id = $"00000000-0000-4000-8000-{i:D12}",
            protocol = "vless-reality",
            entry = "DE",
            exit = "DE",
            latency_ms = 50.0 + i,
            history = 2,
            scored = new
            {
                features = new { latency = 0.95, own = 0.5, prior = 0.5, hint = 0, recommended = 0, load = 0, protocol_fit = 0.5, whitelist = 0 },
                alpha = 1.123456789,
                beta = 1.0,
                log_odds = 0.1,
                score = 3.0 - i * 0.01,
                eligible = true,
                tier = 0,
                theta = (double?)null
            }
        }).ToList(),
        ranking = Enumerable.Range(0, candidates).Reverse().ToList(),
        choice = new { node_id = "00000000-0000-4000-8000-000000000000", protocol = "vless-reality" }
    };

    [Fact]
    public void SectionMatchesTheSchema()
    {
        var journal = Journal(j =>
        {
            j.Add(ColituEngineJournal.PingRound, new { vpn_off = true, total = 10, ok = 9, failed = 1 });
            j.Add(ColituEngineJournal.Decision, Decision(1));
            j.Add(ColituEngineJournal.Outcome, new { node_id = "00000000-0000-4000-8000-000000000001", protocol = "hysteria2", outcome = "fail", reason = "no_traffic" });
            j.Add(ColituEngineJournal.Hidden, new { reason = "unreachable_vpn_off", servers = new[] { "a" }, of = 10 });
            j.Add(ColituEngineJournal.Whitelist, new { foreign_ok = 0, foreign_total = 5, domestic_ok = 3, domestic_total = 4, state = "on" });
        });
        var hidden = new object[] { new { kind = "hidden_now", node_id = "x", name = "Almanya", country = "DE" } };
        var flags = new Dictionary<string, object?> { ["ac_engine_4"] = true, ["whitelist_mode"] = false, ["model_country"] = null };
        var engine = ColituDiagnosticsEngine.Build(journal, hidden, "1 2026-11-02T03:00:00Z", flags, ColituHosts)!;

        engine.Select(pair => pair.Key).Should().BeEquivalentTo(["model_version", "flags", "decisions", "ping_rounds", "hidden_servers", "whitelist_checks"]);
        engine["model_version"]!.GetValue<string>().Should().Be("1 2026-11-02T03:00:00Z");
        engine["flags"]!["ac_engine_4"]!.GetValue<bool>().Should().BeTrue();
        var decisions = engine["decisions"]!.AsArray();
        decisions.Should().HaveCount(2);
        // Newest first: the outcome came after the decision.
        decisions[0]!["kind"]!.GetValue<string>().Should().Be("outcome");
        var decision = decisions[1]!.AsObject();
        decision["candidates_total"]!.GetValue<int>().Should().Be(3);
        // Compact candidates in ranking order with the eight features.
        decision["candidates"]![0]!["i"]!.GetValue<int>().Should().Be(2);
        decision["candidates"]![0]!["f"]!.AsArray().Should().HaveCount(8);
        decision["candidates"]![0]!["alpha"]!.GetValue<double>().Should().Be(1.1235);
        engine["ping_rounds"]!.AsArray().Should().ContainSingle();
        engine["hidden_servers"]!.AsArray().Should().HaveCount(2);
        engine["whitelist_checks"]!.AsArray().Should().ContainSingle();

        var body = JsonDocument.Parse(ColituDiagnosticsAsker.BuildUploadBody("text", "linux", "1.6.3", "Win 11", engine)).RootElement;
        body.GetProperty("report").GetString().Should().Be("text");
        body.GetProperty("engine").GetProperty("decisions").GetArrayLength().Should().Be(2);
        JsonDocument.Parse(ColituDiagnosticsAsker.BuildUploadBody("text", "linux", "1.6.3", "Win 11")).RootElement
            .TryGetProperty("engine", out _).Should().BeFalse();
    }

    [Fact]
    public void NothingToSendGivesNoSection() =>
        ColituDiagnosticsEngine.Build([], null, null, null, null).Should().BeNull();

    [Fact]
    public void CountLimitsKeepTheNewest()
    {
        var journal = Journal(j =>
        {
            for (var i = 0; i < 280; i++) j.Add(ColituEngineJournal.Decision, new { index = i });
            for (var i = 0; i < 20; i++) j.Add(ColituEngineJournal.PingRound, new { index = i });
        });
        var flags = Enumerable.Range(0, 100).ToDictionary(i => $"flag{i}", i => (object?)i);
        var engine = ColituDiagnosticsEngine.Build(journal, null, new string('v', 100), flags, ColituHosts)!;
        engine["model_version"]!.GetValue<string>().Should().HaveLength(64);
        engine["flags"]!.AsObject().Count.Should().Be(64);
        engine["decisions"]!.AsArray().Should().HaveCount(280);
        engine["decisions"]![0]!["index"]!.GetValue<int>().Should().Be(279);

        var many = Journal(j =>
        {
            for (var i = 0; i < 300; i++) j.Add(ColituEngineJournal.PingRound, new { index = i });
        });
        var rounds = ColituDiagnosticsEngine.Build(many, null, null, null, null)!["ping_rounds"]!.AsArray();
        rounds.Should().HaveCount(60);
        rounds[0]!["index"]!.GetValue<int>().Should().Be(299);
        rounds[^1]!["index"]!.GetValue<int>().Should().Be(240);

        var hidden = Enumerable.Range(0, 250).Select(i => (object)new { node_id = $"n{i}" });
        ColituDiagnosticsEngine.Build([], hidden, null, null, null)!["hidden_servers"]!.AsArray().Should().HaveCount(200);
    }

    [Fact]
    public void ItemsAreCutTo4KiBAndTheWholeTo192KiB()
    {
        // A decision with 200 candidates: its candidate list is halved until the item fits.
        var big = Journal(j => j.Add(ColituEngineJournal.Decision, Decision(0, candidates: 200)));
        var item = ColituDiagnosticsEngine.Build(big, null, null, null, null)!["decisions"]![0]!.AsObject();
        Encoding.UTF8.GetByteCount(item.ToJsonString()).Should().BeLessThanOrEqualTo(ColituDiagnosticsEngine.MaxItemBytes);
        item["candidates_total"]!.GetValue<int>().Should().Be(200);
        item["candidates_cut"]!.GetValue<bool>().Should().BeTrue();
        item["candidates"]!.AsArray().Count.Should().BeInRange(1, 199);
        // The best-ranked candidate survives the cut.
        item["candidates"]![0]!["i"]!.GetValue<int>().Should().Be(199);

        var huge = Journal(j => j.Add(ColituEngineJournal.Whitelist, new { blob = new string('x', 5000) }));
        // An item that cannot be cut is dropped (here nothing is left to send).
        ColituDiagnosticsEngine.Build(huge, null, null, null, null).Should().BeNull();

        // 300 decisions of ~3 KiB each are over 192 KiB: the oldest decisions go, the newest stay.
        var full = Journal(j =>
        {
            for (var i = 0; i < 300; i++) j.Add(ColituEngineJournal.Decision, new { index = i, pad = new string('p', 3000) });
        });
        var engine = ColituDiagnosticsEngine.Build(full, null, null, null, null)!;
        Encoding.UTF8.GetByteCount(engine.ToJsonString()).Should().BeLessThanOrEqualTo(ColituDiagnosticsEngine.MaxTotalBytes);
        var decisions = engine["decisions"]!.AsArray();
        decisions.Count.Should().BeInRange(50, 70);
        decisions[0]!["index"]!.GetValue<int>().Should().Be(299);
    }

    [Fact]
    public void SitesAndForeignAddressesAreMaskedColituHostsStay()
    {
        var journal = Journal(j => j.Add(ColituEngineJournal.Whitelist, new
        {
            host = "www.google.com",
            node = "fi4.node.example",
            entry = "203.0.113.42",
            foreign_ip = "203.0.113.7",
            check = "ya.ru",
            note = "dial tcp 198.51.100.9:443: i/o timeout",
            node_id = "00000000-0000-4000-8000-0000000000de",
            at_text = "2026-11-02T03:00:00Z",
            version = "1.6.3",
            nested = new { sites = new[] { "instagram.com", "fi4.node.example" } }
        }));
        var item = ColituDiagnosticsEngine.Build(journal, null, null, null, ColituHosts)!["whitelist_checks"]![0]!.AsObject();
        item["host"]!.GetValue<string>().Should().Be("<site>");
        item["node"]!.GetValue<string>().Should().Be("fi4.node.example");
        item["entry"]!.GetValue<string>().Should().Be("203.0.113.42");
        item["foreign_ip"]!.GetValue<string>().Should().Be("<ip>");
        item["check"]!.GetValue<string>().Should().Be("ya.ru");
        item["note"]!.GetValue<string>().Should().NotContain("198.51.100.9");
        item["node_id"]!.GetValue<string>().Should().Be("00000000-0000-4000-8000-0000000000de");
        item["at_text"]!.GetValue<string>().Should().Be("2026-11-02T03:00:00Z");
        item["version"]!.GetValue<string>().Should().Be("1.6.3");
        item["nested"]!["sites"]![0]!.GetValue<string>().Should().Be("<site>");
        item["nested"]!["sites"]![1]!.GetValue<string>().Should().Be("fi4.node.example");
    }
}

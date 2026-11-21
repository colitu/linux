using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>Whitelist trigger (WHITELIST-TRIGGER-SPEC, 2026-11-17): the failure ledger (T1), the ping rule (T2) and the switch.</summary>
public class ColituWhitelistTriggerTests
{
    private const string Net = "cellular|AS12389";
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly ColituWhitelistFailure S = ColituWhitelistFailure.Server;

    [Fact]
    public void ThreeServersInTwoCountriesAreDue()
    {
        var ledger = new ColituWhitelistLedger();
        ledger.Note(Net, "de-1", "DE", S).Should().BeTrue();
        ledger.Note(Net, "nl-1", "NL", S).Should().BeTrue();
        ledger.ServersDue(Net).Should().BeFalse();
        ledger.Note(Net, "de-2", "DE", S).Should().BeTrue();
        ledger.ServersDue(Net).Should().BeTrue();
    }

    [Fact]
    public void ThreeServersInOneCountryAreNotDue()
    {
        var ledger = new ColituWhitelistLedger();
        ledger.Note(Net, "de-1", "DE", S);
        ledger.Note(Net, "de-2", "de", S);
        ledger.Note(Net, "de-3", "DE", S);
        ledger.ServersDue(Net).Should().BeFalse();
    }

    [Fact]
    public void TheSameServerThreeTimesIsNotDue()
    {
        var ledger = new ColituWhitelistLedger();
        ledger.Note(Net, "de-1", "DE", S).Should().BeTrue();
        ledger.Note(Net, "DE-1", "DE", S).Should().BeFalse();
        ledger.Note(Net, "de-1", "NL", S).Should().BeFalse();
        ledger.Count.Should().Be(1);
        ledger.ServersDue(Net).Should().BeFalse();
    }

    [Theory]
    [InlineData(ColituWhitelistFailure.Auth)]
    [InlineData(ColituWhitelistFailure.Quota)]
    [InlineData(ColituWhitelistFailure.Crash)]
    [InlineData(ColituWhitelistFailure.NetworkChange)]
    [InlineData(ColituWhitelistFailure.NoNetwork)]
    [InlineData(ColituWhitelistFailure.Cancel)]
    public void FailuresThatDoNotBlameTheServerAreNotCounted(ColituWhitelistFailure kind)
    {
        var ledger = new ColituWhitelistLedger();
        ledger.Note(Net, "de-1", "DE", kind).Should().BeFalse();
        ledger.Note(Net, "nl-1", "NL", kind).Should().BeFalse();
        ledger.Note(Net, "fi-1", "FI", kind).Should().BeFalse();
        ledger.Count.Should().Be(0);
        ledger.ServersDue(Net).Should().BeFalse();
    }

    [Fact]
    public void FailuresAreClassifiedWithTheConnectCounterExclusions()
    {
        var noTraffic = new ColituConnectException("unreachable");
        ColituWhitelistLedger.Classify(noTraffic, credentialsRefused: false, networkChanged: false, hasNetwork: true).Should().Be(ColituWhitelistFailure.Server);
        ColituWhitelistLedger.Classify(noTraffic, credentialsRefused: true, networkChanged: false, hasNetwork: true).Should().Be(ColituWhitelistFailure.Auth);
        ColituWhitelistLedger.Classify(noTraffic, credentialsRefused: false, networkChanged: true, hasNetwork: true).Should().Be(ColituWhitelistFailure.NetworkChange);
        ColituWhitelistLedger.Classify(noTraffic, credentialsRefused: false, networkChanged: false, hasNetwork: false).Should().Be(ColituWhitelistFailure.NoNetwork);
        ColituWhitelistLedger.Classify(new ColituConnectException("offline", offline: true), false, false, true).Should().Be(ColituWhitelistFailure.NoNetwork);
        ColituWhitelistLedger.Classify(new ColituPlanRequiredException(), false, false, true).Should().Be(ColituWhitelistFailure.Quota);
        ColituWhitelistLedger.Classify(new ColituApiException(HttpStatusCode.PaymentRequired, "plan", "ENTITLEMENT_EXPIRED"), false, false, true).Should().Be(ColituWhitelistFailure.Quota);
        ColituWhitelistLedger.Classify(new ColituApiException(HttpStatusCode.Unauthorized, "auth"), false, false, true).Should().Be(ColituWhitelistFailure.Auth);
        ColituWhitelistLedger.Classify(new InvalidOperationException("VPN core failed to start"), false, false, true).Should().Be(ColituWhitelistFailure.Crash);
        ColituWhitelistLedger.Classify(new OperationCanceledException(), false, false, true).Should().Be(ColituWhitelistFailure.Cancel);
    }

    [Fact]
    public void SuccessResetsTheLedger()
    {
        var ledger = Due();
        ledger.Reset();
        ledger.Count.Should().Be(0);
        ledger.ServersDue(Net).Should().BeFalse();
    }

    [Fact]
    public void NetworkChangeResetsTheLedger()
    {
        var ledger = Due();
        ledger.ServersDue("wifi|AS3216").Should().BeFalse();
        ledger.Count.Should().Be(0);
        ledger.Note(Net, "de-1", "DE", S);
        ledger.ServersDue(Net).Should().BeFalse();
    }

    [Fact]
    public void AfterANormalCheckOneMoreServerIsNeeded()
    {
        var ledger = Due();
        ledger.CheckDone(Net, whitelist: false);
        ledger.ServersDue(Net).Should().BeFalse();
        // The same server again is not "one more".
        ledger.Note(Net, "nl-1", "NL", S);
        ledger.ServersDue(Net).Should().BeFalse();
        ledger.Note(Net, "de-2", "DE", S);
        ledger.ServersDue(Net).Should().BeTrue();
    }

    [Fact]
    public void EnteringWhitelistModeClearsTheLedger()
    {
        var ledger = Due();
        ledger.CheckDone(Net, whitelist: true);
        ledger.Count.Should().Be(0);
        ledger.ServersDue(Net).Should().BeFalse();
    }

    [Fact]
    public void ChecksAreDebouncedSixtySecondsPerNetwork()
    {
        var ledger = new ColituWhitelistLedger();
        ledger.TryTakeCheck(Net, T0).Should().BeTrue();
        ledger.TryTakeCheck(Net, T0.AddSeconds(30)).Should().BeFalse();
        ledger.TryTakeCheck(Net, T0.AddSeconds(59)).Should().BeFalse();
        ledger.TryTakeCheck(Net, T0.AddSeconds(60)).Should().BeTrue();
        // Another network has its own minute.
        ledger.TryTakeCheck("wifi|AS3216", T0.AddSeconds(61)).Should().BeTrue();
    }

    // ── Ping rule (T2) ──────────────────────────────────────────────────────

    [Fact]
    public void FourForeignServersInTwoCountriesWithoutAnswerAreDue()
    {
        var servers = new[] { Node("de-1", "DE"), Node("de-2", "DE"), Node("nl-1", "NL"), Node("fi-1", "FI") };
        ColituWhitelistLedger.PingsDue(servers, Pings(servers, null)).Should().BeTrue();
    }

    [Fact]
    public void ThreeFailedPingsAreNotDue()
    {
        var servers = new[] { Node("de-1", "DE"), Node("nl-1", "NL"), Node("fi-1", "FI") };
        ColituWhitelistLedger.PingsDue(servers, Pings(servers, null)).Should().BeFalse();
    }

    [Fact]
    public void OneAnsweredPingIsNotDue()
    {
        var servers = new[] { Node("de-1", "DE"), Node("de-2", "DE"), Node("nl-1", "NL"), Node("fi-1", "FI") };
        var pings = Pings(servers, null);
        pings["fi-1"] = 80;
        ColituWhitelistLedger.PingsDue(servers, pings).Should().BeFalse();
    }

    [Fact]
    public void FourFailedPingsInOneCountryAreNotDue()
    {
        var servers = new[] { Node("de-1", "DE"), Node("de-2", "DE"), Node("de-3", "DE"), Node("de-4", "DE") };
        ColituWhitelistLedger.PingsDue(servers, Pings(servers, null)).Should().BeFalse();
    }

    [Fact]
    public void RussianAndMultihopServersAreIgnoredByThePingRule()
    {
        var servers = new List<ColituVpnServer>
        {
            Node("de-1", "DE"), Node("nl-1", "NL"), Node("fi-1", "FI"),
            Node("ru-1", "RU"),
            Node("br-1", "FI", name: "Finlandiya (Köprü)"),
            new() { Id = "route-1", Name = "route-1", CountryCode = "NL", IsMultihop = true, Entry = new ColituRouteEndpoint { Country = "DE" }, Exit = new ColituRouteEndpoint { Country = "NL" }, Available = true }
        };
        // Only three foreign plain servers failed: the Russian one, the bridge (enters in Russia) and the route do not count.
        ColituWhitelistLedger.PingsDue(servers, Pings(servers, null)).Should().BeFalse();
        // A Russian server answering does not stop the rule either.
        servers.Add(Node("se-1", "SE"));
        var pings = Pings(servers, null);
        pings["ru-1"] = 15;
        ColituWhitelistLedger.PingsDue(servers, pings).Should().BeTrue();
    }

    // ── The switch ──────────────────────────────────────────────────────────

    [Fact]
    public void WhitelistResultPicksTheBeyazListeServer()
    {
        const string beyaz = "00000000-0000-4000-8000-00000000b11e";
        var candidates = new List<ColituLmCandidate>
        {
            new() { NodeId = "00000000-0000-4000-8000-0000000000de", Protocol = "vless-reality", EntryCountry = "DE", ExitCountry = "DE", LatencyMs = 40 },
            new() { NodeId = "00000000-0000-4000-8000-0000000000a1", Protocol = "hysteria2", EntryCountry = "NL", ExitCountry = "NL", LatencyMs = 50 },
            new() { NodeId = beyaz, Protocol = "vless-ws", EntryCountry = "RU", ExitCountry = "GB", WhitelistOk = true, LatencyMs = 300 }
        };
        var check = new ColituWhitelistCheck(0, 5, 3, 4, 900);
        var mode = ColituWhitelist.NextState(false, check);
        mode.Should().BeTrue();
        var decision = ColituLm.Decide(Input(candidates, mode));
        decision.WhitelistNone.Should().BeFalse();
        decision.Choice.Should().NotBeNull();
        candidates[decision.Choice!.Value].NodeId.Should().Be(beyaz);
        candidates[decision.Choice!.Value].Protocol.Should().Be("vless-ws");
    }

    [Fact]
    public void NormalResultKeepsTheModeOff()
    {
        ColituWhitelist.NextState(false, new ColituWhitelistCheck(4, 5, 4, 4, 300)).Should().BeFalse();
        ColituWhitelist.NextState(false, new ColituWhitelistCheck(0, 5, 1, 4, 4000)).Should().BeFalse();
    }

    private static ColituWhitelistLedger Due()
    {
        var ledger = new ColituWhitelistLedger();
        ledger.Note(Net, "de-1", "DE", S);
        ledger.Note(Net, "nl-1", "NL", S);
        ledger.Note(Net, "pl-1", "PL", S);
        ledger.ServersDue(Net).Should().BeTrue();
        return ledger;
    }

    private static ColituVpnServer Node(string id, string country, string? name = null) => new()
    {
        Id = id,
        Name = name ?? id,
        DisplayName = name ?? id,
        CountryCode = country,
        Host = id + ".example",
        Port = 443,
        Available = true
    };

    private static Dictionary<string, int?> Pings(IEnumerable<ColituVpnServer> servers, int? ms) =>
        servers.ToDictionary(server => server.Id!, _ => ms, StringComparer.OrdinalIgnoreCase);

    private static ColituLmInput Input(List<ColituLmCandidate> candidates, bool whitelistMode) => new()
    {
        Model = new ColituLmModel { Version = 1, Country = "RU", Weights = new ColituLmWeights(), Priors = [], ProtocolFit = [], Explore = 0 },
        EngineEnabled = true,
        Country = "RU",
        NetworkType = "cellular",
        WhitelistMode = whitelistMode,
        Seed = 1,
        Candidates = candidates
    };
}

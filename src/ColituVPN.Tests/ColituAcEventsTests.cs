using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>Adaptive Connect / connection telemetry (POST /client/ac-events): JSON shape, reason mapping, the queue.</summary>
public class ColituAcEventsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 30, 0, TimeSpan.Zero);
    private const string NodeA = "0b7e7a52-3d0c-4d8e-9a77-5c1f2b6a1e01";
    private const string NodeB = "8F3C1D90-5B2A-4C77-8E11-0A9D4C2B7F02";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "colitu-ac-" + Guid.NewGuid().ToString("N"));

    public ColituAcEventsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ColituInsights Create() => new(() => Path.Combine(_dir, "insights.json"));

    private static JsonElement Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    private static ColituAcEvent Probe(string node, int ms = 40, string? protocol = null) =>
        new() { Kind = "probe", NodeId = node, Protocol = protocol, Outcome = "ok", Ms = ms };

    // ── Event shapes ───────────────────────────────────────────────────────
    [Fact]
    public void Connect_Event_HasTheContractedFields()
    {
        var insights = Create();
        insights.AddEvent(new ColituAcEvent
        {
            Kind = "connect", NodeId = NodeB, Protocol = "Hysteria2", Outcome = "ok", Ms = 1234, FirstTry = true, Mode = "manual",
            Reason = "timeout"   // only a failure carries a reason
        }, Now);
        var batch = insights.BuildEventBatch(Now, "2.9.1", "tok", "wifi")!;
        var item = Json(batch.Events[0]);
        item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("kind", "node_id", "protocol", "outcome", "ms", "first_try", "mode");
        item.GetProperty("node_id").GetString().Should().Be(NodeB.ToLowerInvariant());
        item.GetProperty("protocol").GetString().Should().Be("hysteria2");
        item.GetProperty("first_try").GetBoolean().Should().BeTrue();
        item.GetProperty("mode").GetString().Should().Be("manual");
    }

    [Fact]
    public void Connect_Failure_CarriesAReason_AndDefaultsToOther()
    {
        var insights = Create();
        insights.AddEvent(new ColituAcEvent { Kind = "connect", NodeId = NodeA, Outcome = "fail", Reason = "tls", Mode = "auto" }, Now);
        insights.AddEvent(new ColituAcEvent { Kind = "connect", NodeId = NodeA, Outcome = "fail", Reason = "made-up", Mode = "auto" }, Now);
        var events = insights.BuildEventBatch(Now, "2.9.1", "", "")!.Events;
        events[0].Reason.Should().Be("tls");
        events[1].Reason.Should().Be("other");
        events[0].FirstTry.Should().BeFalse();
    }

    [Fact]
    public void Recovery_Switch_Drop_Choice_Probe_Shapes()
    {
        var insights = Create();
        insights.AddEvent(new ColituAcEvent { Kind = "recovery", NodeId = NodeA, Protocol = "hysteria2", ToProtocol = "vless-reality", Outcome = "ok", Reason = "timeout", Ms = 4200, DetectMs = 3100, Spare = false }, Now);
        insights.AddEvent(new ColituAcEvent { Kind = "switch", Protocol = "hysteria2", ToProtocol = "trojan", Outcome = "ok", Flap = true }, Now);
        insights.AddEvent(new ColituAcEvent { Kind = "drop", NodeId = NodeA, Protocol = "trojan", Reason = "reset", Minutes = 42 }, Now);
        insights.AddEvent(new ColituAcEvent { Kind = "choice", NodeId = NodeA, Ms = 80, BestNodeId = NodeB, BestMs = 35, Mode = "manual" }, Now);
        insights.AddEvent(new ColituAcEvent { Kind = "probe", NodeId = NodeA, Outcome = "fail", Ms = 99 }, Now);
        var events = insights.BuildEventBatch(Now, "2.9.1", "", "")!.Events.Select(Json).ToList();

        events.Select(item => item.GetProperty("kind").GetString()).Should().Equal("recovery", "switch", "drop", "choice", "probe");
        events[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("kind", "node_id", "protocol", "to_protocol", "outcome", "reason", "ms", "detect_ms", "spare");
        events[1].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("kind", "protocol", "to_protocol", "outcome", "flap");
        events[1].GetProperty("flap").GetBoolean().Should().BeTrue();
        events[2].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("kind", "node_id", "protocol", "reason", "minutes");
        events[3].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("kind", "node_id", "ms", "best_node_id", "best_ms", "mode");
        events[3].GetProperty("mode").GetString().Should().Be("auto");
        // A failed probe has no time.
        events[4].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("kind", "node_id", "outcome");
    }

    [Fact]
    public void Choice_BestIsNeverAboveTheChosenPing()
    {
        var insights = Create();
        insights.AddEvent(new ColituAcEvent { Kind = "choice", NodeId = NodeA, Ms = 50, BestNodeId = NodeB, BestMs = 90 }, Now);
        var item = insights.BuildEventBatch(Now, "2.9.1", "", "")!.Events[0];
        item.BestMs.Should().Be(50);
    }

    [Theory]
    [InlineData("connect", null, "ok")]                       // no node
    [InlineData("connect", "not-a-uuid", "ok")]
    [InlineData("connect", NodeA, "maybe")]                    // unknown outcome
    [InlineData("probe", "route-1", "ok")]
    [InlineData("teleport", NodeA, "ok")]
    public void InvalidEvents_AreNotQueued(string kind, string? node, string outcome)
    {
        var insights = Create();
        insights.AddEvent(new ColituAcEvent { Kind = kind, NodeId = node, Outcome = outcome, Mode = "auto" }, Now);
        insights.QueuedEvents.Should().Be(0);
    }

    [Fact]
    public void Values_AreClamped_AndUnknownProtocolsDropped()
    {
        var insights = Create();
        insights.AddEvent(new ColituAcEvent { Kind = "connect", NodeId = NodeA, Protocol = "wireguard", Outcome = "ok", Ms = 9_999_999, Mode = "auto" }, Now);
        insights.AddEvent(new ColituAcEvent { Kind = "connect", NodeId = NodeA, Protocol = "tuic", Outcome = "ok", Ms = -5, Mode = "auto" }, Now);
        var events = insights.BuildEventBatch(Now, "2.9.1", "", "")!.Events;
        events[0].Protocol.Should().BeNull();
        events[0].Ms.Should().Be(600_000);
        events[1].Protocol.Should().Be("tuic");
        events[1].Ms.Should().Be(0);
    }

    // ── Request ────────────────────────────────────────────────────────────
    [Fact]
    public void Request_HasTheContractedEnvelope()
    {
        var insights = Create();
        insights.AddEvent(Probe(NodeA), Now);
        var batch = insights.BuildEventBatch(Now, "2.9.1", "net-token", "cellular")!;
        var root = Json(batch.Body);
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("platform", "app_version", "network_token", "network_type", "events");
        root.GetProperty("platform").GetString().Should().Be("linux");
        root.GetProperty("app_version").GetString().Should().Be("2.9.1");
        root.GetProperty("network_token").GetString().Should().Be("net-token");
        root.GetProperty("network_type").GetString().Should().Be("cellular");
        root.GetProperty("events").GetArrayLength().Should().Be(1);

        var unknown = Json(insights.BuildEventBatch(Now, "2.9.1", "", "none")!.Body);
        unknown.GetProperty("network_token").GetString().Should().BeEmpty();
        unknown.GetProperty("network_type").GetString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("wifi", "wifi")]
    [InlineData("Ethernet", "ethernet")]
    [InlineData("cellular", "cellular")]
    [InlineData("other", "other")]
    [InlineData("none", "")]
    [InlineData(null, "")]
    public void NetworkType_IsOneOfTheFourOrEmpty(string? link, string expected)
    {
        ColituAcSpec.NetworkType(link).Should().Be(expected);
    }

    // ── Queue ──────────────────────────────────────────────────────────────
    [Fact]
    public void Queue_IsCappedAtAThousand_DroppingTheOldest()
    {
        var insights = Create();
        for (var index = 0; index < 1100; index++)
        {
            insights.AddEvent(new ColituAcEvent { Kind = "drop", NodeId = NodeA, Reason = "other", Minutes = index }, Now);
        }
        insights.QueuedEvents.Should().Be(1000);
        // The first 100 are gone: the oldest left is #100.
        insights.BuildEventBatch(Now, "2.9.1", "", "")!.Events[0].Minutes.Should().Be(100);
    }

    [Fact]
    public void Batch_HoldsAtMost300_OldestFirst_AndCommitRemovesExactlyThose()
    {
        var insights = Create();
        for (var index = 0; index < 450; index++)
        {
            insights.AddEvent(new ColituAcEvent { Kind = "drop", NodeId = NodeA, Reason = "other", Minutes = index }, Now);
        }
        var batch = insights.BuildEventBatch(Now, "2.9.1", "", "")!;
        batch.Events.Should().HaveCount(300);
        batch.Events[0].Minutes.Should().Be(0);

        insights.AddEvent(new ColituAcEvent { Kind = "drop", NodeId = NodeA, Reason = "other", Minutes = 999 }, Now);   // during the send
        insights.CommitEvents(batch, Now);
        insights.QueuedEvents.Should().Be(151);
        insights.BuildEventBatch(Now + ColituAcSpec.MinSendInterval, "2.9.1", "", "")!.Events[0].Minutes.Should().Be(300);
    }

    [Fact]
    public void Send_IsDueHourly_AndAfterAFailureWaitsHalfAnHour()
    {
        var insights = Create();
        insights.AddEvent(Probe(NodeA), Now);
        insights.CommitEvents(insights.BuildEventBatch(Now, "2.9.1", "", "")!, Now);

        insights.AddEvent(Probe(NodeB), Now);
        insights.BuildEventBatch(Now + TimeSpan.FromMinutes(59), "2.9.1", "", "").Should().BeNull();
        var due = Now + TimeSpan.FromMinutes(61);
        insights.BuildEventBatch(due, "2.9.1", "", "").Should().NotBeNull();

        insights.EventSendFailed(due);
        insights.BuildEventBatch(due + TimeSpan.FromMinutes(10), "2.9.1", "", "").Should().BeNull();
        insights.BuildEventBatch(due + TimeSpan.FromMinutes(31), "2.9.1", "", "").Should().NotBeNull();
    }

    [Fact]
    public void Probes_KeepOnlyTheLastOfEachHourPerNodeAndProtocol()
    {
        var insights = Create();
        insights.AddEvent(Probe(NodeA, 10), Now);
        insights.AddEvent(Probe(NodeA, 20), Now.AddMinutes(10));                      // same hour: replaces
        insights.AddEvent(Probe(NodeA, 30, "trojan"), Now.AddMinutes(11));            // another protocol: kept
        insights.AddEvent(Probe(NodeB, 40), Now.AddMinutes(12));                      // another node: kept
        insights.AddEvent(Probe(NodeA, 50), Now.AddHours(1));                         // next hour: kept
        var events = insights.BuildEventBatch(Now.AddHours(2), "2.9.1", "", "")!.Events;
        events.Select(item => (item.NodeId, item.Protocol, item.Ms)).Should().Equal(
            (NodeA, null, 20), (NodeA, "trojan", 30), (NodeB.ToLowerInvariant(), null, 40), (NodeA, null, 50));
    }

    [Fact]
    public void Queue_SurvivesARestart_AndTurningStatsOffClearsIt()
    {
        var first = Create();
        first.AddEvent(Probe(NodeA), Now);
        Create().QueuedEvents.Should().Be(1);

        first.SetStatsEnabled(false);
        first.QueuedEvents.Should().Be(0);
        first.AddEvent(Probe(NodeA), Now);                                            // nothing while off
        Create().QueuedEvents.Should().Be(0);
        first.BuildEventBatch(Now, "2.9.1", "", "").Should().BeNull();
    }

    // ── Reason mapping ─────────────────────────────────────────────────────
    [Theory]
    [InlineData("VPN core stopped unexpectedly", "core_crash")]
    [InlineData("network changed", "network_change")]
    [InlineData("No internet outside the tunnel either", "no_network")]
    [InlineData("The SSL connection could not be established", "tls")]
    [InlineData("tunnel carries no traffic: handshake failed", "handshake")]
    [InlineData("No connection could be made because the target machine actively refused it", "refused")]
    [InlineData("Connection reset by peer", "reset")]
    [InlineData("No such host is known", "dns")]
    [InlineData("access blocked by the network", "blocked")]
    [InlineData("401 Unauthorized", "auth")]
    [InlineData("monthly quota used", "quota")]
    [InlineData("tunnel carries no traffic: A task was canceled", "timeout")]
    [InlineData("The operation timed out", "timeout")]
    [InlineData("both paths carry no traffic", "timeout")]
    [InlineData("something odd", "other")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    public void ReasonOfText(string? text, string reason)
    {
        ColituAcSpec.ReasonOfText(text).Should().Be(reason);
        ColituAcSpec.Reasons.Should().Contain(reason);
    }

    [Fact]
    public void ReasonOf_Exceptions_UsesTypesAndCodesBeforeText()
    {
        ColituAcSpec.ReasonOf(new SocketException((int)SocketError.ConnectionRefused)).Should().Be("refused");
        ColituAcSpec.ReasonOf(new SocketException((int)SocketError.ConnectionReset)).Should().Be("reset");
        ColituAcSpec.ReasonOf(new SocketException((int)SocketError.HostNotFound)).Should().Be("dns");
        ColituAcSpec.ReasonOf(new SocketException((int)SocketError.NetworkUnreachable)).Should().Be("no_network");
        ColituAcSpec.ReasonOf(new SocketException((int)SocketError.TimedOut)).Should().Be("timeout");
        ColituAcSpec.ReasonOf(new AuthenticationException("The remote certificate is invalid")).Should().Be("tls");
        ColituAcSpec.ReasonOf(new TimeoutException()).Should().Be("timeout");
        ColituAcSpec.ReasonOf(new ColituConnectException("no internet", offline: true)).Should().Be("no_network");
        ColituAcSpec.ReasonOf(new ColituApiException(System.Net.HttpStatusCode.PaymentRequired, "quota", "QUOTA_EXCEEDED")).Should().Be("quota");
        ColituAcSpec.ReasonOf(new ColituApiException(System.Net.HttpStatusCode.Forbidden, "gone", "DEVICE_REVOKED")).Should().Be("auth");
        ColituAcSpec.ReasonOf(new ColituApiException(System.Net.HttpStatusCode.Forbidden, "region", "REGION_NOT_SUPPORTED")).Should().Be("blocked");
        // The cause inside a wrapper counts, as does the text when the types say nothing.
        ColituAcSpec.ReasonOf(new ColituConnectException("x", new SocketException((int)SocketError.ConnectionReset))).Should().Be("reset");
        ColituAcSpec.ReasonOf(new InvalidOperationException("Connection reset by peer")).Should().Be("reset");
        ColituAcSpec.ReasonOf(new InvalidOperationException("odd")).Should().Be("other");
        ColituAcSpec.ReasonOf(null).Should().Be("other");
    }

    // ── Counters hooks that feed events ────────────────────────────────────
    [Fact]
    public void CounterHooks_ReportWhatTheEventsNeed()
    {
        var insights = Create();
        insights.UnexpectedDisconnect().Should().BeTrue();
        insights.UnexpectedDisconnect().Should().BeFalse();             // one drop per outage
        insights.BeginRecovery().Should().BeTrue();
        insights.BeginRecovery().Should().BeFalse();
        insights.EndRecovery(true).Should().NotBeNull();
        insights.EndRecovery(true).Should().BeNull();
        insights.ProtocolSwitch("hysteria2", "trojan", true, Now).Should().BeFalse();
        insights.ProtocolSwitch("trojan", "hysteria2", true, Now.AddSeconds(30)).Should().BeTrue();   // flap
    }

    // ── The automatic choice ───────────────────────────────────────────────
    private static ColituVpnServer Node(string id, string country = "DE") => new() { Id = id, CountryCode = country, Available = true };

    [Fact]
    public void Choice_NamesTheChosenAndTheBestMeasuredCandidate()
    {
        const string network = "wifi|TR-AS9121";
        var ranked = new[] { Node("a"), Node("b"), Node("c") };
        var pings = new Dictionary<string, ColituPingSample>
        {
            ["a"] = new(120, Now.AddMinutes(-1), network),
            ["b"] = new(35, Now.AddMinutes(-1), network),
            ["c"] = new(null, Now.AddMinutes(-1), network),
        };
        var choice = ColituAdaptiveConnect.ChoiceOf(ranked, pings, network, Now)!;
        choice.Should().Be(new ColituAdaptiveConnect.Choice("a", 120, "b", 35));

        // The chosen one is the best: best == chosen.
        ColituAdaptiveConnect.ChoiceOf([ranked[1], ranked[0]], pings, network, Now)!.BestId.Should().Be("b");
        // No ping for the chosen server: no event.
        ColituAdaptiveConnect.ChoiceOf([ranked[2], ranked[0]], pings, network, Now).Should().BeNull();
        ColituAdaptiveConnect.ChoiceOf([], pings, network, Now).Should().BeNull();
    }
}

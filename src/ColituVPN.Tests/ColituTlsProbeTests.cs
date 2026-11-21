using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using AwesomeAssertions;
using System.Net;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>TLS probe, ip_blocked classifier, measurement schedule and events (TLS-PROBE-SPEC; shared fixtures tls-rst-fixtures.json).</summary>
public class ColituTlsProbeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 30, 0, TimeSpan.Zero);
    private const string Node = "0b7e7a52-3d0c-4d8e-9a77-5c1f2b6a1e01";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "colitu-tls-" + Guid.NewGuid().ToString("N"));

    public ColituTlsProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static JsonElement Fixtures() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "tls-rst-fixtures.json"))).RootElement;

    private static JsonElement Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    private ColituInsights Create(bool tlsEvents) => new(() => Path.Combine(_dir, "insights.json")) { TlsProbeEvents = tlsEvents };

    // ── Shared fixtures ────────────────────────────────────────────────────
    [Fact]
    public void Classify_EveryFixtureCase()
    {
        var cases = Fixtures().GetProperty("classify").EnumerateArray().ToList();
        cases.Should().NotBeEmpty();
        foreach (var item in cases)
        {
            var results = item.GetProperty("results").EnumerateArray()
                .Select(result => new ColituTlsResult(result.GetProperty("port").GetInt32(), result.GetProperty("tls").GetString()!, 100))
                .ToList();
            ColituIpBlock.IsBlocked(results).Should().Be(item.GetProperty("ip_blocked").GetBoolean(), item.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void Schedule_EveryFixtureCase()
    {
        var cases = Fixtures().GetProperty("schedule").EnumerateArray().ToList();
        cases.Should().NotBeEmpty();
        foreach (var item in cases)
        {
            var age = item.GetProperty("last_run_age_h");
            TimeSpan? since = age.ValueKind == JsonValueKind.Null ? null : TimeSpan.FromHours(age.GetDouble());
            ColituIpBlock.MeasureDue(item.GetProperty("vpn_on").GetBoolean(), item.GetProperty("targets").GetInt32(), since, item.GetProperty("interval_h").GetInt32())
                .Should().Be(item.GetProperty("due").GetBoolean(), item.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void Events_EveryFixtureCase_ThroughTheQueue()
    {
        var cases = Fixtures().GetProperty("events").EnumerateArray().ToList();
        cases.Should().NotBeEmpty();
        foreach (var item in cases)
        {
            var insights = new ColituInsights(() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json")) { TlsProbeEvents = true };
            var result = new ColituTlsResult(item.GetProperty("port").GetInt32(), item.GetProperty("tls").GetString()!, 123);
            insights.AddEvent(ColituTlsProbe.Event(Node, "vless-xhttp", result, item.GetProperty("measure").GetBoolean()), Now);
            var sent = Json(insights.BuildEventBatch(Now, "3.2.0", "", "")!.Events.Single());
            var name = item.GetProperty("name").GetString();
            foreach (var expected in item.GetProperty("expect").EnumerateObject())
            {
                var actual = sent.GetProperty(expected.Name);
                actual.GetRawText().Should().Be(expected.Value.GetRawText(), $"{name}: {expected.Name}");
            }
            sent.GetProperty("node_id").GetString().Should().Be(Node);
            sent.GetProperty("protocol").GetString().Should().Be("vless-xhttp");
            sent.GetProperty("ms").GetInt32().Should().Be(123);
        }
    }

    // ── Event shape and the panel flag ─────────────────────────────────────
    [Fact]
    public void TlsEvent_HasTheContractedFields()
    {
        var insights = Create(tlsEvents: true);
        insights.AddEvent(ColituTlsProbe.Event(Node, "vless-reality", new ColituTlsResult(443, ColituTlsClass.TlsRst, 210), measure: false), Now);
        var item = Json(insights.BuildEventBatch(Now, "3.2.0", "", "")!.Events[0]);
        item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("kind", "node_id", "protocol", "outcome", "reason", "ms", "probe", "port", "measure");
    }

    [Fact]
    public void NoFlag_NoTlsProbeEventsInTheReport()
    {
        var insights = Create(tlsEvents: false);
        insights.AddEvent(ColituTlsProbe.Event(Node, "vless-reality", new ColituTlsResult(443, ColituTlsClass.TlsRst, 210), measure: false), Now);
        insights.AddEvent(new ColituAcEvent { Kind = "probe", NodeId = Node, Protocol = "trojan", Outcome = "ok", Ms = 40 }, Now);
        var batch = insights.BuildEventBatch(Now, "3.2.0", "", "")!;
        batch.Events.Should().ContainSingle();
        var text = JsonSerializer.Serialize(batch.Body);
        text.Should().NotContain("\"probe\":").And.NotContain("\"port\"").And.NotContain("\"measure\"");
    }

    [Fact]
    public void FlagGone_QueuedTlsEventsNeverReachThePanel()
    {
        var insights = Create(tlsEvents: true);
        insights.AddEvent(ColituTlsProbe.Event(Node, "trojan", new ColituTlsResult(2053, ColituTlsClass.Ok, 90), measure: true), Now);
        insights.TlsProbeEvents = false;
        insights.BuildEventBatch(Now, "3.2.0", "", "").Should().BeNull();
        insights.QueuedEvents.Should().Be(0);
    }

    [Fact]
    public void TcpProbeEvent_StaysAsItWas_AndIsNotReplacedByATlsOne()
    {
        var insights = Create(tlsEvents: true);
        insights.AddEvent(new ColituAcEvent { Kind = "probe", NodeId = Node, Protocol = "trojan", Outcome = "ok", Ms = 40 }, Now);
        insights.AddEvent(ColituTlsProbe.Event(Node, "trojan", new ColituTlsResult(443, ColituTlsClass.Ok, 90), measure: false), Now);
        var events = insights.BuildEventBatch(Now, "3.2.0", "", "")!.Events.Select(Json).ToList();
        events.Should().HaveCount(2);
        events[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("kind", "node_id", "protocol", "outcome", "ms");
    }

    // ── .NET exceptions → class ────────────────────────────────────────────
    [Fact]
    public void ClassOf_MapsTheDotNetFailures()
    {
        ColituTlsProbe.ClassOf(new IOException("Unable to read data from the transport connection.", new SocketException((int)SocketError.ConnectionReset)))
            .Should().Be(ColituTlsClass.TlsRst);
        ColituTlsProbe.ClassOf(new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")).Should().Be(ColituTlsClass.TlsRst);
        ColituTlsProbe.ClassOf(new IOException("Authentication failed because the remote party has closed the transport stream.")).Should().Be(ColituTlsClass.TlsRst);
        ColituTlsProbe.ClassOf(new AuthenticationException("Authentication failed, see inner exception.", new IOException("x", new SocketException((int)SocketError.ConnectionAborted))))
            .Should().Be(ColituTlsClass.TlsRst);
        ColituTlsProbe.ClassOf(new AuthenticationException("Authentication failed because the remote party sent a TLS alert: 'HandshakeFailure'."))
            .Should().Be(ColituTlsClass.Ok);
        ColituTlsProbe.ClassOf(new AuthenticationException("The remote certificate is invalid.")).Should().Be(ColituTlsClass.Ok);
        ColituTlsProbe.ClassOf(new OperationCanceledException()).Should().Be(ColituTlsClass.Timeout);
        ColituTlsProbe.ClassOf(new IOException("x", new SocketException((int)SocketError.TimedOut))).Should().Be(ColituTlsClass.Timeout);
        // Unknown: never counted as a block.
        ColituTlsProbe.ClassOf(new InvalidOperationException("something")).Should().Be(ColituTlsClass.Timeout);
    }

    [Fact]
    public void Sni_EndpointNameFirst_AddressSendsNone()
    {
        ColituTlsProbe.Sni("www.cloudflare.com", "1.2.3.4").Should().Be("www.cloudflare.com");
        ColituTlsProbe.Sni("", "de6.example").Should().Be("de6.example");
        ColituTlsProbe.Sni(null, "1.2.3.4").Should().BeNull();
    }

    // ── Classifier inputs ──────────────────────────────────────────────────
    [Fact]
    public void PickEndpoints_TwoDistinctPortsOfTlsTransportsOnly()
    {
        var items = new[] { ("hysteria2", 443), ("shadowsocks", 8388), ("vless-reality", 443), ("trojan", 443), ("vless-xhttp", 4443), ("vless-xhttp", 2053) };
        ColituIpBlock.PickEndpoints(items, item => item.Item1, item => item.Item2)
            .Should().Equal(("vless-reality", 443), ("vless-xhttp", 4443));
    }

    [Fact]
    public void OneResetNextToOk_IsOnlyLogged()
    {
        var results = new[] { new ColituTlsResult(443, ColituTlsClass.TlsRst, 50), new ColituTlsResult(2053, ColituTlsClass.Ok, 80) };
        ColituIpBlock.IsBlocked(results).Should().BeFalse();
        ColituIpBlock.OneReset(results).Should().BeTrue();
    }

    // ── measure_targets ────────────────────────────────────────────────────
    [Fact]
    public void Parse_MeasureTargets()
    {
        var raw = JsonDocument.Parse($$"""
            {"interval_h": 6, "targets": [
              {"node_id":"{{Node}}","endpoint_id":"e1","protocol":"vless-xhttp","host":"de6.example","port":4443,"sni":"www.cloudflare.com"},
              {"node_id":"bad","protocol":"vless-xhttp","host":"x.example","port":443},
              {"node_id":"{{Node}}","protocol":"hysteria2","host":"x.example","port":443},
              {"node_id":"{{Node}}","protocol":"trojan","host":"x.example","port":22}
            ]}
            """).RootElement;
        var parsed = ColituIpBlock.Parse(raw)!;
        parsed.IntervalH.Should().Be(6);
        parsed.Targets.Should().ContainSingle();
        parsed.Targets[0].Should().Be(new ColituMeasureTarget(Node, "e1", "vless-xhttp", "de6.example", 4443, "www.cloudflare.com"));
        ColituIpBlock.Parse(null).Should().BeNull();
        ColituIpBlock.Parse(JsonDocument.Parse("""{"interval_h":6,"targets":[]}""").RootElement).Should().BeNull();
    }

    [Fact]
    public void EventsFeature_OnlyALiteralTrue()
    {
        static Dictionary<string, JsonElement> Features(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        ColituIpBlock.ParseEventsFeature(Features("""{"tls_probe_events":true}""")).Should().BeTrue();
        ColituIpBlock.ParseEventsFeature(Features("""{"tls_probe_events":"true"}""")).Should().BeFalse();
        ColituIpBlock.ParseEventsFeature(Features("""{"ac_engine_4":true}""")).Should().BeFalse();
        ColituIpBlock.ParseEventsFeature(null).Should().BeFalse();
    }

    // ── §5 latency map by endpoint ─────────────────────────────────────────
    [Fact]
    public void TwoEndpointsOfOneProtocol_KeepTwoLatencies()
    {
        var map = ColituTransportOrder.EndpointLatency([("vless-xhttp", 443, -1), ("vless-xhttp", 4443, 85), ("vless-reality", 443, -1)]);
        map.Should().HaveCount(3);
        map[ColituTransportOrder.EndpointKey("vless-xhttp", 443)].Should().Be(-1);
        map[ColituTransportOrder.EndpointKey("vless-xhttp", 4443)].Should().Be(85);
    }

    [Fact]
    public void Outage_OneSilentAndOneAnsweringEndpoint_IsNoOutage()
    {
        var map = ColituTransportOrder.EndpointLatency([("vless-xhttp", 443, -1), ("vless-xhttp", 4443, 85)]);
        ColituTransportOrder.ServerOutage(map).Should().BeFalse();
        var items = new List<(string Protocol, int Port)> { ("hysteria2", 443), ("vless-xhttp", 443), ("vless-xhttp", 4443) };
        string Key((string Protocol, int Port) item) => ColituTransportOrder.EndpointKey(item.Protocol, item.Port);
        ColituTransportOrder.WithoutSilent(items, Key, map).Should().Equal(("hysteria2", 443), ("vless-xhttp", 4443));
        ColituTransportOrder.NothingCanConnect(items.Skip(1).ToList(), Key, map).Should().BeFalse();

        var silent = ColituTransportOrder.EndpointLatency([("vless-xhttp", 443, -1), ("vless-xhttp", 4443, -1)]);
        ColituTransportOrder.ServerOutage(silent).Should().BeTrue();
        ColituTransportOrder.NothingCanConnect(items.Skip(1).ToList(), Key, silent).Should().BeTrue();
    }

    // ── Linux: nftables kill switch exception ─────────────────────────────
    [Fact]
    public void KillSwitch_EngageScriptHasUidScopedTlsProbeSets()
    {
        var allowed = new ColituKillSwitch.Allowance { Servers = [IPAddress.Parse("203.0.113.10")], Api = [IPAddress.Parse("198.51.100.20")], Resolvers = [IPAddress.Parse("1.1.1.1")] };
        var script = ColituKillSwitch.BuildEngageScript(allowed, 4242, appUid: 1000);
        script.Should().Contain("set tlsprobe4 { type ipv4_addr . inet_service; flags timeout; }");
        script.Should().Contain("set tlsprobe6 { type ipv6_addr . inet_service; flags timeout; }");
        var rule = script.IndexOf("meta skuid 1000 ip daddr . tcp dport @tlsprobe4 accept", StringComparison.Ordinal);
        rule.Should().BeGreaterThan(script.IndexOf("policy drop;", StringComparison.Ordinal));
        script.Should().Contain("meta skuid 1000 ip6 daddr . tcp dport @tlsprobe6 accept");
        ColituKillSwitch.BuildEngageScript(allowed, 4242, appUid: null).Should().NotContain("@tlsprobe4");
    }

    [Fact]
    public void KillSwitch_TlsProbeElementsArePublicAddressPortPairsForTenSeconds()
    {
        var script = ColituKillSwitch.BuildTlsProbeScript(
        [
            (IPAddress.Parse("203.0.113.10"), 443),
            (IPAddress.Parse("::ffff:203.0.113.10"), 4443),
            (IPAddress.Parse("203.0.113.10"), 443),
            (IPAddress.Parse("192.168.0.1"), 443),
            (IPAddress.Parse("203.0.113.11"), 0)
        ])!;
        script.Should().Contain("nft add element inet colitu_killswitch tlsprobe4 { 203.0.113.10 . 443 timeout 10s, 203.0.113.10 . 4443 timeout 10s }");
        script.Should().NotContain("192.168.0.1").And.NotContain("203.0.113.11");
        ColituKillSwitch.BuildTlsProbeScript([(IPAddress.Loopback, 443)]).Should().BeNull();
    }

    // ── AC3 hinted start: the classifier next to the first attempt ─────────
    /// <summary>
    /// The service's use of the gate on the hinted path, in miniature: transports in order; after the first attempt the
    /// gate is asked once; ip_blocked leaves the server before any stall mark; a failed transport is marked otherwise.
    /// </summary>
    private static async Task<(int Tried, List<string> Marked, bool Blocked, bool Connected)> HintedConnectAsync(
        IReadOnlyList<string> transports, Func<string, Task<bool>> attempt, ColituTlsGate gate)
    {
        var marked = new List<string>();
        var tried = 0;
        ColituTlsGate? pending = gate;
        foreach (var transport in transports)
        {
            tried++;
            var ok = await attempt(transport);
            if (pending != null)
            {
                var tls = await pending.AfterAttemptAsync(ok);
                pending = null;
                if (tls != null && ColituIpBlock.IsBlocked(tls.Select(item => item.Result)))
                {
                    return (tried, marked, true, false);
                }
            }
            if (ok)
            {
                return (tried, marked, false, true);
            }
            marked.Add(transport);
        }
        return (tried, marked, false, false);
    }

    private static List<(string Protocol, ColituTlsResult Result)> FixtureResults(string name) =>
        Fixtures().GetProperty("classify").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name)
            .GetProperty("results").EnumerateArray()
            .Select(result => ("vless-reality", new ColituTlsResult(result.GetProperty("port").GetInt32(), result.GetProperty("tls").GetString()!, 300)))
            .ToList();

    [Fact]
    public async Task HintedStart_Blocked_ServerSkippedAfterTheFirstFailure_NoStallMarks()
    {
        var gate = new ColituTlsGate(async _ => { await Task.Delay(50); return FixtureResults("two ports reset"); });
        var result = await HintedConnectAsync(["hysteria2", "vless-reality", "trojan", "shadowsocks"], _ => Task.FromResult(false), gate);
        result.Blocked.Should().BeTrue();
        result.Tried.Should().Be(1);
        result.Marked.Should().BeEmpty();
    }

    [Fact]
    public async Task HintedStart_NotBlocked_GoesOnAndMarksAsBefore()
    {
        var gate = new ColituTlsGate(_ => Task.FromResult(FixtureResults("one reset one ok")));
        var result = await HintedConnectAsync(["hysteria2", "vless-reality"], transport => Task.FromResult(transport == "vless-reality"), gate);
        result.Blocked.Should().BeFalse();
        result.Connected.Should().BeTrue();
        result.Marked.Should().Equal("hysteria2");
    }

    [Fact]
    public async Task HintedStart_FirstAttemptOk_NeverWaitsForASlowProbe()
    {
        var probeCancelled = false;
        var gate = new ColituTlsGate(async cancel =>
        {
            try
            {
                await Task.Delay(3000, cancel);
            }
            catch (OperationCanceledException)
            {
                probeCancelled = true;
            }
            return FixtureResults("two ports reset");
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await HintedConnectAsync(["vless-reality", "trojan"], _ => Task.FromResult(true), gate);
        watch.Stop();
        result.Connected.Should().BeTrue();
        result.Blocked.Should().BeFalse();
        watch.ElapsedMilliseconds.Should().BeLessThan(500);
        gate.Cancelled.Should().BeTrue();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        probeCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task HintedStart_FailedAttempt_WaitsAtMostTheBudget()
    {
        var gate = new ColituTlsGate(async cancel => { await Task.Delay(5000, cancel); return FixtureResults("two ports reset"); });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tls = await gate.AfterAttemptAsync(false, TimeSpan.FromMilliseconds(200));
        watch.ElapsedMilliseconds.Should().BeLessThan(2000);
        tls.Should().NotBeNull().And.BeEmpty();
    }
}

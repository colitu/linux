using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>Wake-up check waits for the network, a late spare win ends the connect on the proven server, the connect event names its engine.</summary>
public class ColituResumeSpareEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
    private const string Node = "0b7e7a52-3d0c-4d8e-9a77-5c1f2b6a1e01";

    private static string Wire(ColituAcEvent source)
    {
        var path = Path.Combine(Path.GetTempPath(), "colitu-engine-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var insights = new ColituInsights(() => path);
            insights.AddEvent(source, Now);
            return JsonSerializer.Serialize(insights.BuildEventBatch(Now, "3.1.2", "", "")!.Events[0]);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── Resume check ───────────────────────────────────────────────────────
    [Fact]
    public void Resume_NetworkAlreadyUp_ChecksAfterTheMinimumWait()
    {
        ColituResumeWait.ShouldCheck(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)).Should().BeFalse();
        ColituResumeWait.ShouldCheck(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)).Should().BeFalse();
        ColituResumeWait.ShouldCheck(ColituResumeWait.MinWait, ColituResumeWait.MinWait).Should().BeTrue();
    }

    [Fact]
    public void Resume_SlowWifi_WaitsForTheNetworkAndLetsItSettle()
    {
        // Down at 4 s and at 12 s: no check, however long it takes (up to the limit).
        ColituResumeWait.ShouldCheck(TimeSpan.FromSeconds(4), null).Should().BeFalse();
        ColituResumeWait.ShouldCheck(TimeSpan.FromSeconds(12), null).Should().BeFalse();
        // Just joined at 13 s: a moment to settle first.
        ColituResumeWait.ShouldCheck(TimeSpan.FromSeconds(13), TimeSpan.Zero).Should().BeFalse();
        ColituResumeWait.ShouldCheck(TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(1)).Should().BeFalse();
        ColituResumeWait.ShouldCheck(TimeSpan.FromSeconds(15), ColituResumeWait.Settle).Should().BeTrue();
    }

    [Fact]
    public void Resume_NeverWaitsPastTheLimit()
    {
        ColituResumeWait.ShouldCheck(ColituResumeWait.MaxWait - TimeSpan.FromMilliseconds(1), null).Should().BeFalse();
        ColituResumeWait.ShouldCheck(ColituResumeWait.MaxWait, null).Should().BeTrue();
    }

    [Fact]
    public void ResumeReason_IsSentAsItself()
    {
        ColituAcSpec.Reasons.Should().Contain(ColituAcSpec.ResumeReason);
        var drop = Wire(new ColituAcEvent { Kind = "drop", NodeId = Node, Reason = ColituAcSpec.ResumeReason, Minutes = 5 });
        JsonDocument.Parse(drop).RootElement.GetProperty("reason").GetString().Should().Be("resume");
        var recovery = Wire(new ColituAcEvent { Kind = "recovery", NodeId = Node, Outcome = "ok", Reason = "resume", Ms = 900 });
        JsonDocument.Parse(recovery).RootElement.GetProperty("reason").GetString().Should().Be("resume");
    }

    // ── Spare win past the budget ──────────────────────────────────────────
    [Fact]
    public void SpareWin_WithinTheBudget_IsAlwaysFollowed()
    {
        ColituVpnService.ShouldFollowSpare(TimeSpan.FromSeconds(10), lateSwapUsed: false).Should().BeTrue();
        ColituVpnService.ShouldFollowSpare(TimeSpan.FromSeconds(44), lateSwapUsed: true).Should().BeTrue();
    }

    [Fact]
    public void SpareWin_PastTheBudget_IsFollowedOnce()
    {
        var late = ColituVpnService.ConnectBudget + TimeSpan.FromSeconds(3);
        ColituVpnService.ShouldFollowSpare(late, lateSwapUsed: false).Should().BeTrue();
        ColituVpnService.ShouldFollowSpare(late, lateSwapUsed: true).Should().BeFalse();
        ColituVpnService.ShouldFollowSpare(ColituVpnService.ConnectBudget, lateSwapUsed: false).Should().BeTrue();
    }

    // ── Engine tag ─────────────────────────────────────────────────────────
    [Theory]
    [InlineData(5, false, true, "ac4")]
    [InlineData(5, false, false, "ac3")]
    [InlineData(0, true, false, "panel")]
    [InlineData(0, false, true, "panel")]     // no ranked list at all: the panel chose, whatever engine was last
    [InlineData(5, true, true, "panel")]      // a later round: every ranked server failed, the panel chose
    public void EngineOfRound(int ranked, bool panelChose, bool ac4, string expected) =>
        ColituAcSpec.EngineOfRound(ranked, panelChose, ac4).Should().Be(expected);

    [Fact]
    public void Connect_AutomaticEvent_CarriesTheEngine()
    {
        foreach (var engine in new[] { "ac4", "ac3", "panel" })
        {
            var json = JsonDocument.Parse(Wire(new ColituAcEvent { Kind = "connect", NodeId = Node, Outcome = "ok", Ms = 1500, FirstTry = true, Mode = "auto", Engine = engine })).RootElement;
            json.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("kind", "node_id", "outcome", "ms", "first_try", "mode", "engine");
            json.GetProperty("engine").GetString().Should().Be(engine);
        }
    }

    [Fact]
    public void Connect_ManualEvent_HasNoEngine_AndUnknownValuesAreDropped()
    {
        var manual = JsonDocument.Parse(Wire(new ColituAcEvent { Kind = "connect", NodeId = Node, Outcome = "ok", Mode = "manual", Engine = "ac4" })).RootElement;
        manual.TryGetProperty("engine", out _).Should().BeFalse();
        var unknown = JsonDocument.Parse(Wire(new ColituAcEvent { Kind = "connect", NodeId = Node, Outcome = "ok", Mode = "auto", Engine = "v9" })).RootElement;
        unknown.TryGetProperty("engine", out _).Should().BeFalse();
        var none = JsonDocument.Parse(Wire(new ColituAcEvent { Kind = "connect", NodeId = Node, Outcome = "ok", Mode = "auto" })).RootElement;
        none.TryGetProperty("engine", out _).Should().BeFalse();
    }

    [Fact]
    public void Engine_IsOnlyOnTheConnectEvent()
    {
        var recovery = JsonDocument.Parse(Wire(new ColituAcEvent { Kind = "recovery", NodeId = Node, Outcome = "ok", Reason = "timeout", Engine = "ac4" })).RootElement;
        recovery.TryGetProperty("engine", out _).Should().BeFalse();
    }
}

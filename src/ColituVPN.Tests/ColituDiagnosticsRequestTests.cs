using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>Connection-report requests: response parsing, upload body, report text, timing and decisions.</summary>
public class ColituDiagnosticsRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private const string Pending = """
        {"data":{"request":{"id":"r1","status":"pending","created_at":"2026-10-15T11:55:00Z","expires_at":"2026-10-16T11:55:00Z"}}}
        """;

    [Fact]
    public void Parse_ReadsAPendingRequest()
    {
        var request = ColituDiagnosticsAsker.Parse(Pending);
        request.Should().NotBeNull();
        request!.Id.Should().Be("r1");
        request.Status.Should().Be("pending");
        request.ExpiresAt.Should().Be(new DateTimeOffset(2026, 10, 16, 11, 55, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_ToleratesAConversationIdThePanelNoLongerSends()
    {
        var json = """{"data":{"request":{"id":"r2","status":"pending","conversation_id":null}}}""";
        ColituDiagnosticsAsker.Parse(json)!.Id.Should().Be("r2");
    }

    [Theory]
    [InlineData("""{"data":{"request":null}}""")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"request":{"status":"pending"}}}""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_GivesNullWhenThereIsNothingToAnswer(string? json)
    {
        ColituDiagnosticsAsker.Parse(json).Should().BeNull();
    }

    [Fact]
    public void UploadBody_HasTheReportAndTheDeviceFields()
    {
        using var body = JsonDocument.Parse(ColituDiagnosticsAsker.BuildUploadBody("text", "windows", "2.9.0", "Win 11"));
        body.RootElement.GetProperty("report").GetString().Should().Be("text");
        body.RootElement.GetProperty("platform").GetString().Should().Be("windows");
        body.RootElement.GetProperty("app_version").GetString().Should().Be("2.9.0");
        body.RootElement.GetProperty("os_version").GetString().Should().Be("Win 11");
        body.RootElement.TryGetProperty("request_id", out _).Should().BeFalse();
    }

    [Fact]
    public void ReportText_HasSectionHeadersAndTheFields()
    {
        var diagnostics = new ColituSupportDiagnostics
        {
            Device = "Windows PC",
            Os = "Win 11 (x64)",
            AppVersion = "2.9.0",
            Platform = "windows",
            Network = "tun",
            Server = "Germany Frankfurt (de-1)",
            Protocol = "hysteria2",
            Connected = true,
            LastErrors = ["timeout"]
        };

        var text = ColituDiagnosticsAsker.BuildReportText(diagnostics, "line one\nline two\n", Now);

        text.Should().Contain("===== Device =====").And.Contain("===== App =====").And.Contain("===== Connection =====")
            .And.Contain("===== Last errors =====").And.Contain("===== App log (latest lines) =====");
        text.Should().Contain("Version: 2.9.0").And.Contain("Connected: yes").And.Contain("Protocol: hysteria2")
            .And.Contain("timeout").And.Contain("line two");
    }

    [Fact]
    public void ReportText_SaysSoWhenThereAreNoErrorsOrLog()
    {
        var text = ColituDiagnosticsAsker.BuildReportText(new ColituSupportDiagnostics(), null, Now);
        text.Should().Contain("(none)").And.Contain("(no log)");
    }

    [Fact]
    public void Check_RunsAtMostOncePerMinute()
    {
        var asker = new ColituDiagnosticsAsker();
        asker.TryBeginCheck(Now).Should().BeTrue();
        asker.TryBeginCheck(Now.AddSeconds(59)).Should().BeFalse();
        asker.TryBeginCheck(Now.AddSeconds(60)).Should().BeTrue();
    }

    [Fact]
    public void AnsweredRequest_IsNeverAnsweredAgain()
    {
        var asker = new ColituDiagnosticsAsker();
        var request = ColituDiagnosticsAsker.Parse(Pending);
        asker.ShouldAnswer(request, Now).Should().BeTrue();

        asker.Record("r1", ColituDiagnosticsOutcome.Answered, Now);

        asker.ShouldAnswer(request, Now.AddHours(1)).Should().BeFalse();
    }

    [Fact]
    public void FailedUpload_IsRetriedAfterTenMinutes()
    {
        var asker = new ColituDiagnosticsAsker();
        var request = ColituDiagnosticsAsker.Parse(Pending);
        asker.Record("r1", ColituDiagnosticsOutcome.Failed, Now);

        asker.ShouldAnswer(request, Now.AddMinutes(9)).Should().BeFalse();
        asker.ShouldAnswer(request, Now.AddMinutes(10)).Should().BeTrue();
    }

    [Fact]
    public void ExpiredOrNotPendingRequests_AreIgnored()
    {
        var asker = new ColituDiagnosticsAsker();
        asker.ShouldAnswer(new ColituDiagnosticsRequest { Id = "r", Status = "pending", ExpiresAt = Now.AddSeconds(-1) }, Now).Should().BeFalse();
        asker.ShouldAnswer(new ColituDiagnosticsRequest { Id = "r", Status = "answered" }, Now).Should().BeFalse();
        asker.ShouldAnswer(null, Now).Should().BeFalse();
    }

    [Fact]
    public void Reset_ForgetsAnsweredRequestsAndTheCheckClock()
    {
        var asker = new ColituDiagnosticsAsker();
        var request = ColituDiagnosticsAsker.Parse(Pending);
        asker.TryBeginCheck(Now);
        asker.Record("r1", ColituDiagnosticsOutcome.Answered, Now);

        asker.Reset();

        asker.ShouldAnswer(request, Now).Should().BeTrue();
        asker.TryBeginCheck(Now).Should().BeTrue();
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound, true)]
    [InlineData(System.Net.HttpStatusCode.MethodNotAllowed, true)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, false)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, false)]
    public void AnOlderPanelWithoutTheEndpoint_MeansNoRequest(System.Net.HttpStatusCode status, bool missing)
    {
        ColituDiagnosticsAsker.IsMissingEndpoint(new ColituApiException(status, "x")).Should().Be(missing);
    }

    [Fact]
    public void OtherFailures_AreNotAMissingEndpoint()
    {
        ColituDiagnosticsAsker.IsMissingEndpoint(new System.Net.Http.HttpRequestException("offline")).Should().BeFalse();
    }
}

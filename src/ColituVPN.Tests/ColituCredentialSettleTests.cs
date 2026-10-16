using System;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>A server whose new credentials the node has not confirmed yet is retried, not given up on.</summary>
public class ColituCredentialSettleTests
{
    [Fact]
    public void PendingFlagIsRead()
    {
        JsonSerializer.Deserialize<ColituConfigEnvelopeDto>("""{"revision":1,"credentials_pending":true}""")!.CredentialsPending.Should().BeTrue();
        JsonSerializer.Deserialize<ColituConfigEnvelopeDto>("""{"revision":1}""")!.CredentialsPending.Should().BeFalse();
    }

    [Fact]
    public void PickedServerWaitsLongerThanAutomatic()
    {
        ColituCredentialSettle.Window(automatic: false).Should().BeGreaterThan(ColituCredentialSettle.Window(automatic: true));
        ColituCredentialSettle.Window(automatic: true).Should().BeLessThan(ColituVpnService.PerServerBudget);
    }

    [Fact]
    public void RetriesStopAtTheWindow()
    {
        var window = TimeSpan.FromSeconds(20);
        ColituCredentialSettle.ShouldRetry(TimeSpan.Zero, window).Should().BeTrue();
        ColituCredentialSettle.ShouldRetry(TimeSpan.FromSeconds(17), window).Should().BeTrue();
        ColituCredentialSettle.ShouldRetry(TimeSpan.FromSeconds(18), window).Should().BeFalse();
        ColituCredentialSettle.ShouldRetry(TimeSpan.FromSeconds(30), window).Should().BeFalse();
    }
}

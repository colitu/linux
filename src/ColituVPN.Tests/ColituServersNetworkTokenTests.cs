using System;
using System.Linq;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>GET /servers carries the stored network token of the current network only (panel 736c32b).</summary>
public class ColituServersNetworkTokenTests
{
    private const string Place = "Wi-Fi|192.168.1.20|192.168.1.1";
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly ColituVpnSession Session = new()
    {
        ClientNetwork = "AS12389",
        NetworkToken = "tok-ru",
        NetworkTokenNetwork = "AS12389",
        NetworkTokenAt = Now.AddHours(-1),
        NetworkTokenPlace = Place
    };

    [Fact]
    public void TokenIsSentOnTheNetworkItWasIssuedOnAndOmittedAfterANetworkChange()
    {
        var token = ColituVpnService.ServersNetworkToken(Session, Place, Now);
        token.Should().Be("tok-ru");
        using (var request = ColituApiClient.ServersRequest(new Uri("https://api.example/v1/servers"), token))
        {
            request.Headers.GetValues(ColituApiClient.NetworkTokenHeader).Single().Should().Be("tok-ru");
        }

        // Another Wi-Fi / hotspot: no token at all, never the old one.
        var changed = ColituVpnService.ServersNetworkToken(Session, "Wi-Fi|172.20.10.2|172.20.10.1", Now);
        changed.Should().BeNull();
        using (var request = ColituApiClient.ServersRequest(new Uri("https://api.example/v1/servers"), changed))
        {
            request.Headers.Contains(ColituApiClient.NetworkTokenHeader).Should().BeFalse();
        }
        // Client network forgotten after a change, a token from before this rule, an expired token: none either.
        ColituVpnService.ServersNetworkToken(Session with { ClientNetwork = null }, Place, Now).Should().BeNull();
        ColituVpnService.ServersNetworkToken(Session with { NetworkTokenPlace = null }, Place, Now).Should().BeNull();
        ColituVpnService.ServersNetworkToken(Session, null, Now).Should().BeNull();
        ColituVpnService.ServersNetworkToken(Session, Place, Now.AddHours(48)).Should().BeNull();
    }
}

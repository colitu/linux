using System.IO;
using AwesomeAssertions;
using ServiceLib;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

public class ColituRuDirectTests
{
    [Fact]
    public void RussianDomainsAndIps_GoDirectWhateverTheSettings()
    {
        foreach (var preferences in new[]
                 {
                     new ColituVpnPreferences(),
                     new ColituVpnPreferences(DnsLeakProtectionEnabled: false, AdBlockEnabled: true, ConnectionMode: ColituConnectionModes.Tun),
                 })
        {
            var rules = ColituVpnService.BuildColituRoutingRules(preferences);

            var domain = rules.Single(r => r.Id == "colitu-ru-direct-domain");
            domain.Enabled.Should().BeTrue();
            domain.OutboundTag.Should().Be(Global.DirectTag);
            domain.Domain.Should().Equal(ColituVpnService.RussianDirectDomains);
            domain.Domain.Should().Contain(["geosite:category-ru", "domain:ru", "domain:su", "domain:xn--p1ai", "domain:vk.com", "domain:yandex.net", "domain:tbank.com", "domain:moscow", "domain:tatar"]);

            var ip = rules.Single(r => r.Id == "colitu-ru-direct-ip");
            ip.Enabled.Should().BeTrue();
            ip.OutboundTag.Should().Be(Global.DirectTag);
            ip.Ip.Should().Equal("geoip:ru");

            // DNS protection and ad blocking stay in front of them.
            rules.FindIndex(r => r.Id == "colitu-dns-protection").Should().BeLessThan(rules.IndexOf(domain));
            rules.FindIndex(r => r.Id == "colitu-ad-block").Should().BeLessThan(rules.IndexOf(domain));
        }
    }

    [Fact]
    public void RussianSitesDirect_IsAlwaysTrue() => ColituVpnService.RussianSitesDirect().Should().BeTrue();

    [Theory]
    [InlineData("DE")]
    [InlineData("RU")]
    [InlineData(null)]
    public void PrivacyMode_KeepsTheRussianDirectRule_AndSendsEverythingElseThroughTheTunnel(string? country)
    {
        var rules = ColituVpnService.BuildColituRoutingRules(new ColituVpnPreferences(PrivacyModeEnabled: true), country);

        rules.Single(r => r.Id == "colitu-ru-direct-domain").Enabled.Should().BeTrue();
        rules.Single(r => r.Id == "colitu-ru-direct-ip").Enabled.Should().BeTrue();
        // Nothing else may leave outside the tunnel: only the local network and the Russian destinations.
        rules.Where(r => r.Enabled && r.OutboundTag == Global.DirectTag).Select(r => r.Id)
            .Should().BeEquivalentTo(ColituVpnService.LanDirectRuleId, "colitu-ru-direct-domain", "colitu-ru-direct-ip");
    }

    [Fact]
    public void PrivacyMode_IsOnForNewInstalls_OffForSavedStatesWithoutTheProperty()
    {
        ColituVpnPreferences.ForNewInstall().PrivacyModeEnabled.Should().BeTrue();
        new ColituVpnPreferences().PrivacyModeEnabled.Should().BeFalse();

        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var old = System.Text.Json.JsonSerializer.Deserialize<ColituVpnPreferences>("{\"KillSwitchEnabled\":true,\"AdBlockEnabled\":true}", options)!;
        old.PrivacyModeEnabled.Should().BeFalse();

        var rules = ColituVpnService.BuildColituRoutingRules(ColituVpnPreferences.ForNewInstall(), "DE");
        rules.Single(r => r.Id == "colitu-ru-direct-domain").Enabled.Should().BeTrue();
        rules.Single(r => r.Id == "colitu-ru-direct-ip").Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("RU")]
    [InlineData("ru")]
    public void RussianServer_AlsoSendsRussianSitesDirect(string country)
    {
        var rules = ColituVpnService.BuildColituRoutingRules(new ColituVpnPreferences(), country);

        rules.Single(r => r.Id == "colitu-ru-direct-domain").Enabled.Should().BeTrue();
        rules.Single(r => r.Id == "colitu-ru-direct-ip").Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("DE")]
    [InlineData(null)]
    public void OtherServers_SendRussianSitesDirect(string? country)
    {
        var rules = ColituVpnService.BuildColituRoutingRules(new ColituVpnPreferences(), country);

        rules.Single(r => r.Id == "colitu-ru-direct-domain").Enabled.Should().BeTrue();
        rules.Single(r => r.Id == "colitu-ru-direct-ip").Enabled.Should().BeTrue();
    }

    [Fact]
    public void RuleSetFilesShipWithTheApp()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "srss-dosyalari");
        File.Exists(Path.Combine(dir, "geosite-category-ru.srs")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "geoip-ru.srs")).Should().BeTrue();
    }

    [Fact]
    public void LocalNetwork_GoesDirectAfterDns()
    {
        var rules = ColituVpnService.BuildColituRoutingRules(new ColituVpnPreferences(PrivacyModeEnabled: true), "DE");
        var lan = rules.Single(r => r.Id == ColituVpnService.LanDirectRuleId);
        lan.Enabled.Should().BeTrue();
        lan.OutboundTag.Should().Be(Global.DirectTag);
        lan.Ip.Should().Equal("geoip:private");
        rules.FindIndex(r => r.Id == "colitu-dns-protection").Should().BeLessThan(rules.IndexOf(lan));
    }
}

using System.Net;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

public class ColituKillSwitchTests
{
    private static string Script() => ColituKillSwitch.BuildEngageScript(
        [IPAddress.Parse("203.0.113.7"), IPAddress.Parse("203.0.113.7"), IPAddress.Parse("2001:db8::5"), IPAddress.Parse("fe80::1")],
        4242);

    [Fact]
    public void Rules_DropEverythingElseByDefault()
    {
        var script = Script();
        script.Should().Contain("type filter hook output priority 0; policy drop;");
        script.Should().Contain("oifname \"lo\" accept");
        script.Should().Contain("oifname { \"xray_tun\", \"singbox_tun\" } accept");
    }

    [Fact]
    public void Rules_LetTheServersAndTheLocalNetworkThrough()
    {
        var script = Script();
        script.Should().Contain("ip daddr { 203.0.113.7 } accept", "duplicates are removed");
        script.Should().Contain("ip6 daddr { 2001:db8::5 } accept", "link-local addresses are covered by fe80::/10 already");
        script.Should().Contain("192.168.0.0/16").And.Contain("10.0.0.0/8").And.Contain("udp sport 68 udp dport 67 accept");
    }

    [Fact]
    public void Rules_OutliveTheApp_FailClosed()
    {
        var script = Script();
        // No watcher that deletes the table when the app dies: a crash must not open the internet.
        script.Should().NotContain("kill -0").And.NotContain("setsid");
        script.Should().NotContain("nft delete table");
        // An old (≤ 1.1.2) watcher is stopped so it cannot remove the new rules either.
        script.Should().Contain("/run/colitu-killswitch.watch");
    }

    [Fact]
    public void Rules_ReplaceTheTableInOneTransaction()
    {
        var script = Script();
        script.Should().Contain("table inet colitu_killswitch\ndelete table inet colitu_killswitch\ntable inet colitu_killswitch {");
    }

    [Fact]
    public void Rules_LeaveAMarkerForTheNextStart()
    {
        var script = Script();
        script.Should().Contain("printf '%s %s\\n' \"${SUDO_UID:-0}\" 4242 > /run/colitu-killswitch.active");
        script.Should().Contain("chmod 0644 /run/colitu-killswitch.active");
    }

    [Fact]
    public void Release_RemovesTableMarkerAndOldWatcher()
    {
        var script = ColituKillSwitch.ReleaseScript;
        script.Should().Contain("nft delete table inet colitu_killswitch 2>/dev/null || true");
        script.Should().Contain("rm -f /run/colitu-killswitch.active");
        script.Should().Contain("/run/colitu-killswitch.watch");
        script.Should().NotContain("\r");
    }

    [Theory]
    [InlineData("1000 4242\n", 4242)]
    [InlineData("0 17", 17)]
    [InlineData("1000", null)]
    [InlineData("1000 abc", null)]
    [InlineData("1000 -5", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Marker_NamesTheAppThatInstalledTheRules(string? text, int? expected)
    {
        ColituKillSwitch.ParseMarkerPid(text).Should().Be(expected);
    }

    [Fact]
    public void OrphanCores_AreMatchedOnTheirExecutablePath()
    {
        var script = ColituKillSwitch.BuildStopOrphanCoresScript("/opt/colitu-vpn/bin", "/home/u/.local/share/ColituVPN/bin/", "/opt/colitu-vpn/bin/");
        script.Should().Contain("readlink \"$p/exe\"");
        script.Should().Contain("'/opt/colitu-vpn/bin/'*) kill -9 \"${p#/proc/}\" 2>/dev/null || true ;;");
        script.Should().Contain("'/home/u/.local/share/ColituVPN/bin/'*) kill -9");
        script.Split("/opt/colitu-vpn/bin/").Length.Should().Be(2, "duplicates are dropped");
        script.Should().NotContain("\r");
    }

    [Fact]
    public void OrphanCores_PathIsQuotedForTheShell()
    {
        ColituKillSwitch.BuildStopOrphanCoresScript("/home/o'neil/$(evil)/bin/")
            .Should().Contain("'/home/o'\\''neil/$(evil)/bin/'*)");
    }

    [Fact]
    public void Rules_UseUnixLineEndings()
    {
        Script().Should().NotContain("\r");
    }

    [Fact]
    public void Rules_HaveNoAddressesWhenNoneAreKnown()
    {
        var script = ColituKillSwitch.BuildEngageScript([], 1);
        script.Should().NotContain("ip daddr { } accept");
    }

    private static ColituKillSwitch.Allowance Split() => new()
    {
        Servers = [IPAddress.Parse("203.0.113.7"), IPAddress.Parse("1.1.1.1")],
        Api = [IPAddress.Parse("198.51.100.20"), IPAddress.Parse("2001:db8::20")],
        ApiPorts = [443, 443, 0, 70000],
        Resolvers = [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700:4700::1111")]
    };

    [Fact]
    public void Split_ApiOnlyForTheAppOverHttps()
    {
        var script = ColituKillSwitch.BuildEngageScript(Split(), 1, appUid: 1000);
        script.Should().Contain("    meta skuid 1000 ip daddr { 198.51.100.20 } tcp dport 443 accept");
        script.Should().Contain("    meta skuid 1000 ip6 daddr { 2001:db8::20 } tcp dport 443 accept");
        script.Should().NotContain("meta skuid 0 ip daddr { 198.51.100.20 }", "the cores never call the panel");
        script.Should().NotContain("70000").And.NotContain("dport 0");
    }

    [Fact]
    public void Split_ApiPortsComeFromTheUrls()
    {
        var script = ColituKillSwitch.BuildEngageScript(new ColituKillSwitch.Allowance
        {
            Api = [IPAddress.Parse("198.51.100.20")],
            ApiPorts = [8443, 443]
        }, 1, appUid: 1000);
        script.Should().Contain("meta skuid 1000 ip daddr { 198.51.100.20 } tcp dport { 443, 8443 } accept");
    }

    [Fact]
    public void Split_ResolversForTheCoresOnDnsPorts_AndForTheAppOnlyDoh()
    {
        var script = ColituKillSwitch.BuildEngageScript(Split(), 1, appUid: 1000);
        script.Should().Contain("    meta skuid 0 ip daddr { 1.1.1.1, 8.8.8.8 } udp dport 53 accept");
        script.Should().Contain("    meta skuid 0 ip daddr { 1.1.1.1, 8.8.8.8 } tcp dport { 53, 443, 853 } accept");
        script.Should().Contain("    meta skuid 0 ip6 daddr { 2606:4700:4700::1111 } tcp dport { 53, 443, 853 } accept");
        script.Should().Contain("    meta skuid 1000 ip daddr { 1.1.1.1, 8.8.8.8 } tcp dport 443 accept");
        script.Should().Contain("    meta skuid 1000 ip6 daddr { 2606:4700:4700::1111 } tcp dport 443 accept");
        // No rule opens a resolver to the user's programs without a port and protocol.
        script.Should().NotContain("8.8.8.8 } accept").And.NotContain("1.1.1.1 } accept");
        script.Should().NotContain("208.67.222.222");
    }

    [Fact]
    public void Split_ServerListCannotWidenTheResolverRules()
    {
        var script = ColituKillSwitch.BuildEngageScript(Split(), 1, appUid: 1000);
        script.Should().Contain("    meta skuid 0 ip daddr { 203.0.113.7 } accept");
        script.Should().Contain("    meta skuid 1000 meta l4proto tcp ip daddr { 203.0.113.7 } accept");
        script.Should().NotContain("203.0.113.7, 1.1.1.1");
    }

    [Fact]
    public void Split_WithoutAUid_KeepsThePortLimits()
    {
        var script = ColituKillSwitch.BuildEngageScript(Split(), 1);
        script.Should().Contain("    ip daddr { 203.0.113.7 } accept");
        script.Should().Contain("    ip daddr { 198.51.100.20 } tcp dport 443 accept");
        script.Should().Contain("    ip daddr { 1.1.1.1, 8.8.8.8 } tcp dport 443 accept");
        script.Should().Contain("    meta skuid 0 ip daddr { 1.1.1.1, 8.8.8.8 } udp dport 53 accept");
    }

    [Fact]
    public void Split_KeepsDnsDropLanDhcpAndBypassUnchanged()
    {
        var script = ColituKillSwitch.BuildEngageScript(Split(), 1, ["192.0.2.0/24"], appUid: 1000);
        script.Should().Contain("meta skuid != 0 udp dport 53 drop").And.Contain("meta skuid != 0 tcp dport 53 drop");
        script.Should().Contain("192.168.0.0/16").And.Contain("udp sport 68 udp dport 67 accept");
        script.Should().Contain("    ip daddr { 192.0.2.0/24 } accept");
        script.Should().NotContain("\r");
    }

    [Fact]
    public void LeftoverRules_AreNeverReportedOutsideLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            ColituKillSwitch.HasLeftoverRules().Should().BeFalse();
        }
    }
}

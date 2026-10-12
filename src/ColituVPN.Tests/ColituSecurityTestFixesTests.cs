using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using ServiceLib.Common;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// Fixes from the 2026-10-12 security test (v2): the kill switch rules (LNX-04/05 and the control
/// plane limited to the app's user), the root config policy (LNX-03: loopback-only listeners, the
/// control API with a secret), the launcher's hash and owner checks (LNX-02/03), the trusted app
/// path for the ping rule (LNX-01) and the updater's hand-followed redirects (LNX-06).
/// </summary>
public class ColituSecurityTestFixesTests
{
    private const string InstallBin = "/opt/colitu-vpn/bin";

    // ── Kill switch ──

    [Fact]
    public void KillSwitch_DropsDnsOutsideTheTunnel_BeforeTheLocalNetwork()
    {
        var script = ColituKillSwitch.BuildEngageScript([IPAddress.Parse("203.0.113.7")], 1, appUid: 1000);
        var dns = script.IndexOf("meta skuid != 0 udp dport 53 drop", StringComparison.Ordinal);
        var tun = script.IndexOf("oifname { \"xray_tun\", \"singbox_tun\" } accept", StringComparison.Ordinal);
        var lan = script.IndexOf("192.168.0.0/16", StringComparison.Ordinal);
        var plane = script.IndexOf("ip daddr { 203.0.113.7 } accept", StringComparison.Ordinal);
        dns.Should().BeGreaterThan(tun, "DNS inside the tunnel stays allowed");
        dns.Should().BeLessThan(lan, "a resolver on the LAN gets no names");
        dns.Should().BeLessThan(plane, "the allowed addresses (public resolvers among them) get no plain DNS either");
        script.Should().Contain("meta skuid != 0 tcp dport 53 drop");
    }

    [Fact]
    public void KillSwitch_DhcpV6OnlyFromItsClientPort()
    {
        var script = ColituKillSwitch.BuildEngageScript([], 1);
        script.Should().Contain("meta nfproto ipv6 udp sport 546 udp dport 547 accept");
        script.Should().NotContain("    udp dport 547 accept");
    }

    [Fact]
    public void KillSwitch_ControlPlaneOnlyForTheAppsUserAndRoot()
    {
        var script = ColituKillSwitch.BuildEngageScript([IPAddress.Parse("203.0.113.7"), IPAddress.Parse("2001:db8::5")], 1, ["198.51.100.0/24"], appUid: 1000);
        script.Should().Contain("meta skuid { 0, 1000 } ip daddr { 203.0.113.7 } accept");
        script.Should().Contain("meta skuid { 0, 1000 } ip6 daddr { 2001:db8::5 } accept");
        // Networks the user excluded from the VPN stay open to every program.
        script.Should().Contain("    ip daddr { 198.51.100.0/24 } accept");
    }

    [Fact]
    public void KillSwitch_WithoutTheLocalNetwork_KeepsLinkLocalAndDhcp()
    {
        var script = ColituKillSwitch.BuildEngageScript([], 1, allowLan: false);
        script.Should().NotContain("192.168.0.0/16").And.NotContain("10.0.0.0/8").And.NotContain("fc00::/7");
        script.Should().Contain("ip daddr { 169.254.0.0/16, 224.0.0.0/4, 255.255.255.255 } accept");
        script.Should().Contain("ip6 daddr { fe80::/10, ff00::/8 } accept");
        script.Should().Contain("udp sport 68 udp dport 67 accept");
    }

    [Fact]
    public void KillSwitchLan_HasATranslationInEveryLanguage()
    {
        Loc.ValuesOf("settings.killSwitchLan").Should().HaveCount(3);
        Loc.ValuesOf("settings.killSwitchLanHint").Should().HaveCount(3);
    }

    // ── Root config policy ──

    [Theory]
    [InlineData("sing-box", """{"inbounds": [{"type": "mixed", "tag": "socks", "listen": "0.0.0.0", "listen_port": 10808}]}""")]
    [InlineData("sing-box", """{"inbounds": [{"type": "mixed", "tag": "socks", "listen_port": 10808}]}""")]
    [InlineData("sing-box", """{"inbounds": [{"type": "mixed", "tag": "socks", "listen": "192.168.1.5", "listen_port": 10808}]}""")]
    [InlineData("sing-box", """{"experimental": {"clash_api": {"external_controller": "0.0.0.0:9090", "secret": "s"}}}""")]
    [InlineData("sing-box", """{"experimental": {"clash_api": {"external_controller": "127.0.0.1:9090"}}}""")]
    [InlineData("sing-box", """{"experimental": {"v2ray_api": {"listen": "127.0.0.1:8080"}}}""")]
    [InlineData("xray", """{"inbounds": [{"protocol": "socks", "listen": "0.0.0.0", "port": 10808}]}""")]
    [InlineData("xray", """{"inbounds": [{"protocol": "socks", "port": 10808}]}""")]
    public void RootPolicy_RefusesListenersOffLoopback(string kind, string json)
    {
        RootConfigPolicy.Check(json, kind, InstallBin, out var reason).Should().BeFalse();
        reason.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("sing-box", """{"inbounds": [{"type": "tun", "tag": "tun-in"}, {"type": "mixed", "tag": "socks", "listen": "127.0.0.1", "listen_port": 10808}]}""")]
    [InlineData("sing-box", """{"experimental": {"clash_api": {"external_controller": "127.0.0.1:9090", "secret": "s"}}}""")]
    [InlineData("xray", """{"inbounds": [{"protocol": "tun", "tag": "tun"}, {"protocol": "socks", "listen": "127.0.0.1", "port": 10808}, {"protocol": "dokodemo-door", "listen": "[::1]", "port": 10813}]}""")]
    public void RootPolicy_AcceptsLoopbackListeners(string kind, string json)
    {
        RootConfigPolicy.Check(json, kind, InstallBin, out var reason).Should().BeTrue(reason);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1:9090", true)]
    [InlineData("[::1]:9090", true)]
    [InlineData("::1", true)]
    [InlineData("localhost:80", true)]
    [InlineData("::1:9090", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LoopbackListen(string? value, bool loopback)
    {
        RootConfigPolicy.IsLoopbackListen(value).Should().Be(loopback);
    }

    // ── Launcher: the hash and the owner ──

    private static string Launcher => Path.Combine(AppContext.BaseDirectory, "TestData", "colitu-core-root");

    [Fact]
    public void Launcher_RunsOnlyTheConfigTheAppHashed()
    {
        var script = File.ReadAllText(Launcher);
        script.Should().Contain("[ \"$#\" -eq 3 ] || refuse \"usage: colitu-core-root <core> <config.json> <sha256>\"");
        script.Should().Contain("[ \"${#want}\" -eq 64 ] || refuse \"bad config hash\"");
        script.Should().Contain("got=$(sha256sum -- \"$copy\" | cut -d ' ' -f 1)");
        script.Should().Contain("[ \"$got\" = \"$want\" ]");
        // The hash is checked before the core parses the copy.
        script.IndexOf("[ \"$got\" = \"$want\" ]", StringComparison.Ordinal).Should()
            .BeLessThan(script.IndexOf("\"$core\" format -c \"$copy\"", StringComparison.Ordinal));
        script.Should().Contain("[ \"$(realpath -e -- \"$config\" 2>/dev/null)\" = \"$config\" ]");
        script.Should().Contain("[ \"$(stat -c '%u' -- \"$config\" 2>/dev/null)\" = \"$SUDO_UID\" ]");
    }

    // ── Ping rule: only a root-owned exe ──

    private static RootCoreGuard.StatFunc Stat(Dictionary<string, (long Uid, int Mode)> table) =>
        path => table.TryGetValue(path, out var value) ? value : null;

    [Fact]
    public void TrustedAppPath_OnlyForARootOwnedInstall()
    {
        var packaged = new Dictionary<string, (long Uid, int Mode)>
        {
            ["/"] = (0, 0b111_101_101),
            ["/opt"] = (0, 0b111_101_101),
            ["/opt/colitu-vpn"] = (0, 0b111_101_101),
            ["/opt/colitu-vpn/ColituVPN"] = (0, 0b111_101_101),
        };
        RootCoreGuard.TrustedAppPath("/opt/colitu-vpn/ColituVPN", path => path, Stat(packaged)).Should().Be("/opt/colitu-vpn/ColituVPN");

        var portable = new Dictionary<string, (long Uid, int Mode)>
        {
            ["/"] = (0, 0b111_101_101),
            ["/home"] = (0, 0b111_101_101),
            ["/home/u"] = (1000, 0b111_101_101),
            ["/home/u/ColituVPN"] = (1000, 0b111_101_101),
        };
        RootCoreGuard.TrustedAppPath("/home/u/ColituVPN", path => path, Stat(portable)).Should().BeNull();

        var groupWritable = new Dictionary<string, (long Uid, int Mode)>(packaged) { ["/opt/colitu-vpn"] = (0, 0b111_111_101) };
        RootCoreGuard.TrustedAppPath("/opt/colitu-vpn/ColituVPN", path => path, Stat(groupWritable)).Should().BeNull();
        RootCoreGuard.TrustedAppPath(null, path => path, Stat(packaged)).Should().BeNull();
    }

    // ── Updater: redirects by hand, every hop checked ──

    private static readonly IReadOnlySet<string> Mirrors = new HashSet<string> { "mirror.example.test" };

    private sealed class ScriptedHandler(params (HttpStatusCode Status, string? Location)[] steps) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var (status, location) = steps[Math.Min(Requests.Count - 1, steps.Length - 1)];
            var response = new HttpResponseMessage(status) { RequestMessage = request };
            if (location != null)
            {
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            }
            else
            {
                response.Content = new StringContent("ok");
            }
            return Task.FromResult(response);
        }
    }

    private static Task<HttpResponseMessage> Send(ScriptedHandler handler) =>
        ColituUpdateService.SendCheckedAsync(new HttpClient(handler), new Uri("https://colitu.com/downloads/linux/latest.json"), null,
            HttpCompletionOption.ResponseContentRead, CancellationToken.None, Mirrors);

    [Fact]
    public async Task Updater_AllowedRedirectsAreFollowed()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.Found, "https://dl.colitu.com/a.deb"),
            (HttpStatusCode.MovedPermanently, "https://mirror.example.test/b.deb"),
            (HttpStatusCode.TemporaryRedirect, "/c.deb"),
            (HttpStatusCode.OK, null));
        using var response = await Send(handler);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requests.Select(uri => uri.AbsoluteUri).Should().Equal(
            "https://colitu.com/downloads/linux/latest.json",
            "https://dl.colitu.com/a.deb",
            "https://mirror.example.test/b.deb",
            "https://mirror.example.test/c.deb");
    }

    [Theory]
    [InlineData("https://evil.example.test/x.deb")]
    [InlineData("https://colitu.com.example.test/x.deb")]
    [InlineData("http://colitu.com/x.deb")]
    [InlineData("https://user:pass@colitu.com/x.deb")]
    [InlineData("https://colitu.com:8443/x.deb")]
    [InlineData("https://mirror.example.test:8443/x.deb")]
    public async Task Updater_UntrustedRedirectIsRefused(string location)
    {
        var handler = new ScriptedHandler((HttpStatusCode.Found, location), (HttpStatusCode.OK, null));
        var send = () => Send(handler);
        await send.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().HaveCount(1, "the untrusted target must never be requested");
    }

    [Fact]
    public async Task Updater_FourthRedirectIsRefused()
    {
        var handler = new ScriptedHandler((HttpStatusCode.Found, "https://colitu.com/next"));
        var send = () => Send(handler);
        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*too many*");
        handler.Requests.Should().HaveCount(1 + ColituUpdateService.MaxRedirects);
    }

    [Fact]
    public void Updater_HandlerDoesNotFollowRedirects()
    {
        using var handler = ColituUpdateService.CreateHandler();
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseProxy.Should().BeFalse();
        handler.SslOptions.RemoteCertificateValidationCallback.Should().NotBeNull();
    }

    [Fact]
    public void Updater_MaskForLogHidesTheHost()
    {
        var masked = ColituUpdateService.MaskForLog(new Uri("https://user:pw@node7.secret-mirror.example.test:8443/x"));
        masked.Should().Be("https://***@***.test:8443");
    }
}

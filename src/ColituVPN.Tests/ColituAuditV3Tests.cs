using System.Text.Json.Nodes;
using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler.Fmt;
using ServiceLib.Models.Entities;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>2026-10-13 independent audit v3 (Linux): L3-01, L3-02, L3-03, L3-05, L3-09.</summary>
public class ColituAuditV3Tests
{
    private const string PrimaryConfig = """
    {
      "inbounds": [ { "type": "mixed", "tag": "socks", "listen": "127.0.0.1", "listen_port": 10808 } ],
      "outbounds": [
        { "type": "vless", "tag": "proxy", "server": "primary.example.test", "server_port": 443, "uuid": "11111111-2222-3333-4444-555555555555", "tls": { "enabled": true } },
        { "type": "direct", "tag": "direct" }
      ],
      "route": { "rules": [], "final": "proxy" }
    }
    """;

    // ── L3-01: Hysteria2/TUIC spares get TLS ─────────────────────────────
    [Fact]
    public void Hysteria2SpareLink_Normalized_GetsTls()
    {
        var parsed = FmtHandler.ResolveConfig("hysteria2://secret@hy.example.test:8443?sni=hy.example.test&insecure=0#Colitu", out _)!;
        parsed.StreamSecurity.Should().BeNullOrEmpty("the link itself has no security=tls");

        var node = ColituWarmSpare.NormalizeSpareProfile(parsed);

        node.StreamSecurity.Should().Be("tls");
        node.Alpn.Should().BeEmpty();
    }

    [Fact]
    public void TuicSpare_Normalized_GetsTlsAndH3()
    {
        var node = ColituWarmSpare.NormalizeSpareProfile(new ProfileItem { ConfigType = EConfigType.TUIC, Address = "tuic.example.test", Port = 443 });

        node.StreamSecurity.Should().Be("tls");
        node.Alpn.Should().Be("h3");
        node.CoreType.Should().Be(ECoreType.sing_box);
    }

    [Theory]
    [InlineData("""{ "type": "hysteria2", "tag": "warm-spare", "server": "hy.example.test", "server_port": 8443 }""")]
    [InlineData("""{ "type": "tuic", "tag": "warm-spare", "server": "tuic.example.test", "server_port": 443, "tls": { "enabled": false } }""")]
    public void SingboxProblems_QuicOutboundWithoutTls_IsReported(string spare)
    {
        var root = JsonNode.Parse(PrimaryConfig)!.AsObject();
        root["outbounds"]!.AsArray().Add(JsonNode.Parse(spare));

        ColituWarmSpare.SingboxProblems(root.ToJsonString()).Should().ContainSingle(problem => problem.Contains("without tls"));
    }

    [Fact]
    public void SingboxProblems_QuicOutboundWithTls_IsFine()
    {
        var root = JsonNode.Parse(PrimaryConfig)!.AsObject();
        root["outbounds"]!.AsArray().Add(JsonNode.Parse("""{ "type": "hysteria2", "tag": "warm-spare", "server": "hy.example.test", "server_port": 8443, "tls": { "enabled": true } }"""));

        ColituWarmSpare.SingboxProblems(root.ToJsonString()).Should().BeEmpty();
    }

    // ── L3-02 / L3-03: root policy ───────────────────────────────────────
    [Theory]
    [InlineData("xray", """{"api": {"tag": "api", "listen": "0.0.0.0:10085", "services": ["HandlerService"]}}""", "listener not on loopback")]
    [InlineData("xray", """{"metrics": {"tag": "m", "listen": "192.168.1.10:11111"}}""", "listener not on loopback")]
    [InlineData("xray", """{"routing": {"script": "rules.lua", "rules": []}}""", "Lua script")]
    [InlineData("xray", """{"dns": {"script": "dns.lua"}}""", "Lua script")]
    [InlineData("sing-box", """{"services": [{"type": "resolved", "tag": "r", "listen": "127.0.0.1"}]}""", "sing-box services")]
    [InlineData("sing-box", """{"endpoints": [{"type": "tailscale", "tag": "ts"}]}""", "sing-box endpoint other than WireGuard")]
    [InlineData("sing-box", """{"endpoints": [{"type": "wireguard", "tag": "wg", "listen_port": 51820}]}""", "endpoint listen_port")]
    public void RootPolicy_RefusesListenersServicesAndScripts(string kind, string json, string reason)
    {
        RootConfigPolicy.Check(json, kind, "/opt/colitu-vpn/bin", out var why).Should().BeFalse();
        why.Should().Be(reason);
    }

    [Theory]
    [InlineData("xray", """{"api": {"tag": "api", "listen": "127.0.0.1:10085", "services": ["StatsService"]}}""")]
    [InlineData("xray", """{"routing": {"script": "", "rules": []}}""")]
    [InlineData("sing-box", """{"services": [], "endpoints": [{"type": "wireguard", "tag": "wg", "address": ["10.0.0.2/32"]}]}""")]
    public void RootPolicy_AcceptsLoopbackApiEmptyScriptAndWireGuard(string kind, string json)
    {
        RootConfigPolicy.Check(json, kind, "/opt/colitu-vpn/bin", out var why).Should().BeTrue(why);
    }

    // ── L3-05: log masking and the session file ──────────────────────────
    [Theory]
    [InlineData("node7.example-vpn.net", "***.net")]
    [InlineData("203.0.113.7", "203.0.x.x")]
    [InlineData("api.colitu.com", "api.colitu.com")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("", "?")]
    public void LogPrivacy_ForLog_MasksNodesAndMirrors(string value, string expected)
    {
        ColituLogPrivacy.ForLog(value).Should().Be(expected);
    }

    [Fact]
    public void Session_AccountDetails_AreEncryptedOnDisk()
    {
        var session = new ColituSession
        {
            AccessToken = "a",
            RefreshToken = "r",
            PendingEmail = "user@example.test",
            CachedUser = new ColituUser { Email = "user@example.test" }
        };

        var disk = ColituAuthService.ForDisk(session);
        var text = System.Text.Json.JsonSerializer.Serialize(disk);

        text.Should().NotContain("user@example.test");
        disk.Profile.Should().NotBeNullOrEmpty();
        ColituAuthService.RestoreProfile(disk);
        disk.CachedUser!.Email.Should().Be("user@example.test");
        disk.PendingEmail.Should().Be("user@example.test");
        disk.Profile.Should().BeNull();
    }

    // ── L3-09: "offline" survives a wrapped error ────────────────────────
    [Fact]
    public void ConnectException_OfflineSurvivesWrapping()
    {
        var inner = new ColituConnectException("offline", offline: true);
        ColituConnectException.IsOffline(new InvalidOperationException("wrap", inner)).Should().BeTrue();
        ColituConnectException.IsOffline(new InvalidOperationException("other")).Should().BeFalse();
    }

    // ── L3-02: root runs only the config this app wrote ──────────────────
    [Fact]
    public void Source_RootRunsOnlyTheWrittenConfig()
    {
        var root = AppSource();
        var admin = File.ReadAllText(Path.Combine(root, "..", "ServiceLib", "Manager", "CoreAdminManager.cs"));
        admin.Should().Contain("CoreConfigHandler.WrittenConfigHash(configFile)");
    }

    private static string AppSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "ColituVPN", "Services")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir!.FullName, "ColituVPN");
    }
}

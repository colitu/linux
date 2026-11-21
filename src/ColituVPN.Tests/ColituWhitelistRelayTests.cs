using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using v2rayN.Desktop.Views;
using Xunit;

namespace v2rayN.Tests;

public class ColituWhitelistRelayTests
{
    private const string RelayPayload = """{"schema_version":1,"protocol":"vless-reality-relay","endpoint":{"host":"relay.example.test","port":443},"credentials":{"uuid":"00000000-0000-4000-8000-000000000005"},"transport":{"type":"tcp"},"security":{"type":"reality","server_name":"exit.example.test","public_key":"PBK","short_id":"ab","fingerprint":"firefox"},"dns_policy":{},"routing_policy":{}}""";

    private static ColituConfigCandidate C(string protocol) => new() { Protocol = protocol, ShareLink = "vless://" + protocol };

    private static ColituVpnConfigResponse Config(params string[] protocols) => new() { ServerId = "n", Candidates = protocols.Select(C).ToList() };

    [Fact]
    public void RelayPayload_BecomesARealityVisionLinkWithTheProfileFingerprint()
    {
        var link = ColituShareLinkBuilder.Build(JsonDocument.Parse(RelayPayload).RootElement.Clone(), "x");

        link.Should().StartWith("vless://00000000-0000-4000-8000-000000000005@relay.example.test:443?")
            .And.Contain("security=reality").And.Contain("flow=xtls-rprx-vision").And.Contain("type=tcp")
            .And.Contain("sni=exit.example.test").And.Contain("pbk=PBK").And.Contain("sid=ab").And.Contain("fp=firefox");
        ColituShareLinkBuilder.Build(JsonDocument.Parse(RelayPayload.Replace("\"type\":\"tcp\"", "\"type\":\"ws\"")).RootElement.Clone(), "x").Should().BeNull();
    }

    [Fact]
    public void WhitelistTransports_AreOnlyForWhitelistServers_AndNeverInNormalOrOnChains()
    {
        ColituApiClient.IsWhitelistProtocol("vless-reality-relay").Should().BeTrue();
        ColituApiClient.IsWhitelistProtocol("vless-ws").Should().BeTrue();
        ColituApiClient.IsWhitelistProtocol("vless-reality").Should().BeFalse();

        var config = Config("vless-reality", "vless-ws", "vless-reality-relay", "trojan");
        ColituApiClient.WithoutWhitelistProtocols(config).Candidates.Select(item => item.Protocol).Should().Equal("vless-reality", "trojan");
        ColituApiClient.WithoutWhitelistProtocols(Config("vless-ws")).Candidates.Select(item => item.Protocol).Should().Equal("vless-ws");
        ColituApiClient.RestrictToVless(config)!.Candidates.Select(item => item.Protocol).Should().Equal("vless-reality");
    }

    [Fact]
    public void Forcing_UsesOnlyWhitelistTransports_RelayFirst()
    {
        var forced = ColituApiClient.RestrictToWhitelist(Config("vless-reality", "vless-ws", "vless-reality-relay"));

        forced!.Candidates.Select(item => item.Protocol).Should().Equal("vless-reality-relay", "vless-ws");
        ColituApiClient.RestrictToWhitelist(Config("vless-ws"))!.Candidates.Select(item => item.Protocol).Should().Equal("vless-ws");
        ColituApiClient.RestrictToWhitelist(Config("vless-reality", "trojan")).Should().BeNull();
        ColituVpnService.TransportPriority("vless-reality-relay").Should().BeLessThan(ColituVpnService.TransportPriority("vless-ws"));
    }

    [Fact]
    public void VirtualRow_AlsoForRelayServers_OnePerServer_WithCountryAndCity()
    {
        ColituVpnServer S(string id, string cc, string city, params string[] protocols) =>
            new() { Id = id, Name = id, CountryCode = cc, Country = cc, City = city, WhitelistOk = true, Protocols = protocols.ToList() };
        var servers = new[] { S("cz", "CZ", "Prague", "vless-reality-relay"), S("fi", "FI", "Helsinki", "vless-reality-relay", "vless-ws"), S("de", "DE", "Berlin", "vless-reality") };

        var rows = ColituLocationSort.WhitelistRows(servers, "RU");

        rows.Select(server => server.Id).Should().Equal("cz", "fi");
        var views = rows.Select(ColituServerRow.Whitelist).ToList();
        views.Should().OnlyContain(row => row.Title == Loc.I["transport.vless-reality-relay"]);
        views.Select(row => row.Subtitle).Distinct().Should().HaveCount(2);
        views.Should().OnlyContain(row => row.Subtitle.Length > 0 && row.HasInfo);
    }

    [Fact]
    public void RelayName_IsTheWhitelistName()
    {
        Loc.ValuesOf("transport.vless-reality-relay").Should().Equal(Loc.ValuesOf("transport.vless-ws"));
        ColituTransportNames.Of("vless-reality-relay", Loc.I).Should().Be(Loc.I["transport.vless-ws"]);
    }
}

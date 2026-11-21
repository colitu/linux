using AwesomeAssertions;
using v2rayN.Desktop.Services;
using v2rayN.Desktop.Views;
using Xunit;

namespace v2rayN.Tests;

/// <summary>The virtual "Beyaz Liste" row (Russia only) replaces the old pinning of the server itself.</summary>
public class ColituWhitelistPinTests
{
    private static ColituVpnServer S(string id, bool wl, params string[] protocols) =>
        new() { Id = id, Name = id, CountryCode = "GB", Country = "GB", WhitelistOk = wl, Protocols = protocols.ToList() };

    [Fact]
    public void VirtualRow_OnlyForRussia_WhitelistOk_AndVlessWs()
    {
        var servers = new[] { S("wl", true, "vless-reality", "vless-ws"), S("plain", true, "vless-reality"), S("noflag", false, "vless-ws") };

        ColituLocationSort.WhitelistRows(servers, "RU").Select(server => server.Id).Should().Equal("wl");
        ColituLocationSort.WhitelistRows(servers, "ru").Should().HaveCount(1);
        ColituLocationSort.WhitelistRows(servers, "DE").Should().BeEmpty();
        ColituLocationSort.WhitelistRows(servers, null).Should().BeEmpty();
        ColituLocationSort.WhitelistRows(servers, "").Should().BeEmpty();
    }

    [Fact]
    public void NormalRow_KeepsItsNameCountryAndHasNoInfoButton()
    {
        var server = S("wl", true, "vless-reality", "vless-ws");
        server.Name = "Londra";
        var normal = ColituServerRow.From(server, 40);
        var virtualRow = ColituServerRow.Whitelist(server);

        normal.IsWhitelistRow.Should().BeFalse();
        normal.HasInfo.Should().BeFalse();
        normal.Title.Should().NotBe(Loc.I["transport.vless-ws"]);
        normal.PingText.Should().Be("40 ms");
        virtualRow.IsWhitelistRow.Should().BeTrue();
        virtualRow.HasInfo.Should().BeTrue();
        virtualRow.Title.Should().Be(Loc.I["transport.vless-ws"]);
        virtualRow.PingText.Should().BeEmpty();
        virtualRow.SelectionKey.Should().Be("whitelist:wl");
        normal.SelectionKey.Should().Be("wl");
    }

    [Fact]
    public void SelectionKey_RoundTrips()
    {
        ColituLocationSort.WhitelistNodeOf(ColituLocationSort.WhitelistKey("node-48")).Should().Be("node-48");
        ColituLocationSort.WhitelistNodeOf("node-48").Should().BeNull();
        ColituLocationSort.WhitelistNodeOf("whitelist:").Should().BeNull();
        ColituLocationSort.WhitelistNodeOf(null).Should().BeNull();
    }

    [Fact]
    public void ForcedProtocol_KeepsOnlyVlessWs_AndRefusesANodeWithoutIt()
    {
        var config = new ColituVpnConfigResponse
        {
            ServerId = "wl",
            Candidates =
            [
                new ColituConfigCandidate { Protocol = "vless-reality", ShareLink = "vless://a" },
                new ColituConfigCandidate { Protocol = "vless-ws", ShareLink = "vless://b" },
                new ColituConfigCandidate { Protocol = "trojan", ShareLink = "trojan://c" }
            ]
        };

        var forced = ColituApiClient.RestrictToProtocol(config, "vless-ws");

        forced!.Candidates.Select(item => item.Protocol).Should().Equal("vless-ws");
        forced.ProtocolType.Should().Be("vless-ws");
        ColituApiClient.RestrictToProtocol(forced, "vless-ws").Should().BeSameAs(forced);
        ColituApiClient.RestrictToProtocol(new ColituVpnConfigResponse { Candidates = [config.Candidates[0]] }, "vless-ws").Should().BeNull();
        // Not a chain protocol: a multihop / rotating restriction never keeps it.
        ColituApiClient.RestrictToVless(config)!.Candidates.Select(item => item.Protocol).Should().Equal("vless-reality");
    }
}

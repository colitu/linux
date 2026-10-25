using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

public class ColituNetworkTests
{
    [Theory]
    [InlineData("198.18.0.36", true)]
    [InlineData("198.19.255.1", true)]
    [InlineData("198.17.0.1", false)]
    [InlineData("203.0.113.10", false)]
    [InlineData("192.168.0.1", false)]
    public void FakeIpPool_IsRecognised(string address, bool placeholder)
    {
        ColituNetwork.IsPlaceholder(IPAddress.Parse(address)).Should().Be(placeholder);
    }

    [Theory]
    [InlineData("tun0")]
    [InlineData("wg0")]
    [InlineData("tailscale0")]
    [InlineData("docker0")]
    [InlineData("br-1a2b3c")]
    [InlineData("veth12ab")]
    [InlineData("virbr0")]
    [InlineData("singbox_tun")]
    [InlineData("xray_tun")]
    [InlineData("lo")]
    public void VirtualInterfaces_AreNeverUsedForTheCore(string name)
    {
        ColituNetwork.IsVirtual(name).Should().BeTrue();
    }

    [Fact]
    public void PhysicalInterface_IsNeverAVirtualOne()
    {
        var name = ColituNetwork.PhysicalInterfaceName();
        if (name == null)
        {
            return; // no default route on this machine (CI, or not Linux)
        }
        ColituNetwork.IsVirtual(name).Should().BeFalse();
    }

    [Theory]
    [InlineData("xray_tun", true)]
    [InlineData("singbox_tun", true)]
    [InlineData("tun0", false)]
    public void OwnTunnels_AreNotAnotherVpn(string name, bool own)
    {
        ColituNetwork.IsOwnTun(name).Should().Be(own);
    }

    [Fact]
    public async Task LiteralAddress_IsReturnedWithoutLookup()
    {
        (await ColituNetwork.ResolveServerAsync("203.0.113.10")).Should().Be(IPAddress.Parse("203.0.113.10"));
    }

    [Fact]
    public void ShareLink_UsesThePinnedAddressAndKeepsTheNameForTls()
    {
        var payload = JsonDocument.Parse("""{"schema_version":1,"protocol":"trojan","endpoint":{"host":"vpn.example.com","port":443},"credentials":{"password":"p"},"transport":{"type":"tcp"},"security":{"type":"tls","server_name":"vpn.example.com"}}""").RootElement.Clone();

        ColituShareLinkBuilder.HostOf(payload).Should().Be("vpn.example.com");
        var link = ColituShareLinkBuilder.Build(payload, "x", "203.0.113.7");

        link.Should().StartWith("trojan://p@203.0.113.7:443?").And.Contain("sni=vpn.example.com");
    }

    [Fact]
    public void ShareLink_WithoutPin_DialsTheName()
    {
        var payload = JsonDocument.Parse("""{"schema_version":1,"protocol":"shadowsocks","endpoint":{"host":"vpn.example.com","port":8388},"credentials":{"method":"aes-128-gcm","password":"p"},"transport":{"type":"tcp"},"security":{"type":"none"}}""").RootElement.Clone();

        ColituShareLinkBuilder.Build(payload, "x").Should().Contain("@vpn.example.com:8388");
    }

    [Fact]
    public void FirstARecord_SkipsTheQuestionAndCnames()
    {
        // Reply to an A query for www.example.com: a CNAME to example.com (name compressed) then its A record.
        var reply = new byte[]
        {
            0x12, 0x34, 0x81, 0x80, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00,
            3, (byte)'w', (byte)'w', (byte)'w', 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0,
            0x00, 0x01, 0x00, 0x01,
            0xC0, 0x0C, 0x00, 0x05, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x02, 0xC0, 0x10,
            0xC0, 0x10, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x04, 203, 0, 113, 9
        };

        ColituNetwork.FirstARecord(reply).Should().Be(IPAddress.Parse("203.0.113.9"));
        ColituNetwork.DnsAnswerState(reply, 0x1234).Should().BeTrue();
        ColituNetwork.DnsAnswerState(reply, 0x4321).Should().BeNull("a reply to another query is ignored");
    }

    [Fact]
    public void FirstARecord_IsNullForErrorsAndTruncatedReplies()
    {
        var nameError = new byte[] { 0x12, 0x34, 0x81, 0x83, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        ColituNetwork.FirstARecord(nameError).Should().BeNull();
        var truncated = new byte[] { 0x12, 0x34, 0x81, 0x80, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x0C, 0x00, 0x01 };
        ColituNetwork.FirstARecord(truncated).Should().BeNull();
    }

    [Fact]
    public void DnsQuery_AsksRecursivelyForTheARecord()
    {
        var query = ColituNetwork.BuildDnsQuery(0xBEEF, "vpn.example.com");

        query.Take(12).Should().Equal(new byte[] { 0xBE, 0xEF, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        query.Skip(12).Should().Equal(new byte[] { 3, (byte)'v', (byte)'p', (byte)'n', 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            3, (byte)'c', (byte)'o', (byte)'m', 0, 0x00, 0x01, 0x00, 0x01 });
    }

    [Fact]
    public void AdapterDns_FromResolvectl_TakesTheLinksIPv4Servers()
    {
        const string output = "Link 2 (wlp3s0): 192.168.1.1 fe80::1%3 10.0.0.53#dns.example 10.0.0.54:53 127.0.0.53 198.18.0.2\n";

        ColituNetwork.ParseResolvectlDns(output).Should().Equal(IPAddress.Parse("192.168.1.1"), IPAddress.Parse("10.0.0.53"), IPAddress.Parse("10.0.0.54"));
        ColituNetwork.ParseResolvectlDns("Link 2 (wlp3s0):\n").Should().BeEmpty();
        ColituNetwork.ParseResolvectlDns(null).Should().BeEmpty();
    }

    [Fact]
    public void AdapterDns_FromResolvConf_SkipsTheLocalStub()
    {
        ColituNetwork.ParseResolvConf("nameserver 127.0.0.53\noptions edns0 trust-ad\nsearch .\n").Should().BeEmpty();
        ColituNetwork.ParseResolvConf("# NetworkManager\nnameserver 192.168.0.1\nnameserver 2001:db8::1\nnameserver 192.168.0.1\n")
            .Should().Equal(IPAddress.Parse("192.168.0.1"));
    }

    [Theory]
    [InlineData("hysteria2://p%40ss@vpn.example.com:8443?sni=vpn.example.com&insecure=0&mport=20000-30000#Colitu x", "hysteria2://p%40ss@203.0.113.4:8443?sni=vpn.example.com&insecure=0&mport=20000-30000#Colitu x")]
    [InlineData("vless://uuid@vpn.example.com:443?security=reality&sni=www.example.org&type=tcp#Colitu x", "vless://uuid@203.0.113.4:443?security=reality&sni=www.example.org&type=tcp#Colitu x")]
    [InlineData("trojan://pw@vpn.example.com:4433?security=tls&sni=vpn.example.com&type=tcp#Colitu x", "trojan://pw@203.0.113.4:4433?security=tls&sni=vpn.example.com&type=tcp#Colitu x")]
    [InlineData("ss://YWVzLTEyOC1nY206cA_-@vpn.example.com:8388#Colitu x", "ss://YWVzLTEyOC1nY206cA_-@203.0.113.4:8388#Colitu x")]
    public void StoredLinks_DialThePinnedAddress(string link, string pinned)
    {
        ColituShareLinkBuilder.DialHostOf(link).Should().Be("vpn.example.com");
        ColituShareLinkBuilder.WithDialAddress(link, IPAddress.Parse("203.0.113.4")).Should().Be(pinned);
    }

    [Theory]
    [InlineData("hysteria2://p@203.0.113.4:8443?sni=vpn.example.com#x")]
    [InlineData("vless://uuid@[2001:db8::1]:443?sni=a#x")]
    [InlineData("not a link")]
    public void StoredLinks_ThatDialAnAddress_AreLeftAlone(string link)
    {
        ColituShareLinkBuilder.DialHostOf(link).Should().BeNull();
    }

    [Fact]
    public void StoredLinks_PinAnIPv6AddressInBrackets()
    {
        ColituShareLinkBuilder.WithDialAddress("ss://abc@vpn.example.com:8388#x", IPAddress.Parse("2001:db8::1"))
            .Should().Be("ss://abc@[2001:db8::1]:8388#x");
    }

    [Fact]
    public async Task StoredSettings_ThatDialAddresses_AreLeftAsTheyAre()
    {
        var config = new ColituVpnConfigResponse
        {
            Candidates =
            [
                new ColituConfigCandidate { ShareLink = "trojan://pw@203.0.113.4:4433?security=tls&sni=vpn.example.com#a" },
                new ColituConfigCandidate { ShareLink = "ss://abc@203.0.113.5:8388#b" }
            ]
        };
        await ColituApiClient.PinDialHostsAsync(config, CancellationToken.None);
        config.RawConfig.Should().BeNull("nothing dialled a name, so nothing was rewritten");
        config.Candidates[0].ShareLink.Should().Be("trojan://pw@203.0.113.4:4433?security=tls&sni=vpn.example.com#a");
    }

    [Fact]
    public async Task UdpDns_ResolvesARealName_AgainstAGivenServer()
    {
        // Real network: only with COLITU_LIVE_DNS_SERVER=<resolver address> (never in CI).
        if (Environment.GetEnvironmentVariable("COLITU_LIVE_DNS_SERVER") is not { Length: > 0 } server)
        {
            return;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var address = await ColituNetwork.QueryUdpDnsAsync(IPAddress.Parse(server), "colitu.com", null, timeout.Token);
        address.Should().NotBeNull();
    }
}

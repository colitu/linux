using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>On-device masking of visited sites in the connection report.</summary>
public class ColituSiteRedactionTests
{
    private static readonly string[] Servers = ["de1.nodes.example.net", "203.0.113.7"];

    private static string Mask(string line) => ColituSiteRedaction.Apply(line, Servers);

    [Fact]
    public void DialTargets_LoseTheirHostButKeepProtocolAndPort()
    {
        Mask("dial tcp:www.example.org:443 failed").Should().Be("dial tcp:<site>:443 failed");
        Mask("outbound udp:198.51.100.20:53").Should().Be("outbound udp:<ip>:53");
        Mask("connect TCP:[2001:db8::1]:8443 ok").Should().Be("connect TCP:<ip>:8443 ok");
    }

    [Theory]
    [InlineData("lookup news.example.org: no such host", "lookup <site>: no such host")]
    [InlineData("response for video.example.org.", "response for <site>.")]
    [InlineData("sniffed domain: chat.example.org", "sniffed domain: <site>")]
    [InlineData("resolving 198.51.100.9", "resolving <ip>")]
    public void Lookups_AreMasked(string line, string expected)
    {
        Mask(line).Should().Be(expected);
    }

    [Theory]
    [InlineData("domain config.json loaded")]
    [InlineData("lookup geosite.dat")]
    [InlineData("resolve helper.cs")]
    [InlineData("lookup localname")]
    public void FileNamesAndSingleLabels_AreNotLookups(string line)
    {
        Mask(line).Should().Be(line);
    }

    [Fact]
    public void UrlHosts_AreMasked()
    {
        Mask("GET https://shop.example.org/cart?id=1").Should().Be("GET https://<site>/cart?id=1");
        Mask("open wss://stream.example.org").Should().Be("open wss://<site>");
        Mask("http://198.51.100.5:8080/x").Should().Be("http://<ip>:8080/x");
        Mask("http://[2001:db8::5]/x").Should().Be("http://<ip>/x");
    }

    [Fact]
    public void KnownServersAndTheirDomain_AreKept()
    {
        Mask("dial tcp:de1.nodes.example.net:443").Should().Be("dial tcp:de1.nodes.example.net:443");
        Mask("dial tcp:fi2.nodes.example.net:443").Should().Be("dial tcp:fi2.nodes.example.net:443");
        Mask("udp:203.0.113.7:8443").Should().Be("udp:203.0.113.7:8443");
        Mask("https://other.example.org/").Should().Be("https://<site>/");
    }

    [Theory]
    [InlineData("dial tcp:127.0.0.1:10808")]
    [InlineData("dial tcp:localhost:10808")]
    [InlineData("dial tcp:[::1]:10808")]
    [InlineData("https://colitu.com/pricing")]
    [InlineData("https://api.colitu.com/v1")]
    [InlineData("GET https://www.gstatic.com/generate_204")]
    [InlineData("GET http://connectivitycheck.gstatic.com/generate_204")]
    [InlineData("https://captive.apple.com/")]
    [InlineData("https://www.apple.com/library/test/success.html")]
    [InlineData("https://ya.ru/")]
    [InlineData("http://www.msftconnecttest.com/connecttest.txt")]
    [InlineData("udp:8.8.8.8:53")]
    [InlineData("udp:8.8.4.4:53")]
    [InlineData("udp:77.88.8.8:53")]
    [InlineData("udp:208.67.222.222:53")]
    [InlineData("tcp:1.1.1.1:443")]
    public void WellKnownHosts_AreKept(string line)
    {
        Mask(line).Should().Be(line);
    }

    [Fact]
    public void TheLookalikeOfAKeptHost_IsStillMasked()
    {
        Mask("https://colitu.com.evil.example.org/").Should().Be("https://<site>/");
        Mask("https://notcolitu.com/").Should().Be("https://<site>/");
    }

    [Fact]
    public void NoServerList_StillWorks()
    {
        ColituSiteRedaction.Apply("tcp:shop.example.org:443", null).Should().Be("tcp:<site>:443");
        ColituSiteRedaction.Apply("", null).Should().Be("");
    }
}

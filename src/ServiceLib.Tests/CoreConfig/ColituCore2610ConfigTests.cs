namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// Xray 26.10.10 and sing-box 1.14.3 (2026-10-12): the configs must not use what they refuse or
/// deprecate. Xray refuses "allowInsecure": true (26.6.22), dropped "echForceQuery", and checks a
/// pinned certificate against a name (26.7.11); sing-box deprecates a rule set's download_detour
/// (1.14, removed in 1.16) in favour of http_client.
/// </summary>
public class ColituCore2610ConfigTests
{
    private static JsonNode TlsSettings(string link, Action<ProfileItem> edit)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = TrojanFmt.Resolve(link, out _)!;
        node.IndexId = "n-trojan";
        node.CoreType = ECoreType.Xray;
        edit(node);
        var result = new CoreConfigV2rayService(CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray)).GenerateClientConfigContent();
        if (!result.Success)
        {
            throw new InvalidOperationException($"ret msg: {result.Msg}");
        }
        var outbound = JsonNode.Parse(result.Data!.ToString()!)!["outbounds"]!.AsArray().First(o => o?["protocol"]?.GetValue<string>() == "trojan")!;
        return outbound["streamSettings"]!["tlsSettings"]!;
    }

    private const string Link = "trojan://secret@203.0.113.7:443?security=tls&sni=vpn.example.test&type=tcp#Colitu";

    [Test]
    public async Task Xray_NeverWritesAllowInsecure()
    {
        var tls = TlsSettings("trojan://secret@203.0.113.7:443?security=tls&sni=vpn.example.test&type=tcp&allowInsecure=1#Colitu", _ => { });

        await (tls["allowInsecure"] == null).Should().BeTrue();
        await tls["serverName"]!.GetValue<string>().Should().BeEqualTo("vpn.example.test");
    }

    [Test]
    public async Task Xray_NoEchForceQuery()
    {
        var tls = TlsSettings(Link, node => node.EchConfigList = "AEX+DQBB");

        await tls["echConfigList"]!.GetValue<string>().Should().BeEqualTo("AEX+DQBB");
        await (tls["echForceQuery"] == null).Should().BeTrue();
    }

    [Test]
    public async Task Xray_PinnedCertificate_AlwaysHasAName()
    {
        var tls = TlsSettings(Link, node =>
        {
            node.Sni = "";
            node.Address = "node.example.test";
            node.CertSha = "c5a0b1a2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0";
        });

        await tls["pinnedPeerCertSha256"]!.GetValue<string>().Should().StartWith("c5a0");
        await tls["serverName"]!.GetValue<string>().Should().BeEqualTo("node.example.test");
    }

    [Test]
    public async Task SingBox_RemoteRuleSet_UsesHttpClientNotDownloadDetour()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r",
                Remarks = "r",
                // A rule set that is not shipped locally: downloaded through the proxy.
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new() { Enabled = true, RuleType = ERuleType.Routing, OutboundTag = Global.DirectTag, Ip = ["geoip:zz-not-shipped"] },
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        var text = result.Data!.ToString()!;
        await text.Contains("download_detour").Should().BeFalse();
        var sets = JsonNode.Parse(text)!["route"]?["rule_set"]?.AsArray().OfType<JsonObject>()
            .Where(set => set["type"]?.GetValue<string>() == "remote").ToList() ?? [];
        await (sets.Count > 0).Should().BeTrue();
        await sets.All(set => set["http_client"] != null).Should().BeTrue();
    }
}

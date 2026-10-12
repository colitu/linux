namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// In TUN mode the app's own server ping (TCP connect to the nodes' ports, ColituLatency) must not
/// go through the tunnel: the sing-box TUN config (sing-box alone, or in front of Xray) starts with
/// a rule sending the app's TCP to those ports to the direct outbound.
/// </summary>
public class ColituPingDirectRuleTests
{
    private static SingboxConfig Generate(bool tun)
    {
        var config = CoreConfigTestFactory.CreateConfigWithTun(ECoreType.sing_box, enableIPv6Address: true);
        config.TunModeItem.EnableTun = tun;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        return JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
    }

    private static string AppPath()
    {
        var path = Utils.GetExePath();
        return Utils.IsWindows() ? path : RootCoreGuard.RealPath(path) ?? path;
    }

    [Test]
    public async Task Tun_FirstRuleSendsTheAppsPingDirect()
    {
        var cfg = Generate(tun: true);

        await cfg.outbounds.Any(o => o.tag == Global.DirectTag).Should().BeTrue();
        var first = cfg.route.rules.First();
        await first.outbound.Should().BeEqualTo(Global.DirectTag);
        await first.process_path!.SequenceEqual([AppPath()]).Should().BeTrue();
        await first.network!.SequenceEqual(["tcp"]).Should().BeTrue();
        await first.port!.SequenceEqual([443, 2053, 2083, 2087, 2096, 3443, 4433, 7443, 8388, 8443, 9443]).Should().BeTrue();
        await first.action.Should().BeNull();
    }

    [Test]
    public async Task Tun_RuleIsAllowedForTheRootCore()
    {
        var cfg = Generate(tun: true);
        var json = JsonUtils.Serialize(cfg);
        await RootConfigPolicy.Check(json, "sing-box", RootCoreGuard.InstallBin(), out var reason).Should().BeTrue().Because(reason);
    }

    [Test]
    public async Task Proxy_NoProcessRule()
    {
        var cfg = Generate(tun: false);
        await cfg.route.rules.Any(r => r.process_path != null && r.port != null && r.port.Contains(2053)).Should().BeFalse();
    }
}

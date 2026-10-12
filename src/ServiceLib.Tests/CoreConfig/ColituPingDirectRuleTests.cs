namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// In TUN mode the app's own server ping (TCP connect to the nodes' ports, ColituLatency) must not
/// go through the tunnel: the sing-box TUN config (sing-box alone, or in front of Xray) starts with
/// a rule sending the app's TCP to those ports direct. The rule names the app by its root-owned
/// path AND only the resolved probe addresses, so no other program and no other destination of the
/// app leaves the tunnel through it (2026-10-12 security test, LNX-01).
/// </summary>
[NotInParallel]
public class ColituPingDirectRuleTests
{
    private const string AppPath = "/opt/colitu-vpn/ColituVPN";
    private static readonly string[] Targets = ["203.0.113.7", "198.51.100.20"];

    private static SingboxConfig Generate(bool tun, string? appPath = AppPath, IReadOnlyList<string>? targets = null)
    {
        var config = CoreConfigTestFactory.CreateConfigWithTun(ECoreType.sing_box, enableIPv6Address: true);
        config.TunModeItem.EnableTun = tun;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);
        var beforePath = AppManager.Instance.PingRouteAppPath;
        var beforeTargets = AppManager.Instance.PingRouteTargets;
        AppManager.Instance.PingRouteAppPath = appPath;
        AppManager.Instance.PingRouteTargets = targets ?? Targets;
        try
        {
            var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
            return JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        }
        finally
        {
            AppManager.Instance.PingRouteAppPath = beforePath;
            AppManager.Instance.PingRouteTargets = beforeTargets;
        }
    }

    [Test]
    public async Task Tun_FirstRuleSendsTheAppsPingDirect()
    {
        var cfg = Generate(tun: true);

        await cfg.outbounds.Any(o => o.tag == Global.DirectTag).Should().BeTrue();
        var first = cfg.route.rules.First();
        await first.outbound.Should().BeEqualTo(Global.DirectTag);
        await first.process_path!.SequenceEqual([AppPath]).Should().BeTrue();
        await first.ip_cidr!.SequenceEqual(["203.0.113.7/32", "198.51.100.20/32"]).Should().BeTrue();
        await first.network!.SequenceEqual(["tcp"]).Should().BeTrue();
        await first.port!.SequenceEqual([443, 2053, 2083, 2087, 2096, 3443, 4433, 7443, 8388, 8443, 9443]).Should().BeTrue();
        await first.action.Should().BeNull();
        await (first.process_name == null).Should().BeTrue();
    }

    [Test]
    public async Task Tun_RuleIsAllowedForTheRootCore()
    {
        var cfg = Generate(tun: true);
        var json = JsonUtils.Serialize(cfg);
        await RootConfigPolicy.Check(json, "sing-box", RootCoreGuard.InstallBin(), out var reason).Should().BeTrue().Because(reason);
    }

    [Test]
    public async Task Tun_NoTrustedPath_NoRule()
    {
        var cfg = Generate(tun: true, appPath: null);
        await cfg.route.rules.Any(r => r.process_path != null && r.port != null && r.port.Contains(2053)).Should().BeFalse();
    }

    [Test]
    public async Task Tun_NoTarget_NoRule()
    {
        var cfg = Generate(tun: true, targets: ["not-an-address", "2001:db8::1"]);
        await cfg.route.rules.Any(r => r.process_path != null && r.port != null && r.port.Contains(2053)).Should().BeFalse();
    }

    [Test]
    public async Task Proxy_NoProcessRule()
    {
        var cfg = Generate(tun: false);
        await cfg.route.rules.Any(r => r.process_path != null && r.port != null && r.port.Contains(2053)).Should().BeFalse();
    }
}

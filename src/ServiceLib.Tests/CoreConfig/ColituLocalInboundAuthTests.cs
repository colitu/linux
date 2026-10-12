namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// TUN mode: the local SOCKS/HTTP inbound (127.0.0.1) asks for a per-run account, so other programs
/// and users on the computer cannot use the tunnel through it; the sing-box TUN front of an Xray
/// tunnel logs in with it. Proxy mode keeps it open (desktop proxy settings carry no credentials).
/// The root config policy still accepts the configs (2026-10-12 security test, LNX-08).
/// </summary>
[NotInParallel]
public class ColituLocalInboundAuthTests
{
    private static T WithLocalAuth<T>(Func<T> action)
    {
        var before = AppManager.Instance.LocalInboundAuth;
        AppManager.Instance.LocalInboundAuth = true;
        try
        {
            return action();
        }
        finally
        {
            AppManager.Instance.LocalInboundAuth = before;
        }
    }

    private static (Config Config, ProfileItem Node, CoreConfigContext Context) Tun(ECoreType core, bool tun = true)
    {
        var config = CoreConfigTestFactory.CreateConfigWithTun(core, enableIPv6Address: true);
        config.TunModeItem.EnableTun = tun;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = core == ECoreType.sing_box
            ? CoreConfigTestFactory.CreateSocksNode(core)
            : CoreConfigTestFactory.CreateVmessNode(core, "n-main", "main");
        return (config, node, CoreConfigTestFactory.CreateContext(config, node, core));
    }

    [Test]
    public async Task SingBox_Tun_MixedInboundHasTheRunAccount()
    {
        var (_, _, context) = Tun(ECoreType.sing_box);
        var json = WithLocalAuth(() => new CoreConfigSingboxService(context).GenerateClientConfigContent().Data!.ToString()!);
        var cfg = JsonUtils.Deserialize<SingboxConfig>(json)!;
        var mixed = cfg.inbounds.First(item => item.tag == "socks");
        await mixed.users!.Count.Should().BeEqualTo(1);
        await mixed.users[0].username.Should().BeEqualTo(AppManager.Instance.LocalInboundUser);
        await mixed.users[0].password.Should().BeEqualTo(AppManager.Instance.LocalInboundPass);
        await RootConfigPolicy.Check(json, "sing-box", RootCoreGuard.InstallBin(), out var reason).Should().BeTrue().Because(reason);
    }

    [Test]
    public async Task Xray_BehindTheTunFront_AsksForTheAccount_AndTheFrontLogsIn()
    {
        var (config, node, context) = Tun(ECoreType.Xray);
        // The Xray core behind the sing-box TUN front runs with TUN off in its own context.
        var json = WithLocalAuth(() => new CoreConfigV2rayService(context with { IsTunEnabled = false }).GenerateClientConfigContent().Data!.ToString()!);
        var cfg = JsonUtils.Deserialize<V2rayConfig>(json)!;
        var socks = cfg.inbounds.First(item => item.tag == "socks");
        await socks.listen.Should().BeEqualTo("127.0.0.1");
        await socks.settings.auth.Should().BeEqualTo("password");
        await socks.settings.accounts![0].user.Should().BeEqualTo(AppManager.Instance.LocalInboundUser);
        await RootConfigPolicy.Check(json, "xray", RootCoreGuard.InstallBin(), out var reason).Should().BeTrue().Because(reason);

        var front = WithLocalAuth(() => ConfigHandler.GetPreSocksItem(config, node, ECoreType.Xray))!;
        await front.Username.Should().BeEqualTo(AppManager.Instance.LocalInboundUser);
        await front.Password.Should().BeEqualTo(AppManager.Instance.LocalInboundPass);
    }

    [Test]
    public async Task ProxyMode_InboundStaysOpen()
    {
        var (_, _, context) = Tun(ECoreType.Xray, tun: false);
        var json = WithLocalAuth(() => new CoreConfigV2rayService(context).GenerateClientConfigContent().Data!.ToString()!);
        var cfg = JsonUtils.Deserialize<V2rayConfig>(json)!;
        var socks = cfg.inbounds.First(item => item.tag == "socks");
        await (socks.settings.accounts == null).Should().BeTrue();
    }
}

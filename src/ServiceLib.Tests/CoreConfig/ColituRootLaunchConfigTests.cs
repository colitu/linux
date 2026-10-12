namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// TUN outside Windows runs the core as root through colitu-core-root, which refuses log files,
/// cache files outside its working folder and rule sets outside the install folder
/// (RootConfigPolicy). The configs the app generates for such a run must pass that check even
/// with file logging and the sing-box cache file switched on.
/// </summary>
public class ColituRootLaunchConfigTests
{
    private static CoreConfigContext RootContext(ECoreType core, bool tun = true, bool windows = false)
    {
        var config = CoreConfigTestFactory.CreateConfigWithTun(core, enableIPv6Address: true);
        config.TunModeItem.EnableTun = tun;
        config.CoreBasicItem.LogEnabled = true;
        config.CoreBasicItem.EnableCacheFile4Sbox = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = core == ECoreType.sing_box
            ? CoreConfigTestFactory.CreateSocksNode(core)
            : CoreConfigTestFactory.CreateVmessNode(core, "n-main", "main");
        return CoreConfigTestFactory.CreateContext(config, node, core) with { IsWindows = windows };
    }

    [Test]
    public async Task SingBox_AsRoot_NoLogFileAndCacheInTheWorkingFolder()
    {
        var context = RootContext(ECoreType.sing_box);
        await context.RunsCoreAsRoot.Should().BeTrue();
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        var json = result.Data!.ToString()!;
        var cfg = JsonUtils.Deserialize<SingboxConfig>(json)!;

        await cfg.log.output.Should().BeNull();
        await cfg.experimental!.cache_file!.path.Should().BeEqualTo("cache.db");
        await (cfg.route.rule_set ?? []).All(r => r.type != "local" || r.path!.StartsWith(Path.Combine(RootCoreGuard.InstallBin(), "srss"), StringComparison.Ordinal)).Should().BeTrue();
        await RootConfigPolicy.Check(json, "sing-box", RootCoreGuard.InstallBin(), out var reason).Should().BeTrue().Because(reason);
    }

    [Test]
    public async Task Xray_AsRoot_NoLogFiles()
    {
        var context = RootContext(ECoreType.Xray);
        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        var json = result.Data!.ToString()!;
        var cfg = JsonUtils.Deserialize<V2rayConfig>(json)!;

        await cfg.log.access.Should().BeEqualTo("none");
        await cfg.log.error.Should().BeNull();
        await RootConfigPolicy.Check(json, "xray", RootCoreGuard.InstallBin(), out var reason).Should().BeTrue().Because(reason);
    }

    [Test]
    public async Task NotRoot_KeepsTheLogAndCacheFilesInTheDataFolder()
    {
        // Proxy mode, and TUN on Windows (no sudo there): unchanged.
        foreach (var context in new[] { RootContext(ECoreType.sing_box, tun: false), RootContext(ECoreType.sing_box, windows: true) })
        {
            await context.RunsCoreAsRoot.Should().BeFalse();
            var cfg = JsonUtils.Deserialize<SingboxConfig>(new CoreConfigSingboxService(context).GenerateClientConfigContent().Data!.ToString())!;
            await cfg.log.output.Should().BeEqualTo(Utils.GetLogPath($"sbox_{DateTime.Now:yyyy-MM-dd}.txt"));
            await cfg.experimental!.cache_file!.path.Should().BeEqualTo(Utils.GetBinPath("cache.db"));
        }
    }
}

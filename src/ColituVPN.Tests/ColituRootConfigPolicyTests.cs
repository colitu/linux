using System.ComponentModel;
using System.Diagnostics;
using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// TUN mode: root reads its config only through scripts/colitu-core-root, which copies it out of the
/// user's data folder and refuses log/cache files, file paths and rule sets outside the install
/// folder (2026-10-12 audit, Medium finding). TestData/rootconfig holds configs in the canonical
/// form the cores print (`sing-box format`, `xray run -dump`, made with sing-box 1.14.2 and
/// Xray 26.5.3); the name says the expected verdict. Both the app's RootConfigPolicy and the
/// launcher's awk rules (run through sh when the machine has one) must agree with it.
/// </summary>
public class ColituRootConfigPolicyTests
{
    private const string InstallBin = "/opt/colitu-vpn/bin";

    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "TestData", "rootconfig");
    private static string Launcher => Path.Combine(AppContext.BaseDirectory, "TestData", "colitu-core-root");

    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(DataDir, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }
        return data;
    }

    private static (string Kind, bool Accept) Expected(string name) =>
        (name.StartsWith("sb-", StringComparison.Ordinal) ? "sing-box" : "xray", name.Contains("-accept-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Samples))]
    public void AppPolicy_GivesTheSampleVerdict(string name)
    {
        var (kind, accept) = Expected(name);
        var json = File.ReadAllText(Path.Combine(DataDir, name));
        RootConfigPolicy.Check(json, kind, InstallBin, out var reason).Should().Be(accept, reason);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Launcher_GivesTheSampleVerdict(string name)
    {
        var (kind, accept) = Expected(name);
        var (exit, output) = RunLauncherPolicy(kind, Path.Combine(DataDir, name));
        (exit == 0).Should().Be(accept, output);
        if (!accept)
        {
            output.Should().NotBeNullOrWhiteSpace("a refusal says why");
        }
    }

    [Fact]
    public void Samples_CoverBothCoresAndBothVerdicts()
    {
        var names = Directory.GetFiles(DataDir, "*.json").Select(Path.GetFileName).ToList();
        names.Should().Contain(n => n.StartsWith("sb-accept-")).And.Contain(n => n.StartsWith("sb-refuse-"))
            .And.Contain(n => n.StartsWith("xr-accept-")).And.Contain(n => n.StartsWith("xr-refuse-"));
    }

    [Theory]
    [InlineData("sing-box", """{"log": {"output": "/var/log/x"}}""")]
    [InlineData("sing-box", """{"route": {"rule_set": [{"type": "local", "tag": "a", "path": "/opt/colitu-vpn/bin/./srss/a.srs"}]}}""")]
    [InlineData("sing-box", """{"route": {"rule_set": [{"type": "local", "tag": "a", "path": "/opt/colitu-vpn/binx/a.srs"}]}}""")]
    [InlineData("xray", """{"log": {"access": "access.log"}}""")]
    [InlineData("xray", """{"routing": {"rules": [{"domain": ["ext-domain:../../a.dat:t"]}]}}""")]
    [InlineData("xray", """{"inbounds": [{"listen": "@abstract"}]}""")]
    [InlineData("xray", """{"log": {"access": "none"} /* comment */}""")]
    [InlineData("mihomo", """{}""")]
    public void AppPolicy_Refuses(string kind, string json)
    {
        RootConfigPolicy.Check(json, kind, InstallBin, out var reason).Should().BeFalse();
        reason.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("sing-box", """{"log": {"level": "warn", "output": ""}, "experimental": {"cache_file": {"enabled": true, "path": "cache.db"}}}""")]
    [InlineData("sing-box", """{"outbounds": [{"type": "vless", "transport": {"type": "httpupgrade", "path": "/up"}}]}""")]
    [InlineData("sing-box", """{"route": {"rules": [{"process_path": ["/usr/bin/firefox"], "outbound": "direct"}]}}""")]
    [InlineData("xray", """{"log": {"access": "none", "error": ""}, "outbounds": [{"streamSettings": {"grpcSettings": {"serviceName": "/a/b"}}}]}""")]
    public void AppPolicy_Accepts(string kind, string json)
    {
        RootConfigPolicy.Check(json, kind, InstallBin, out var reason).Should().BeTrue(reason);
    }

    [Theory]
    [InlineData("/opt/colitu-vpn/bin/sing_box/sing-box", "sing-box")]
    [InlineData("/opt/colitu-vpn/bin/sing_box/sing-box-client", "sing-box")]
    [InlineData("/opt/colitu-vpn/bin/xray/xray", "xray")]
    [InlineData("/opt/colitu-vpn/bin/mihomo/mihomo", null)]
    public void KindOf_OnlyTheCoresTheLauncherKnows(string core, string? kind)
    {
        RootConfigPolicy.KindOf(core).Should().Be(kind);
    }

    // ── RootCoreGuard: the launcher gets the same ownership check as the cores ──

    private static Dictionary<string, (long Uid, int Mode)> Packaged() => new()
    {
        ["/"] = (0, 0b111_101_101),
        ["/opt"] = (0, 0b111_101_101),
        ["/opt/colitu-vpn"] = (0, 0b111_101_101),
        ["/opt/colitu-vpn/bin"] = (0, 0b111_101_101),
        ["/opt/colitu-vpn/bin/colitu-core-root"] = (0, 0b111_101_101),
    };

    private static RootCoreGuard.StatFunc Stat(Dictionary<string, (long Uid, int Mode)> table) =>
        path => table.TryGetValue(path, out var value) ? value : null;

    private static Func<string, string?> RealPathIn(Dictionary<string, (long Uid, int Mode)> table) =>
        path => table.ContainsKey(path) ? path : null;

    [Fact]
    public void Launcher_PackagedCopyIsUsed()
    {
        var table = Packaged();
        RootCoreGuard.ResolveLauncher(InstallBin, RealPathIn(table), Stat(table), out var reason)
            .Should().Be("/opt/colitu-vpn/bin/colitu-core-root", reason);
    }

    [Theory]
    [InlineData("/opt/colitu-vpn/bin/colitu-core-root", 1000, 0b111_101_101)]
    [InlineData("/opt/colitu-vpn/bin/colitu-core-root", 0, 0b111_111_101)]
    [InlineData("/opt/colitu-vpn/bin", 0, 0b111_101_111)]
    [InlineData("/opt/colitu-vpn", 1000, 0b111_101_101)]
    public void Launcher_RefusedWhenSomeoneElseCanChangeIt(string path, long uid, int mode)
    {
        var table = Packaged();
        table[path] = (uid, mode);
        RootCoreGuard.ResolveLauncher(InstallBin, RealPathIn(table), Stat(table), out var reason).Should().BeNull();
        reason.Should().StartWith(path);
    }

    [Fact]
    public void Launcher_MissingMeansNoRootRun()
    {
        var table = Packaged();
        table.Remove("/opt/colitu-vpn/bin/colitu-core-root");
        RootCoreGuard.ResolveLauncher(InstallBin, RealPathIn(table), Stat(table), out var reason).Should().BeNull();
        reason.Should().Contain("not found");
    }

    // ── The launcher script and the packages ──

    [Fact]
    public void LauncherScript_CopiesIntoARootOnlyFolderAndCleansTheEnvironment()
    {
        var script = File.ReadAllText(Launcher);
        script.Should().StartWith("#!/bin/sh\n");
        script.Should().Contain("umask 077");
        script.Should().Contain("WORK=/run/colitu-vpn");
        script.Should().Contain("chmod 0700");
        script.Should().Contain("iflag=nofollow");
        script.Should().Contain("MAX_BYTES=4194304");
        script.Should().Contain("\"$core\" format -c \"$copy\"");
        script.Should().Contain("\"$core\" run -dump -c \"$copy\"");
        script.Should().Contain("exec env -i PATH=\"$PATH\" HOME=/root XRAY_LOCATION_ASSET=\"$here\" XRAY_LOCATION_CERT=\"$here\" \"$core\" run -c \"$copy\"");
        script.Should().Contain("exec env -i PATH=\"$PATH\" HOME=/root \"$core\" run -c \"$copy\" --disable-color");
        script.Should().Contain(RootConfigPolicy.RefusedMarker);
        script.Should().NotContain("jq").And.NotContain("python");
    }

    [Theory]
    [InlineData("scripts/package-linux.sh")]
    [InlineData("package-debian.sh")]
    [InlineData("package-rhel.sh")]
    public void Packages_InstallTheLauncherExecutable(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), file));
        text.Should().MatchRegex(@"install (-D )?-m 0755 ""\$(ROOT|SCRIPT_DIR)/scripts/colitu-core-root""");
    }

    [Fact]
    public void TunConfigRefused_HasATranslationInEveryLanguage()
    {
        var values = v2rayN.Desktop.Services.Loc.ValuesOf("err.tunConfig");
        values.Should().HaveCount(3);
        values.Should().AllSatisfy(value => value.Should().Contain("TUN"));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "scripts", "colitu-core-root")))
        {
            directory = directory.Parent;
        }
        directory.Should().NotBeNull("the tests run inside the repository");
        return directory!.FullName;
    }

    /// <summary>Runs `sh colitu-core-root --policy`; skipped where there is no sh (plain Windows).</summary>
    private static (int Exit, string Output) RunLauncherPolicy(string kind, string file)
    {
        var startInfo = new ProcessStartInfo("sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { Launcher.Replace('\\', '/'), "--policy", kind, InstallBin, file.Replace('\\', '/') })
        {
            startInfo.ArgumentList.Add(arg);
        }
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            process = null;
        }
        if (process == null)
        {
            Assert.Skip("no sh on this machine");
        }
        using (process)
        {
            var stdout = process!.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit(30000).Should().BeTrue();
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
    }
}

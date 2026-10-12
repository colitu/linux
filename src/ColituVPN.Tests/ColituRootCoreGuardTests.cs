using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// TUN mode runs the cores as root: only the install folder's copy, and only when it and every folder
/// above it belong to root and nobody else can write them (2026-10-12 audit, High finding).
/// </summary>
public class ColituRootCoreGuardTests
{
    private const string DataBin = "/home/u/.local/share/ColituVPN/bin";
    private const string InstallBin = "/opt/colitu-vpn/bin";

    /// <summary>A packaged install: everything from / to the core is root:root 0755.</summary>
    private static Dictionary<string, (long Uid, int Mode)> Packaged() => new()
    {
        ["/"] = (0, 0b111_101_101),
        ["/opt"] = (0, 0b111_101_101),
        ["/opt/colitu-vpn"] = (0, 0b111_101_101),
        ["/opt/colitu-vpn/bin"] = (0, 0b111_101_101),
        ["/opt/colitu-vpn/bin/sing_box"] = (0, 0b111_101_101),
        ["/opt/colitu-vpn/bin/sing_box/sing-box"] = (0, 0b111_101_101),
        ["/opt/colitu-vpn/bin/xray"] = (0, 0b111_101_101),
        ["/opt/colitu-vpn/bin/xray/xray"] = (0, 0b111_101_101),
    };

    private static RootCoreGuard.StatFunc Stat(Dictionary<string, (long Uid, int Mode)> table) =>
        path => table.TryGetValue(path, out var value) ? value : null;

    private static Func<string, string?> RealPathIn(Dictionary<string, (long Uid, int Mode)> table) =>
        path => table.ContainsKey(path) ? path : null;

    [Fact]
    public void Packaged_RootRunsTheInstallCopy_NeverTheUsersCopy()
    {
        var table = Packaged();
        var resolved = RootCoreGuard.ResolveForRoot($"{DataBin}/sing_box/sing-box", DataBin, InstallBin, RealPathIn(table), Stat(table), out var reason);
        resolved.Should().Be("/opt/colitu-vpn/bin/sing_box/sing-box");
        reason.Should().BeEmpty();
    }

    [Fact]
    public void MissingInstallCopy_IsRefused_InsteadOfFallingBackToTheUsersCopy()
    {
        var table = Packaged();
        table.Remove("/opt/colitu-vpn/bin/xray/xray");
        table[$"{DataBin}/xray/xray"] = (1000, 0b111_101_101);
        RootCoreGuard.ResolveForRoot($"{DataBin}/xray/xray", DataBin, InstallBin, RealPathIn(table), Stat(table), out var reason)
            .Should().BeNull();
        reason.Should().Contain("not found");
    }

    [Theory]
    [InlineData("/opt/colitu-vpn/bin/xray/xray", 1000L, 0b111_101_101)]   // core owned by the user
    [InlineData("/opt/colitu-vpn/bin/xray/xray", 0L, 0b111_111_101)]      // group-writable core
    [InlineData("/opt/colitu-vpn/bin/xray/xray", 0L, 0b111_101_111)]      // world-writable core
    [InlineData("/opt/colitu-vpn/bin", 1000L, 0b111_101_101)]             // folder owned by the user
    [InlineData("/opt/colitu-vpn", 0L, 0b111_111_101)]                    // group-writable folder
    [InlineData("/opt", 0L, 0b1_111_111_111)]                             // sticky, world-writable (like /tmp)
    [InlineData("/", 1000L, 0b111_101_101)]
    public void AnyUnsafeLinkInTheChain_RefusesRoot(string path, long uid, int mode)
    {
        var table = Packaged();
        table[path] = (uid, mode);
        RootCoreGuard.ResolveForRoot($"{DataBin}/xray/xray", DataBin, InstallBin, RealPathIn(table), Stat(table), out var reason)
            .Should().BeNull();
        reason.Should().StartWith(path);
    }

    [Fact]
    public void UncheckablePath_RefusesRoot()
    {
        var table = Packaged();
        RootCoreGuard.IsRootOwnedChain("/opt/colitu-vpn/bin/xray/xray", path => path == "/opt" ? null : table[path], out var reason)
            .Should().BeFalse();
        reason.Should().Contain("cannot be checked");
        RootCoreGuard.IsRootOwnedChain("relative/xray", Stat(table), out _).Should().BeFalse();
        RootCoreGuard.IsRootOwnedChain("/opt/colitu-vpn/../evil/xray", Stat(table), out _).Should().BeFalse();
    }

    [Fact]
    public void Portable_UserOwnedInstall_IsRefused()
    {
        // A portable copy: data folder and install folder are the same, owned by the user.
        const string bin = "/home/u/ColituVPN/bin";
        var table = new Dictionary<string, (long Uid, int Mode)>
        {
            ["/"] = (0, 0b111_101_101),
            ["/home"] = (0, 0b111_101_101),
            ["/home/u"] = (1000, 0b111_000_000),
            ["/home/u/ColituVPN"] = (1000, 0b111_000_000),
            ["/home/u/ColituVPN/bin"] = (1000, 0b111_000_000),
            ["/home/u/ColituVPN/bin/xray"] = (1000, 0b111_000_000),
            ["/home/u/ColituVPN/bin/xray/xray"] = (1000, 0b111_000_000),
        };
        RootCoreGuard.ResolveForRoot($"{bin}/xray/xray", bin, bin, RealPathIn(table), Stat(table), out var reason).Should().BeNull();
        reason.Should().Contain("uid 1000");
    }

    [Fact]
    public void SymlinkedInstall_RunsTheResolvedRootOwnedTarget()
    {
        var table = Packaged();
        // /opt/colitu-vpn/bin/xray/xray -> /usr/lib/colitu/xray (a distribution package layout).
        table["/usr"] = (0, 0b111_101_101);
        table["/usr/lib"] = (0, 0b111_101_101);
        table["/usr/lib/colitu"] = (0, 0b111_101_101);
        table["/usr/lib/colitu/xray"] = (0, 0b111_101_101);
        string? RealPath(string path) => path == "/opt/colitu-vpn/bin/xray/xray" ? "/usr/lib/colitu/xray" : null;
        RootCoreGuard.ResolveForRoot($"{DataBin}/xray/xray", DataBin, InstallBin, RealPath, Stat(table), out _)
            .Should().Be("/usr/lib/colitu/xray");
    }

    [Theory]
    [InlineData("/home/u/.local/share/ColituVPN/bin/xray/xray", "/opt/colitu-vpn/bin/xray/xray")]
    [InlineData("/home/u/.local/share/ColituVPN/bin/sing-box", "/opt/colitu-vpn/bin/sing-box")]
    [InlineData("/somewhere/else/xray", "/somewhere/else/xray")]                                   // not in the data bin: unchanged
    [InlineData("/home/u/.local/share/ColituVPN/bin/../../evil", "/home/u/.local/share/ColituVPN/bin/../../evil")]
    [InlineData("/home/u/.local/share/ColituVPN/binx/xray", "/home/u/.local/share/ColituVPN/binx/xray")]
    public void InstallCopyOf_MapsTheDataBinToTheInstallBin(string file, string expected)
    {
        RootCoreGuard.InstallCopyOf(file, DataBin, InstallBin).Should().Be(expected);
    }

    [Theory]
    [InlineData("0 755", 0L, 0b111_101_101)]
    [InlineData("1000 700\n", 1000L, 0b111_000_000)]
    [InlineData("0 1777", 0L, 0b1_111_111_111)]
    public void ParseStat_ReadsUidAndOctalMode(string output, long uid, int mode)
    {
        RootCoreGuard.ParseStat(output).Should().Be((uid, mode));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("root 755")]
    [InlineData("0 789")]
    [InlineData("0 75555")]
    [InlineData("0")]
    public void ParseStat_RefusesAnythingElse(string? output)
    {
        RootCoreGuard.ParseStat(output).Should().BeNull();
    }

    [Fact]
    public void TunUnsafe_HasATranslationInEveryLanguage()
    {
        var values = v2rayN.Desktop.Services.Loc.ValuesOf("err.tunUnsafe");
        values.Should().HaveCount(3);
        values.Should().AllSatisfy(value => value.Should().Contain(".deb/.rpm"));
    }
}

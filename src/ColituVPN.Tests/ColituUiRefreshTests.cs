using AwesomeAssertions;
using v2rayN.Desktop.Services;
using v2rayN.Desktop.Views;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Logic behind the interface refresh (same as Windows 3.x): leak test verdicts, the speed meter, device cleanup, favourites, strings.</summary>
public class ColituUiRefreshTests
{
    /// <summary>src/ColituVPN, found from the test binaries upwards (CallerFilePath is mapped to "src/" by PathMap in CI).</summary>
    private static string AppSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "scripts", "colitu-core-root")))
        {
            directory = directory.Parent;
        }
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("repository root"), "src", "ColituVPN");
    }

    [Fact]
    public void Trace_GivesIpAndCountry()
    {
        var exit = ColituConnectionCheck.FromTrace("fl=1\nh=www.cloudflare.com\nip=203.0.113.24\nts=1.2\nloc=de\ntls=TLSv1.3\n");
        exit.Should().Be(new ColituExitInfo("203.0.113.24", "DE"));
        ColituConnectionCheck.FromTrace("ip=2001:db8::1\nloc=XX").Should().Be(new ColituExitInfo("2001:db8::1", null));
        ColituConnectionCheck.FromTrace("ip=not-an-ip\nloc=DE").Should().BeNull();
        ColituConnectionCheck.FromTrace("").Should().BeNull();
    }

    [Theory]
    [InlineData("DE", "DE", ColituLeakState.Ok)]
    [InlineData("de", "DE", ColituLeakState.Ok)]
    [InlineData("TR", "DE", ColituLeakState.Warn)]
    [InlineData("DE", null, ColituLeakState.Ok)]
    public void Ip_IsFine_InTheServersCountry(string exitCountry, string? serverCountry, ColituLeakState expected)
    {
        ColituConnectionCheck.JudgeIp(new ColituExitInfo("203.0.113.24", exitCountry.ToUpperInvariant()), serverCountry).Should().Be(expected);
        ColituConnectionCheck.JudgeIp(null, serverCountry).Should().Be(ColituLeakState.Unknown);
    }

    [Fact]
    public void Dns_IsFine_NextToTheExitOrInItsCountry()
    {
        var exit = new ColituExitInfo("203.0.113.24", "DE");
        ColituConnectionCheck.JudgeDns(exit, "203.0.200.1", null).Should().Be(ColituLeakState.Ok, "same /16 as the exit");
        ColituConnectionCheck.JudgeDns(exit, "198.51.100.7", "DE").Should().Be(ColituLeakState.Ok);
        ColituConnectionCheck.JudgeDns(exit, "198.51.100.7", "TR").Should().Be(ColituLeakState.Warn, "a resolver in another country (home ISP) sees the lookups");
        ColituConnectionCheck.JudgeDns(exit, "198.51.100.7", null).Should().Be(ColituLeakState.Unknown);
        ColituConnectionCheck.JudgeDns(exit, null, null).Should().Be(ColituLeakState.Unknown);
        ColituConnectionCheck.JudgeDns(null, "198.51.100.7", "DE").Should().Be(ColituLeakState.Unknown);
    }

    [Fact]
    public void LeakResult_NamesTheExitCountry_NeverTheExitIp()
    {
        var report = new ColituLeakReport(new ColituExitInfo("203.0.113.24", "DE"), ColituLeakState.Ok, "203.0.113.53", "DE", ColituLeakState.Ok);
        var lines = ColituMainWindow.LeakLines(report);
        lines.Should().HaveCount(2);
        lines[0].State.Should().Be(ColituLeakState.Ok);
        lines[0].Text.Should().NotContain("203.0.113.24", "the exit IP is not shown (owner 2026-11-21)").And.Contain("(DE)");
        lines[1].Text.Should().Contain("203.0.113.53", "the DNS line names the resolver");
        var warn = ColituMainWindow.LeakLines(report with { IpState = ColituLeakState.Warn, DnsState = ColituLeakState.Warn, ResolverCountry = "TR" });
        warn[0].Text.Should().Be(Loc.I.Format("conn.leak.ipWarn", ("country", ColituMainWindow.CountryLabel("DE"))));
        warn[1].State.Should().Be(ColituLeakState.Warn);
    }

    [Fact]
    public void Meter_KeepsTheLastMinute_AndRatesNeverGoNegative()
    {
        var meter = new ColituTrafficMeter();
        for (var i = 0; i < 75; i++)
        {
            meter.Add(new ColituSpeedSample(i, i * 2));
        }
        meter.Samples.Should().HaveCount(ColituTrafficMeter.Capacity);
        meter.Latest.Should().Be(new ColituSpeedSample(74, 148));
        ColituTrafficMeter.Rate(2048, 1024, 1).Should().Be(1024);
        ColituTrafficMeter.Rate(10, 5000, 1).Should().Be(0, "a counter reset is not negative traffic");
        ColituTrafficMeter.Format(0, "en", System.Globalization.CultureInfo.InvariantCulture).Should().Be("0 B/s");
        ColituTrafficMeter.Format(1536, "en", System.Globalization.CultureInfo.InvariantCulture).Should().Be("1.5 KB/s");
        ColituTrafficMeter.Format(250 * 1024 * 1024, "ru", System.Globalization.CultureInfo.InvariantCulture).Should().Be("250 МБ/с");
    }

    [Fact]
    public void Cleanup_TakesOnlyDevicesOlderThan30Days_NeverThisOne()
    {
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var devices = new (string, bool, DateTimeOffset?)[]
        {
            ("this", true, now.AddDays(-90)),
            ("old", false, now.AddDays(-31)),
            ("recent", false, now.AddDays(-29)),
            ("never", false, null),
            ("", false, now.AddDays(-100))
        };
        ColituAccountApi.StaleDeviceIds(devices, 30, now).Should().Equal("old");
    }

    [Fact]
    public void NewStrings_ExistInEveryLanguage()
    {
        foreach (var key in Loc.Keys.Where(key => key.StartsWith("conn.", StringComparison.Ordinal) || key.StartsWith("usage.", StringComparison.Ordinal)
            || key is "locations.pingAll" or "locations.pingRow" or "account.cleanup" or "account.rename" or "support.typing" or "support.read"
                or "tray.favorites" or "tray.recent" or "locations.favorites" or "locations.favoriteAdd" or "locations.favoriteRemove"))
        {
            var values = Loc.ValuesOf(key);
            values.Should().HaveCount(Loc.Languages.Length, key);
            values.Should().OnlyContain(value => value.Length > 0, key);
        }
        Loc.Has("conn.leak.dnsWarn").Should().BeTrue();
        Loc.Has("support.dropHint").Should().BeTrue();
    }

    [Fact]
    public void ServerRows_HaveAPingButton_AndTheWhitelistRowShowsItsPing()
    {
        var berlin = new ColituVpnServer { Id = "de", Name = "Berlin", CountryCode = "DE", City = "Berlin", Available = true, WhitelistOk = true, Protocols = ["vless-ws"] };
        ColituServerRow.From(berlin, 42).PingButtonVisible.Should().BeTrue();
        ColituServerRow.Auto("x").PingButtonVisible.Should().BeFalse();
        ColituServerRow.Header("h").PingButtonVisible.Should().BeFalse();
        var whitelist = ColituServerRow.WhitelistWithPing(berlin, 57);
        whitelist.PingText.Should().Be("57 ms");
        whitelist.ShowPingBars.Should().BeTrue();
        var group = ColituServerRow.Group(new ColituServerGroup { Key = "US", Name = "ABD", CountryCode = "US", Servers = [berlin, berlin] }, 96, false);
        group.GroupServers.Should().HaveCount(2, "a country's ping button measures all of its cities");
        group.PingButtonVisible.Should().BeTrue();
        group.CanFavorite.Should().BeFalse();
        ColituServerRow.From(berlin, 42).CanFavorite.Should().BeTrue();
    }

    [Fact]
    public void Favourites_AndRecent_KeepTheirOrderAndLimits()
    {
        var favorites = ColituServerPicks.ToggleFavorite(["a"], "b", out var added);
        added.Should().BeTrue();
        favorites.Should().Equal("b", "a");
        ColituServerPicks.ToggleFavorite(favorites, "A", out added).Should().Equal("b");
        added.Should().BeFalse();
        ColituServerPicks.AddRecent(["a", "b", "c"], "d").Should().Equal("d", "a", "b");
        ColituServerPicks.AddRecent(["a", "b", "c"], "c").Should().Equal("c", "a", "b");
        var preferences = new ColituVpnPreferences(FavoriteServerIds: [" x ", "x", ""], RecentServerIds: ["1", "2", "3", "4"]).Normalize();
        preferences.FavoriteServerIds.Should().Equal("x");
        preferences.RecentServerIds.Should().Equal("1", "2", "3");
    }

    [Fact]
    public void ActiveBadge_BreathesBetweenTheBrandViolets_AndTextStaysReadable()
    {
        ColituMainWindow.BadgePhase(TimeSpan.Zero).Should().BeApproximately(0, 1e-9);
        ColituMainWindow.BadgePhase(TimeSpan.FromSeconds(2.8)).Should().BeApproximately(1, 1e-9);
        ColituMainWindow.BadgePhase(TimeSpan.FromSeconds(5.6)).Should().BeApproximately(0, 1e-9);
        foreach (var t in new[] { 0.0, 0.5, 1.0 })
        {
            var brush = (Avalonia.Media.LinearGradientBrush)ColituMainWindow.BadgeFrame(t);
            foreach (var stop in brush.GradientStops)
            {
                Contrast(stop.Color, Avalonia.Media.Color.FromRgb(0x0B, 0x0A, 0x14)).Should().BeGreaterThan(5, $"dark badge text on {stop.Color} at t={t}");
            }
        }
    }

    [Fact]
    public void SupportReply_PreviewIsOneShortLine()
    {
        var text = ColituMainWindow.ReplyPreview("Merhaba!\nGünlükleri aldık. " + new string('x', 200));
        text.Should().NotContain("\n").And.EndWith("…");
        text.Length.Should().BeLessThan(140);
    }

    [Fact]
    public void Theme_HasReadableGreys_FocusRings_And40pxIconButtons()
    {
        var theme = File.ReadAllText(Path.Combine(AppSource(), "Assets", "Colitu", "ColituTheme.axaml"));
        theme.Should().Contain("x:Key=\"MutedBrush\" Color=\"#A9ADC2\"").And.Contain("x:Key=\"DimBrush\" Color=\"#9EA2AF\"").And.Contain("x:Key=\"KickerBrush\" Color=\"#A9ADC2\"");
        theme.Should().Contain(":focus-visible");
        var icon = theme[theme.IndexOf("x:Key=\"IconButton\"", StringComparison.Ordinal)..];
        icon[..400].Should().Contain("<Setter Property=\"Width\" Value=\"40\" />").And.Contain("<Setter Property=\"Height\" Value=\"40\" />");

        var xaml = File.ReadAllText(Path.Combine(AppSource(), "Views", "ColituMainWindow.axaml"));
        xaml.Should().Contain("logo-wordmark.png").And.Contain("home-bg.jpg").And.Contain("ColumnDefinitions=\"240,*,240\"");
        // Every icon-only button in the window has a name for screen readers.
        foreach (System.Text.RegularExpressions.Match button in System.Text.RegularExpressions.Regex.Matches(xaml, "<Button(?:(?!>).)*Theme=\"\\{StaticResource (?:IconButton|WindowButton|WindowCloseButton)\\}\"(?:(?!>).)*>", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            button.Value.Should().Contain("AutomationProperties.Name", button.Value);
        }
    }

    private static double Contrast(Avalonia.Media.Color a, Avalonia.Media.Color b)
    {
        static double L(Avalonia.Media.Color c)
        {
            static double Ch(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (x, y) = (L(a), L(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using v2rayN.Desktop.Views;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Avalonia headless with Skia: the real app (App.axaml styles), drawing to bitmaps.</summary>
public static class ColituPreviewAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<v2rayN.Desktop.App>()
        .UseSkia()
        .UseHarfBuzz()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Draws the signed-in window (the real ColituMainWindow.axaml, compiled) with sample data, without
/// a session, services that go out or the tray (ColituMainWindow.PreviewMode), and checks that every
/// page builds. With COLITU_RENDER_DIR set it saves one 1140×760 PNG per page: Home (connected, leak
/// test result), Locations, Plan (30-day chart), Account (devices), Live support. All addresses are
/// documentation ranges (RFC 5737), never real servers; nobody signs in.
/// <para>
/// Avalonia binds its UI dispatcher to the first thread that touches it, and the other tests of this
/// assembly create brushes on xunit's threads. So the headless app runs in a child process of this
/// test executable that runs only the Child_ tests (COLITU_PREVIEW_CHILD=1); in the normal run they are skipped.
/// </para>
/// </summary>
public class ColituUiPreviewTests
{
    private const int W = 1140;
    private const int H = 760;
    private const string ChildVariable = "COLITU_PREVIEW_CHILD";

    private static bool IsChild => Environment.GetEnvironmentVariable(ChildVariable) == "1";

    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(ColituPreviewAppBuilder), AvaloniaTestIsolationLevel.PerAssembly));

    [Fact]
    public void SignedInWindow_DrawsEveryPage_InAHeadlessChildProcess()
    {
        Assert.SkipWhen(IsChild, "this is the child process");
        var assembly = typeof(ColituUiPreviewTests).Assembly.Location;
        var host = Environment.ProcessPath!;
        var info = new System.Diagnostics.ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add("exec");
            info.ArgumentList.Add(assembly);
        }
        foreach (var arg in new[] { "--filter-method", "*ColituUiPreviewTests.Child_*", "--no-progress" })
        {
            info.ArgumentList.Add(arg);
        }
        info.Environment[ChildVariable] = "1";
        using var child = System.Diagnostics.Process.Start(info)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        child.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds).Should().BeTrue("the preview finishes in minutes");
        child.ExitCode.Should().Be(0, output.Result + errors.Result);
    }

    [Fact]
    public void Child_SignedInPages_BuildFromTheRealWindow()
    {
        Assert.SkipUnless(IsChild, "runs in the child process (SignedInWindow_DrawsEveryPage_InAHeadlessChildProcess)");
        var dir = Environment.GetEnvironmentVariable("COLITU_RENDER_DIR");
        var language = Environment.GetEnvironmentVariable("COLITU_RENDER_LANG") is { Length: > 0 } lang ? lang : "tr";
        Session.Value.Dispatch(() => RenderAll(dir, language), CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// A country with several cities opens with one click and closes with the next one. The Windows
    /// app opened and closed it again in the same click (the rebuilt list selected the header twice);
    /// the Linux list did not, and the header now toggles on the press itself, so it never can.
    /// </summary>
    [Fact]
    public void Child_CountryWithManyCities_OpensAndClosesWithOneClickEach()
    {
        Assert.SkipUnless(IsChild, "runs in the child process (SignedInWindow_DrawsEveryPage_InAHeadlessChildProcess)");
        Session.Value.Dispatch(() =>
        {
            var window = NewWindow("tr");
            try
            {
                window.ShowPreviewPage("locations");
                var cities = new[] { "New York", "Ashburn", "Dallas", "Los Angeles", "Miami", "Seattle" }
                    .Select((city, i) => new ColituVpnServer { Id = $"us{i}", Name = "ABD", CountryCode = "US", Country = "US", City = city, Available = true, Online = true, Protocols = ["vless-reality"] })
                    .ToList();
                cities.Add(new ColituVpnServer { Id = "nl", Name = "Hollanda", CountryCode = "NL", Country = "NL", City = "Amsterdam", Available = true, Online = true, Protocols = ["trojan"] });
                window.PreviewSetServers(cities);
                Settle(window);

                ColituServerRow Header() => window.ServerList.Items.OfType<ColituServerRow>().Single(row => row.IsGroup);
                int CityRows() => window.ServerList.Items.OfType<ColituServerRow>().Count(row => row.IsCity);

                Header().Expanded.Should().BeFalse();
                CityRows().Should().Be(0);
                Click(window, Header());
                Header().Expanded.Should().BeTrue("one click opens the country");
                CityRows().Should().Be(6);
                window.ServerList.SelectedItem.Should().NotBe(Header(), "the header is never selected by a click");
                Click(window, Header());
                Header().Expanded.Should().BeFalse("the next click closes it");
                CityRows().Should().Be(0);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Click(ColituMainWindow window, ColituServerRow row)
    {
        var container = window.ServerList.ContainerFromItem(row) as Control;
        container.Should().NotBeNull();
        // The left part of the row (flag and name), away from the ping button and the star.
        var point = container!.TranslatePoint(new Point(60, container.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        Settle(window);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static ColituMainWindow NewWindow(string language)
    {
        ColituMainWindow.PreviewMode = true;
        ColituReducedMotion.Override = true;
        var window = new ColituMainWindow { Width = W, Height = H };
        Loc.I.SetLanguage(language);
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(ColituMainWindow window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        window.UpdateLayout();
    }

    private static void RenderAll(string? dir, string language)
    {
        var window = NewWindow(language);
        try
        {
            foreach (var page in new[] { "home", "locations", "plan", "account", "support" })
            {
                window.ShowPreviewPage(page);
                Fill(window, page);
                Settle(window);
                // Posted status refreshes (language change, timers) may have run after Fill: connected look again.
                window.StatusChipText.Text = Loc.I["status.protected"];
                window.StatusDot.Fill = Res(window, "SuccessBrush");
                // FitNav runs after layout (HeaderBar.SizeChanged); one more pass for the thumb behind the active tab.
                Settle(window);

                window.BrandWordmark.IsVisible.Should().BeTrue("the brand wordmark fits at 1140 px");
                window.BrandWordmark.Bounds.Height.Should().Be(52);
                window.BrandWordmark.Bounds.Width.Should().BeGreaterThan(120, "Assets/Colitu/logo-wordmark.png is packed and drawn");
                var pill = window.NavList.Parent!.Parent as Control;
                var pillCentre = pill!.TranslatePoint(new Point(pill.Bounds.Width / 2, 0), window)!.Value.X;
                var left = window.HeaderBar.ColumnDefinitions[0].Width;
                left.IsAbsolute.Should().BeTrue("at 1140 px the six tabs fit between equal side columns");
                window.HeaderBar.ColumnDefinitions[2].Width.Should().Be(left);
                if (language != "ru")
                {
                    left.Value.Should().Be(240, "the design's 240 px side columns");
                }
                pillCentre.Should().BeApproximately(W / 2.0, 1.5, "equal side columns centre the tabs in the window");
                window.HomeBackdrop.IsVisible.Should().Be(page == "home");

                if (page == "support")
                {
                    window.SupportScroll.ScrollToEnd();
                    Settle(window);
                }
                if (dir == null)
                {
                    continue;
                }
                Directory.CreateDirectory(dir);
                var frame = window.CaptureRenderedFrame();
                frame.Should().NotBeNull();
                frame!.Save(Path.Combine(dir, $"linux-{page}-{language}.png"));
            }
        }
        finally
        {
            window.Close();
            Loc.I.SetLanguage("tr");
        }
    }

    private static IBrush Res(ColituMainWindow window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out var value) && value is IBrush brush ? brush : throw new KeyNotFoundException(key);

    private static void Fill(ColituMainWindow window, string page)
    {
        var loc = Loc.I;
        window.NavSupport.IsVisible = true;
        window.NavSupportDot.IsVisible = true;
        window.NavSupportCount.Text = "2";
        window.StatusChipText.Text = loc["status.protected"];
        window.StatusDot.Fill = Res(window, "SuccessBrush");

        var frankfurt = new ColituVpnServer { Id = "de1", Name = "Almanya", CountryCode = "DE", Country = "DE", City = "Frankfurt", Available = true, Online = true, Protocols = ["hysteria2", "vless-reality"] };
        switch (page)
        {
            case "home":
                FillHome(window, frankfurt);
                break;
            case "locations":
                FillLocations(window, frankfurt);
                break;
            case "plan":
                FillPlan(window);
                break;
            case "account":
                FillAccount(window);
                break;
            case "support":
                FillSupport(window);
                break;
        }
    }

    private static void FillHome(ColituMainWindow window, ColituVpnServer server)
    {
        var loc = Loc.I;
        window.ConnectLabel.Text = loc["home.tapOff"];
        window.HomeTitle.Text = loc["home.title.on"];
        window.HomeSubtitle.Text = loc.Format("home.sub.on", ("server", "Almanya · Frankfurt"));
        window.PowerIcon.IsVisible = false;
        window.SessionTimer.IsVisible = true;
        window.SessionTimer.Text = "00:12:47";
        var row = ColituServerRow.From(server, 38);
        window.HomeFlag.Content = row;
        window.HomeServerTitle.Text = row.Title;
        window.HomeServerSubtitle.Text = row.Subtitle;

        window.ConnInfoCard.IsVisible = true;
        window.ConnCountryText.Text = ColituMainWindow.CountryLabel("DE");
        window.ConnProtocolText.Text = ColituTransportNames.Of("hysteria2", loc);
        window.ConnPingText.Text = "38 ms";
        var samples = Enumerable.Range(0, 60).Select(i => new ColituSpeedSample(
            40_000 + 25_000 * Math.Sin(i / 4.0) + (i % 7) * 3000,
            900_000 + 650_000 * Math.Sin(i / 6.0 + 1) + (i % 5) * 60_000)).ToList();
        window.ConnGraph.Push(samples);
        window.ConnDownText.Text = loc.Format("conn.down", ("v", ColituTrafficMeter.Format(samples[^1].Down, loc.Language)));
        window.ConnUpText.Text = loc.Format("conn.up", ("v", ColituTrafficMeter.Format(samples[^1].Up, loc.Language)));
        window.ConnGraphNote.Text = loc["conn.speedTunnel"];
        window.LeakResults.IsVisible = true;
        window.LeakResults.Children.Clear();
        var report = new ColituLeakReport(new ColituExitInfo("203.0.113.24", "DE"), ColituLeakState.Ok, "203.0.113.53", "DE", ColituLeakState.Ok);
        foreach (var (state, text) in ColituMainWindow.LeakLines(report))
        {
            text.Should().NotContain("203.0.113.24");
            window.LeakResults.Children.Add(ColituMainWindow.LeakLineCore(state, text, key => Res(window, key)));
        }

        window.PlanName.Text = "Premium";
        window.PlanDetail.Text = "5 cihaz · sınırsız trafik";
        window.PlanBadgeText.Text = loc["plan.status.active"];
        window.PlanBadgeText.Foreground = Res(window, "OnAccentBrush");
        window.PlanBadge.Background = ColituMainWindow.BadgeFrame(0.5);
        window.TrafficBlock.IsVisible = false;
        window.DevicesText.Text = "2 / 5";
        window.PassportHomeCard.IsVisible = false;
    }

    private static void FillLocations(ColituMainWindow window, ColituVpnServer frankfurt)
    {
        var loc = Loc.I;
        // The real list first (category and sort controls), then the sample rows.
        window.PreviewSetServers([]);
        window.LocationsSubtitle.Text = loc.Format("locations.sub", ("n", 37));
        window.CategoryHost.IsVisible = false;
        var whitelist = new ColituVpnServer { Id = "fi-wl", Name = "Finlandiya", CountryCode = "FI", Country = "FI", City = "Helsinki", Available = true, WhitelistOk = true, Protocols = ["vless-ws"] };
        var cities = new[] { "New York", "Ashburn", "Dallas", "Los Angeles" }
            .Select((city, i) => new ColituVpnServer { Id = $"us{i}", Name = "ABD", CountryCode = "US", Country = "US", City = city, Available = true, Online = true, Protocols = ["vless-reality"] })
            .ToList();
        var favourite = ColituServerRow.From(frankfurt, 38);
        favourite.MarkState(true, true);
        favourite.IsFavorite = true;
        var auto = ColituServerRow.Auto("Almanya · Frankfurt");
        auto.MarkState(false, false);
        var rows = new List<ColituServerRow>
        {
            ColituServerRow.WhitelistWithPing(whitelist, 57),
            auto,
            ColituServerRow.Header(loc["locations.favorites"]),
            favourite,
            ColituServerRow.Header(loc["locations.all"]),
            ColituServerRow.From(new ColituVpnServer { Id = "nl", Name = "Hollanda", CountryCode = "NL", Country = "NL", City = "Amsterdam", Available = true, Online = true, Protocols = ["trojan"] }, 44),
            ColituServerRow.Group(new ColituServerGroup { Key = "US", Name = "ABD", CountryCode = "US", Servers = cities }, 96, true),
        };
        rows.AddRange(cities.Select((city, i) => ColituServerRow.From(city, new[] { 96, 104, 131, 162 }[i], city: true)));
        rows.Add(ColituServerRow.From(new ColituVpnServer { Id = "se", Name = "İsveç", CountryCode = "SE", Country = "SE", City = "Stockholm", Available = true, Online = true, Protocols = ["hysteria2"] }, 61));
        window.ServerList.ItemsSource = rows;
        window.ServerListEmpty.IsVisible = false;
    }

    private static void FillPlan(ColituMainWindow window)
    {
        var loc = Loc.I;
        window.CurrentPlanText.Text = "Premium · 5 cihaz";
        window.CurrentPlanBadgeText.Text = loc["plan.status.active"];
        window.CurrentPlanBadgeText.Foreground = Res(window, "OnAccentBrush");
        window.CurrentPlanBadge.Background = ColituMainWindow.BadgeFrame(0.5);
        var start = new DateTime(2026, 10, 23);
        var days = Enumerable.Range(0, 30).Select(i => new ColituDailyUsage
        {
            Date = start.AddDays(i).ToString("yyyy-MM-dd"),
            DownloadBytes = i is 4 or 11 ? 0 : (long)((1.2 + Math.Abs(Math.Sin(i * 0.7)) * 3.4 + (i % 6 == 5 ? 4 : 0)) * 1024 * 1024 * 1024),
            UploadBytes = i is 4 or 11 ? 0 : (long)((0.08 + Math.Abs(Math.Cos(i)) * 0.2) * 1024 * 1024 * 1024)
        }).ToList();
        window.ShowUsageHistory(days);
    }

    private static void FillAccount(ColituMainWindow window)
    {
        var loc = Loc.I;
        window.AvatarText.Text = "Ö";
        window.AccountEmail.Text = "ornek@example.com";
        window.AccountId.Text = "ID · 7f3a…c21e";
        window.AccountPlan.Text = "Premium";
        window.AccountPlanBadgeText.Text = loc["plan.status.active"];
        window.AccountPlanBadgeText.Foreground = Res(window, "OnAccentBrush");
        window.AccountPlanBadge.Background = ColituMainWindow.BadgeFrame(0.5);
        window.AccountUntil.Text = "21 Aralık 2026";
        window.DevicesTitle.Text = loc.Format("account.devicesTitle", ("used", 4), ("limit", 5));
        var now = DateTimeOffset.UtcNow;
        var rows = new List<ColituDeviceRow>
        {
            new() { Id = "1", Name = "Ofis bilgisayarı", IsCurrent = true, Detail = "Linux · bugün", LastSeen = now },
            new() { Id = "2", Name = "Pixel 8", Detail = "Android · 2 gün önce", LastSeen = now.AddDays(-2) },
            new() { Id = "3", Name = "Eski dizüstü", Detail = "Windows · 12 Eylül 2026", LastSeen = now.AddDays(-70) },
            new() { Id = "4", Name = "iPad", Detail = "iOS · 3 Ağustos 2026", LastSeen = now.AddDays(-110) },
        };
        window.DeviceList.ItemsSource = rows;
        window.ApplyCleanupButton(rows);
        window.CleanupDevicesButton.IsVisible.Should().BeTrue("two devices were not seen for more than 30 days");
        window.CleanupDevicesText.Text.Should().Be(loc.Format("account.cleanupCount", ("n", 2)));
    }

    private static void FillSupport(ColituMainWindow window)
    {
        window.SupportList.ItemsSource = new[]
        {
            ColituSupportRow.From(new ColituSupportConversation { Id = "a", Subject = "Bağlantı sabah kopuyor", LastMessage = "Günlükleri aldık, Frankfurt…", LastMessageAt = DateTimeOffset.Now.AddMinutes(-5), Unread = 2, Status = "open" }),
            ColituSupportRow.From(new ColituSupportConversation { Id = "b", Subject = "Fatura sorusu", LastMessage = "Teşekkürler, çözüldü.", LastMessageAt = DateTimeOffset.Now.AddDays(-1), Status = "closed" }),
        };
        window.SupportListEmpty.IsVisible = false;
        window.SupportListSpinner.IsVisible = false;
        foreach (var pane in new Control[] { window.SupportPlaceholder, window.SupportNewForm, window.SupportChat })
        {
            pane.IsVisible = false;
        }
        window.SupportThread.IsVisible = true;
        window.SupportThreadTitle.Text = "Bağlantı sabah kopuyor";
        var (status, background, foreground) = ColituSupportRow.StatusLook("open");
        window.SupportThreadStatusText.Text = status;
        window.SupportThreadStatusText.Foreground = foreground;
        window.SupportThreadStatus.Background = background;
        window.SupportComposer.IsVisible = true;
        window.SupportClosedNote.IsVisible = false;
        var today = new DateTimeOffset(2026, 11, 21, 13, 52, 0, TimeSpan.FromHours(3));
        var messages = new[]
        {
            new ColituSupportMessage { Id = "1", Sender = "user", Body = "Merhaba, her sabah 9 civarında bağlantı kopuyor, tekrar bağlanınca düzeliyor.", CreatedAt = today, ReadAt = today.AddMinutes(3) },
            new ColituSupportMessage { Id = "2", Sender = "admin", AdminName = "Deniz", Body = "Merhaba! Günlükleri aldık. Frankfurt sunucusunda sabah bakım penceresi var; bu saatlerde Amsterdam'ı deneyebilir misiniz?", CreatedAt = today.AddMinutes(9) },
            new ColituSupportMessage { Id = "3", Sender = "user", Body = "Tamam, yarın sabah deneyip yazarım. Ekran görüntüsünü de ekledim.", CreatedAt = today.AddMinutes(14) },
        };
        window.SupportMessages.Children.Clear();
        window.SupportMessages.Children.Add(new TextBlock { Text = today.ToString("D", Loc.I.Culture), Margin = new Thickness(0, 10, 0, 12), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, FontSize = 11.5, Foreground = Res(window, "DimBrush") });
        foreach (var message in messages)
        {
            window.SupportMessages.Children.Add(ColituMainWindow.BuildBubbleCore(message, message.CreatedAt, key => Res(window, key), null));
        }
        window.SupportTypingText.IsVisible = true;
    }
}

using Avalonia.Automation;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Colitu Passport page and its home card (the Windows app's ColituMainWindow.Passport.cs). The
/// passport lives on the server (docs/passport-server.md): the app refreshes it when the window
/// becomes active, when the page opens (then every minute while it is open and connected) and after
/// a connect, plays the reveals for the server's unseen events and acks them. No minutes are counted here.
/// </summary>
public partial class ColituMainWindow
{
    private readonly ColituPassport _passport = ColituPassport.Instance;
    private ColituPassportSync? _passportSync;
    private DispatcherTimer? _passportRefreshTimer;
    private int _passportChapter = 1;
    private DateTime _passportRefreshedAt = DateTime.MinValue;

    /// <summary>Fixed stamp size of the grid (as many a row as fit).</summary>
    private const double PassportStampSize = 116;

    private bool PassportPremium => _auth.CurrentSubscription?.Active == true;

    private ColituPassportSync PassportSync => _passportSync ??=
        new ColituPassportSync(_passport, new ColituAuthPassportTransport(_auth), () => _auth.RegisteredDeviceId);

    private void InitPassport()
    {
        BuildPassportHeader();
        PassportHomeCard.Click += (_, _) => Navigate("passport");
        _passportRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _passportRefreshTimer.Tick += (_, _) =>
        {
            PollPassportRewards();
            if (_page == "passport" && IsVisible && _vpn.Status == ColituVpnStatus.Connected)
            {
                _ = RefreshPassportAsync();
            }
        };
        _passportRefreshTimer.Start();
        _vpn.StatusChanged += status => Dispatcher.UIThread.Post(() =>
        {
            if (status == ColituVpnStatus.Connected && _vpn.ConnectedServer is { } server)
            {
                _passport.RememberCity(server.IsMultihop ? server.Exit?.Country ?? server.CountryCode : server.CountryCode, server.IsMultihop ? server.Exit?.City : server.City);
                _ = RefreshPassportAsync(force: true);
            }
            ApplyPassportHome();
        });
        Activated += (_, _) => _ = RefreshPassportAsync();
        // Signed in (app start, sign-in, back from verification): import once and fetch the passport.
        AppView.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && AppView.IsVisible) _ = RefreshPassportAsync(force: true);
        };
        Loc.I.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            BuildPassportHeader();
            ApplyPassportHome();
            if (_page == "passport") RenderPassport();
        });
        PassportPage.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5 && _page == "passport") LayoutPassport();
        };
        ApplyPassportHome();
    }

    /// <summary>
    /// Fetches the account's passport (and does the one-time import of this device's v1 progress, if
    /// any); new events go to the reveal queue. Offline, or before the server offers it, the cached state stays.
    /// </summary>
    private async Task RefreshPassportAsync(bool force = false)
    {
        if (!_auth.HasSession || !AppView.IsVisible)
        {
            return;
        }
        if (!force && DateTime.UtcNow - _passportRefreshedAt < TimeSpan.FromSeconds(15))
        {
            return;
        }
        _passportRefreshedAt = DateTime.UtcNow;
        try
        {
            var events = await PassportSync.RefreshAsync();
            if (events == null)
            {
                return;
            }
            QueuePassportEvents(events);
            ApplyPassportHome();
            if (_page == "passport" && !PassportSheetLayer.IsVisible && !_revealShowing)
            {
                RenderPassport();
            }
            PollPassportRewards();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.RefreshPassportAsync", ex);
        }
    }

    /// <summary>Sign-out: drop the account's cached passport and any rewards still waiting to be played.</summary>
    private void ForgetPassport()
    {
        _passport.ForgetAccount();
        _revealQueue.Clear();
        _revealIds.Clear();
        _revealQueuedThrough = 0;
        _passportRefreshedAt = DateTime.MinValue;
        ApplyPassportHome();
    }

    // ── Home card ──────────────────────────────────────────────────────────

    private void ApplyPassportHome()
    {
        PassportHomeCover.Source = PassportUi.Image($"passport_cover_{_passport.Goals.ActiveCover(PassportPremium).Key()}.png");
        PassportHomeGoal.Text = _passport.Count == 0 ? Loc.I["passport.goal.first"] : NextPassportGoal().Text;
        PassportHomeCount.Text = $"{_passport.Count}/{ColituPassport.Stamps.Count}";
        AutomationProperties.SetName(PassportHomeCard, $"{Loc.I["passport.title"]}: {PassportHomeCount.Text}. {PassportHomeGoal.Text}");
    }

    // ── Next goal ──────────────────────────────────────────────────────────

    private enum PassportGoalKind
    {
        Today,
        Level,
        Done
    }

    private sealed record PassportGoal(string Text, double Fraction, string Icon, PassportGoalKind Kind, string? Code = null, string? Target = null, int Left = 0);

    /// <summary>The nearest reward: today's next piece while one is open, else the stamp closest to its next level.</summary>
    private PassportGoal NextPassportGoal()
    {
        var loc = Loc.I;
        var (minutes, pieces) = _passport.Today;
        if (!_passport.Tester && pieces < ColituPassport.PieceMinutes.Length)
        {
            var next = ColituPassport.PieceMinutes[pieces];
            if (minutes < next)
            {
                return new PassportGoal(loc.Format("passport.goal.today", ("n", next - minutes)), minutes / (double)next, "star", PassportGoalKind.Today, Left: next - minutes);
            }
        }
        var best = _passport.Progress.Where(item => !item.Value.Mastered).OrderBy(item => item.Value.ToNext).FirstOrDefault();
        if (best.Value?.Level is not { } level)
        {
            return new PassportGoal(loc["passport.goal.done"], 1, "check", PassportGoalKind.Done);
        }
        var target = PassportUi.LevelName(level == StampLevel.Diamond ? level : level + 1);
        return new PassportGoal(
            loc.Format("passport.goal.level", ("country", PassportUi.Country(best.Key)), ("level", target), ("n", best.Value.ToNext)),
            best.Value.LevelPieces / (double)StampProgress.PiecesPerLevel,
            "arrowUp",
            PassportGoalKind.Level,
            best.Key,
            target,
            best.Value.ToNext);
    }

    // ── Page ───────────────────────────────────────────────────────────────

    private Button? _passportBackButton;

    /// <summary>Back, the title, compare (QR) and the rules — fixed above the scrolling content.</summary>
    private void BuildPassportHeader()
    {
        var loc = Loc.I;
        PassportHeader.Children.Clear();
        _passportBackButton = PassportUi.Round(PassportUi.Icon("back", PassportUi.Muted, 12), () => Navigate("home"), loc["passport.back"]);
        PassportHeader.Children.Add(_passportBackButton);
        var title = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "COLITU PASSPORT", Classes = { "kicker" } });
        title.Children.Add(new TextBlock { Text = loc["passport.title"], FontSize = 22, Margin = new Thickness(0, 2, 0, 0), Classes = { "h2" } });
        Grid.SetColumn(title, 1);
        PassportHeader.Children.Add(title);
        var duel = PassportUi.Round(PassportUi.Icon("qr", PassportUi.Muted, 16), OpenDuelSheet, loc["passport.duel.title"]);
        duel.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(duel, 2);
        PassportHeader.Children.Add(duel);
        var how = PassportUi.Round(PassportUi.Icon("info", PassportUi.Muted, 18), OpenHowSheet, loc["passport.how"]);
        Grid.SetColumn(how, 3);
        PassportHeader.Children.Add(how);
    }

    /// <summary>Opening the page: build it from the cached state, show the first-visit intro and refresh it from the server.</summary>
    private void OnPassportOpened()
    {
        RenderPassport();
        PassportScroll.Offset = default;
        if (!_passport.Social.IntroSeen)
        {
            ShowPassportIntro();
        }
        _ = RefreshPassportAsync(force: true);
    }

    private void RenderPassport()
    {
        try
        {
            BuildPassportLeft();
            BuildPassportRight();
            LayoutPassport();
            ApplyPassportHome();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.RenderPassport", ex);
        }
    }

    /// <summary>Two columns on a wide window; one under the other when it is narrow. Stamps wrap by themselves.</summary>
    private void LayoutPassport()
    {
        var width = PassportPage.Bounds.Width - 88;
        var wide = width <= 0 || width >= 720;
        PassportColumns.ColumnDefinitions[0].Width = wide ? new GridLength(300) : new GridLength(1, GridUnitType.Star);
        PassportColumns.ColumnDefinitions[1].Width = new GridLength(wide ? 40 : 0);
        Grid.SetRow(PassportRight, wide ? 0 : 1);
        Grid.SetColumn(PassportRight, wide ? 2 : 0);
        Grid.SetColumnSpan(PassportRight, wide ? 1 : 3);
        PassportRight.Margin = new Thickness(0, wide ? 0 : 28, 0, 0);
        PassportLeft.MaxWidth = wide ? double.PositiveInfinity : 420;
    }

    private void BuildPassportLeft()
    {
        var loc = Loc.I;
        PassportLeft.Children.Clear();
        var count = _passport.Count;
        var total = ColituPassport.Stamps.Count;

        // Cover with a soft glow; a click edits the passport.
        var coverHeight = Bounds.Height is > 0 and < 760 ? 132 : 156;
        var hero = new Panel { Width = coverHeight * 1.25, Height = coverHeight * 1.1, Margin = new Thickness(0, 4, 0, 0) };
        hero.Children.Add(new Ellipse
        {
            Width = coverHeight * 1.25,
            Height = coverHeight * 1.25,
            IsHitTestVisible = false,
            Margin = new Thickness(0, -coverHeight * 0.13),
            Fill = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(0x55, 0x9F, 0x8C, 0xFF), 0), new GradientStop(Color.FromArgb(0, 0x9F, 0x8C, 0xFF), 1) } }
        });
        var cover = PassportUi.Cover(_passport.Goals.ActiveCover(PassportPremium), coverHeight);
        cover.HorizontalAlignment = HorizontalAlignment.Center;
        cover.VerticalAlignment = VerticalAlignment.Center;
        hero.Children.Add(cover);
        var heroButton = PassportUi.Plain(hero, OpenIdentitySheet, loc["passport.id.edit"]);
        heroButton.HorizontalAlignment = HorizontalAlignment.Center;
        PassportLeft.Children.Add(heroButton);

        var numbers = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 34, FontWeight = FontWeight.Bold };
        numbers.Inlines = [new Run(count.ToString(loc.Culture)) { Foreground = PassportUi.Lilac }, new Run($" / {total}") { Foreground = PassportUi.Muted }];
        AutomationProperties.SetName(numbers, $"{count} / {total} {loc["passport.countries"]}");
        PassportLeft.Children.Add(numbers);
        PassportLeft.Children.Add(PassportUi.Label(loc["passport.countries"], 12.5, PassportUi.Muted, align: TextAlignment.Center));
        var bar = PassportUi.Bar(count / (double)total, 4, PassportUi.Accent, 160);
        bar.Margin = new Thickness(0, 10, 0, 0);
        bar.HorizontalAlignment = HorizontalAlignment.Center;
        PassportLeft.Children.Add(bar);
        var identity = PassportUi.Label(PassportIdentityLine(), 12.5, PassportUi.Lilac, align: TextAlignment.Center, wrap: false);
        identity.Margin = new Thickness(8, 8, 8, 18);
        PassportLeft.Children.Add(identity);

        PassportLeft.Children.Add(count == 0 ? PassportFirstSteps() : PassportNextCard());

        var week = _passport.Weekly.Goals();
        var tiles = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(-4, 10, -4, 0) };
        tiles.Children.Add(PassportTile("send", $"{week.Count(_passport.Weekly.Done)}/{week.Count}", loc["passport.tile.week"], OpenWeeklySheet));
        tiles.Children.Add(PassportTile("grid", $"{PassportCollection.All.Count(_passport.Goals.Completed)}/{PassportCollection.All.Count}", loc["passport.tile.sets"], OpenSetsSheet));
        tiles.Children.Add(PassportTile("sealCheck", $"{PassportAchievement.All.Count(_passport.Goals.Achieved)}/{PassportAchievement.All.Count}", loc["passport.tile.badges"], OpenBadgesSheet));
        PassportLeft.Children.Add(tiles);

        var note = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        note.Children.Add(PassportUi.Icon("shield", PassportUi.Dim, 12));
        var noteText = PassportUi.Label(loc["passport.local"], 11.5, PassportUi.Dim);
        noteText.MaxWidth = 270;
        noteText.Margin = new Thickness(6, 0, 0, 0);
        note.Children.Add(noteText);
        PassportLeft.Children.Add(note);
    }

    /// <summary>"Night Explorer · Collector #048213", the nickname first when set.</summary>
    private string PassportIdentityLine()
    {
        var goals = _passport.Goals;
        return string.Join(" · ", new[]
        {
            goals.Nickname.Length > 0 ? goals.Nickname : null,
            goals.TitleOf(goals.Title),
            Loc.I.Format("passport.id.collector", ("n", goals.CollectorNo))
        }.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary>A small tile: icon and value on one line, a short label under it.</summary>
    private Button PassportTile(string icon, string value, string label, Action onClick)
    {
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        top.Children.Add(PassportUi.Icon(icon, PassportUi.Lilac, 14));
        var number = PassportUi.Label(value, 14, PassportUi.Text, FontWeight.SemiBold, wrap: false);
        number.Margin = new Thickness(6, 0, 0, 0);
        top.Children.Add(number);
        column.Children.Add(top);
        var caption = PassportUi.Label(label, 11, PassportUi.Muted, align: TextAlignment.Center, wrap: false);
        caption.Margin = new Thickness(0, 2, 0, 0);
        column.Children.Add(caption);
        var button = PassportUi.Plain(column, onClick, $"{label}: {value}");
        button.Background = PassportUi.Tile;
        button.BorderBrush = PassportUi.Line;
        button.BorderThickness = new Thickness(1);
        button.Padding = new Thickness(4, 8);
        button.Margin = new Thickness(4, 0);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    /// <summary>First visit: the whole game in three steps, and a way to start.</summary>
    private Control PassportFirstSteps()
    {
        var loc = Loc.I;
        var body = new StackPanel();
        var steps = new UniformGrid { Rows = 1, Columns = 3 };
        foreach (var (icon, top, bottom) in new[] { ("power", "passport.step1", "passport.step1b"), ("star", "passport.step2", "passport.step2b"), ("arrowUp", "passport.step3", "passport.step3b") })
        {
            var step = new StackPanel { Margin = new Thickness(2, 0) };
            step.Children.Add(PassportUi.IconWell(icon, 28));
            var a = PassportUi.Label(loc[top], 11, PassportUi.Text, align: TextAlignment.Center);
            a.Margin = new Thickness(0, 5, 0, 0);
            step.Children.Add(a);
            step.Children.Add(PassportUi.Label(loc[bottom], 11, PassportUi.Lilac, align: TextAlignment.Center));
            steps.Children.Add(step);
        }
        body.Children.Add(steps);
        if (_vpn.Status is ColituVpnStatus.Disconnected or ColituVpnStatus.Error)
        {
            var connect = PassportUi.Primary(loc["home.connect"], () => _ = PassportConnectAsync(null), 40);
            connect.Margin = new Thickness(0, 12, 0, 0);
            connect.HorizontalAlignment = HorizontalAlignment.Center;
            connect.MinWidth = 200;
            body.Children.Add(connect);
        }
        return PassportUi.Glass(body, padding: new Thickness(10, 12));
    }

    private Control PassportNextCard()
    {
        var goal = NextPassportGoal();
        var row = PassportUi.Columns("Auto,*,Auto");
        row.Children.Add(PassportUi.Ring(goal.Fraction, 42, 3.5, PassportUi.Icon(goal.Icon, PassportUi.Lilac, 15)));
        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(PassportUi.Kicker(Loc.I["passport.next"]));
        var label = PassportUi.Label(goal.Text, 13.5, PassportUi.Text, FontWeight.SemiBold);
        label.Margin = new Thickness(0, 3, 0, 0);
        text.Children.Add(label);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var chevron = PassportUi.Icon("chevron", PassportUi.Muted, 12);
        Grid.SetColumn(chevron, 2);
        row.Children.Add(chevron);
        return PassportUi.Glass(row, OpenNextSheet, $"{Loc.I["passport.nx.title"]}: {goal.Text}", new Thickness(12));
    }

    private void BuildPassportRight()
    {
        var loc = Loc.I;
        PassportRight.Children.Clear();

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        foreach (var n in new[] { 1, 2, 3 })
        {
            var open = n switch { 1 => true, 2 => _passport.Chapter2Open, _ => _passport.Goals.Chapter3Open };
            var content = new StackPanel();
            content.Children.Add(PassportUi.Label($"0{n}", 11.5, n == _passportChapter ? PassportUi.Lilac : PassportUi.Dim, FontWeight.SemiBold, TextAlignment.Center));
            var name = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            if (!open)
            {
                var lockIcon = PassportUi.Icon("lock", PassportUi.Dim, 12);
                lockIcon.Margin = new Thickness(0, 0, 4, 0);
                name.Children.Add(lockIcon);
            }
            name.Children.Add(new TextBlock { Text = loc[$"passport.chName{n}"], FontSize = 15, FontWeight = FontWeight.SemiBold });
            content.Children.Add(name);
            var tab = PassportUi.ChapterTab(content, n == _passportChapter, loc[$"passport.ch{n}"] + (open ? "" : $" · {loc["passport.chapterLocked"]}"));
            var chapter = n;
            tab.IsCheckedChanged += (_, _) =>
            {
                if (tab.IsChecked != true || _passportChapter == chapter) return;
                _passportChapter = chapter;
                Dispatcher.UIThread.Post(() =>
                {
                    BuildPassportRight();
                    LayoutPassport();
                });
            };
            tabs.Children.Add(tab);
        }
        PassportRight.Children.Add(tabs);

        if (_passportChapter == 3)
        {
            PassportRight.Children.Add(PassportLegacy());
        }
        else
        {
            var list = _passportChapter == 2 ? ColituPassport.Chapter2 : ColituPassport.Stamps;
            if (_passportChapter == 2 && !_passport.Chapter2Open)
            {
                var locked = PassportUi.Label(loc["passport.ch2.locked"], 12.5, PassportUi.Muted, align: TextAlignment.Center);
                locked.Margin = new Thickness(0, 0, 0, 16);
                PassportRight.Children.Add(locked);
            }
            PassportRight.Children.Add(PassportStampGrid(list));
        }
        PassportRight.Children.Add(PassportSpecialStamps());
    }

    /// <summary>The chapter's stamps: art, a dot in the level's colour and the name. No boxes.</summary>
    private WrapPanel PassportStampGrid(IEnumerable<ColituStamp> stamps)
    {
        var grid = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(-6, 0) };
        foreach (var stamp in stamps)
        {
            var progress = _passport.ProgressOf(stamp.Code);
            var column = new StackPanel();
            column.Children.Add(PassportUi.StampArt(stamp, progress, PassportStampSize, lockSize: 26));
            var name = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0), MaxWidth = PassportStampSize + 8 };
            if (progress.Level is { } level)
            {
                name.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = PassportUi.LevelBrush(level), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            }
            name.Children.Add(PassportUi.Label(PassportUi.Country(stamp.Code), 12, progress.Unlocked ? PassportUi.Text : PassportUi.Dim, wrap: false));
            column.Children.Add(name);
            var a11y = progress.Level is { } l
                ? Loc.I.Format("passport.stampA11y", ("country", PassportUi.Country(stamp.Code)), ("level", PassportUi.LevelName(l)), ("n", progress.LevelPieces))
                : Loc.I.Format("passport.stampLockedA11y", ("country", PassportUi.Country(stamp.Code)));
            var tile = PassportUi.Plain(column, () => OpenStampSheet(stamp), a11y);
            tile.Padding = new Thickness(4, 4, 4, 6);
            tile.Margin = new Thickness(6, 0, 6, 10);
            tile.Width = PassportStampSize + 8;
            grid.Children.Add(tile);
        }
        return grid;
    }

    /// <summary>Chapter 03 — Legacy: mastery goals with their progress, open or not.</summary>
    private StackPanel PassportLegacy()
    {
        var loc = Loc.I;
        var panel = new StackPanel();
        if (!_passport.Goals.Chapter3Open)
        {
            var locked = PassportUi.Label(loc["passport.legacy.locked"], 12.5, PassportUi.Muted, align: TextAlignment.Center);
            locked.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(locked);
        }
        foreach (var (label, have, level) in new[]
        {
            (loc["passport.legacy.gold1"], _passport.Goals.Legacy(StampLevel.Gold, ColituPassport.Stamps), StampLevel.Gold),
            (loc["passport.legacy.diamond1"], _passport.Goals.Legacy(StampLevel.Diamond, ColituPassport.Stamps), StampLevel.Diamond),
            (loc["passport.legacy.gold2"], _passport.Goals.Legacy(StampLevel.Gold, ColituPassport.Chapter2), StampLevel.Gold)
        })
        {
            var body = new StackPanel();
            var top = PassportUi.Columns("*,Auto");
            top.Children.Add(PassportUi.Label(label, 14, PassportUi.Text, FontWeight.SemiBold));
            var value = new TextBlock { Text = $"{have}/10", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = PassportUi.LevelBrush(level) };
            Grid.SetColumn(value, 1);
            top.Children.Add(value);
            body.Children.Add(top);
            var bar = PassportUi.Bar(have / 10.0, 8, PassportUi.LevelBrush(level));
            bar.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(bar);
            var card = PassportUi.Glass(body, padding: new Thickness(16));
            card.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(card);
        }
        var keep = PassportUi.Label(loc["passport.legacy.keep"], 12, PassportUi.Dim, align: TextAlignment.Center);
        keep.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(keep);
        return panel;
    }

    /// <summary>Special stamps outside the chapters (the Companion stamp), earned or as a hint.</summary>
    private StackPanel PassportSpecialStamps()
    {
        var loc = Loc.I;
        var has = _passport.Social.HasCompanion;
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(PassportUi.Kicker(loc["passport.special"], PassportUi.Dim, TextAlignment.Center));
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(PassportUi.CompanionStamp(has, 76));
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240 };
        text.Children.Add(PassportUi.Label("Companion", 14, has ? PassportUi.Text : PassportUi.Muted, FontWeight.SemiBold));
        text.Children.Add(PassportUi.Label(has ? loc["passport.companion.have"] : loc["passport.companion.how"], 12, PassportUi.Muted));
        row.Children.Add(text);
        AutomationProperties.SetName(row, "Companion: " + (has ? loc["passport.companion.have"] : loc["passport.companion.how"]));
        panel.Children.Add(row);
        return panel;
    }

    // ── Connect ────────────────────────────────────────────────────────────

    /// <summary>The best open server of a stamp's country (no multihop), or null.</summary>
    private ColituVpnServer? PassportServerFor(string code) =>
        _servers.Where(server => !server.IsMultihop && server.Available && !server.Locked && ColituPassport.StampFor(server.CountryCode)?.Code == code)
            .OrderBy(server => server.PingSortValue)
            .FirstOrDefault();

    private bool PassportOnCountry(string? code) =>
        _vpn.Status == ColituVpnStatus.Connected && _vpn.ConnectedServer is { } server
        && ColituPassport.StampFor(server.IsMultihop ? server.Exit?.Country ?? server.CountryCode : server.CountryCode)?.Code == code;

    /// <summary>Connects (or switches) to <paramref name="server"/>, or to the saved choice when null; stays on the passport.</summary>
    private async Task PassportConnectAsync(ColituVpnServer? server)
    {
        try
        {
            var active = _vpn.Status is ColituVpnStatus.Connected or ColituVpnStatus.Reconnecting;
            if (server != null)
            {
                _vpn.SelectServer(server);
                ApplyLocationCard();
            }
            if (active && server != null)
            {
                ShowToast(Loc.I.Format("locations.switching", ("server", ServerLabel(server) ?? PassportUi.Country(server.CountryCode ?? ""))));
                await _vpn.SwitchServerAsync(server);
            }
            else if (!active)
            {
                await ToggleConnectionAsync();
            }
        }
        catch (ColituPlanRequiredException)
        {
            _planRequired = true;
            ApplyAccount();
            Navigate("plan");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, true);
        }
        ApplyStatus();
        if (_page == "passport") RenderPassport();
    }
}

using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.VisualTree;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// The passport's panels (the Windows app's ColituMainWindow.PassportSheets.cs): next goal,
/// expeditions, collections, achievements, rules, one stamp, the editor and the passport
/// comparison. One compact panel at a time over the page; Esc or a click outside closes it.
/// </summary>
public partial class ColituMainWindow
{
    private Action? _sheetRebuild;
    private Action? _sheetClosed;
    private IInputElement? _sheetReturnFocus;
    private Border? _sheetCard;
    private TextBlock? _sheetTitle;
    private Grid? _sheetHeader;
    private ContentControl? _sheetContent;
    private ContentControl? _sheetFooter;
    private ScrollViewer? _sheetScroll;
    private Border? _sheetBackdrop;
    private Button? _sheetClose;
    private bool _sheetWired;

    /// <summary>The panel: a scrim, a card (title, close, scrolling content, footer) — built once.</summary>
    private void EnsurePassportSheet()
    {
        if (_sheetCard != null) return;
        var scrim = new Border { Background = PassportUi.Solid(Color.FromArgb(0xB3, 0x05, 0x05, 0x08)) };
        scrim.PointerPressed += (_, _) => ClosePassportSheet();
        PassportSheetLayer.Children.Add(scrim);

        _sheetBackdrop = new Border
        {
            IsHitTestVisible = false,
            Opacity = 0.55,
            IsVisible = false,
            Background = PassportUi.Image("passport_sheet_bg.jpg") is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top } : null
        };
        _sheetTitle = new TextBlock { FontSize = 20, Margin = new Thickness(0, 0, 48, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Classes = { "h2" } };
        _sheetClose = PassportUi.Round(PassportUi.Icon("x", PassportUi.Muted, 11), ClosePassportSheet, Loc.I["passport.close"]);
        _sheetClose.HorizontalAlignment = HorizontalAlignment.Right;
        _sheetHeader = new Grid { Margin = new Thickness(22, 16, 14, 4) };
        _sheetHeader.Children.Add(_sheetTitle);
        _sheetHeader.Children.Add(_sheetClose);
        _sheetContent = new ContentControl { Margin = new Thickness(22, 4, 22, 6) };
        _sheetScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _sheetContent };
        _sheetFooter = new ContentControl { Margin = new Thickness(22, 6, 22, 18) };
        var rows = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        rows.Children.Add(_sheetHeader);
        Grid.SetRow(_sheetScroll, 1);
        rows.Children.Add(_sheetScroll);
        Grid.SetRow(_sheetFooter, 2);
        rows.Children.Add(_sheetFooter);
        var inner = new Panel();
        inner.Children.Add(_sheetBackdrop);
        inner.Children.Add(rows);
        _sheetCard = new Border
        {
            Width = 460,
            Margin = new Thickness(24, 76, 24, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Resource<IBrush>("PassportSheetFill"),
            BorderBrush = Resource<IBrush>("LineStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(26),
            ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 14 50 0 #99000000"),
            Child = inner
        };
        KeyboardNavigation.SetTabNavigation(_sheetCard, KeyboardNavigationMode.Cycle);
        PassportSheetLayer.Children.Add(_sheetCard);
        if (!_sheetWired)
        {
            _sheetWired = true;
            AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                if (PassportRevealLayer.IsVisible && _revealDone != null)
                {
                    e.Handled = true;
                    FinishReveal();
                }
                else if (PassportSheetLayer.IsVisible)
                {
                    e.Handled = true;
                    ClosePassportSheet();
                }
            }, RoutingStrategies.Tunnel);
        }
    }

    private void OpenPassportSheet(string title, Func<Control> content, Func<Control?>? footer = null, bool backdrop = false, Action? onClosed = null, bool showTitle = true, double width = 460)
    {
        EnsurePassportSheet();
        if (!PassportSheetLayer.IsVisible)
        {
            _sheetReturnFocus = FocusManager?.GetFocusedElement();
        }
        _sheetClosed = onClosed;
        _sheetRebuild = () =>
        {
            _sheetContent!.Content = content();
            _sheetFooter!.Content = footer?.Invoke();
            _sheetFooter.IsVisible = _sheetFooter.Content != null;
        };
        _sheetTitle!.Text = title;
        _sheetTitle.Opacity = showTitle ? 1 : 0;
        // Without a title the close button floats over the content's top corner (no empty header row).
        Grid.SetRowSpan(_sheetHeader!, showTitle ? 1 : 2);
        _sheetHeader!.ZIndex = showTitle ? 0 : 1;
        _sheetHeader.VerticalAlignment = showTitle ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        _sheetContent!.Margin = new Thickness(22, showTitle ? 4 : 20, 22, 6);
        _sheetCard!.Width = Math.Min(width, Math.Max(320, Bounds.Width - 48));
        AutomationProperties.SetName(_sheetCard, title);
        _sheetBackdrop!.IsVisible = backdrop;
        _sheetRebuild();
        _sheetScroll!.Offset = default;
        PassportSheetLayer.IsVisible = true;
        var shift = new TranslateTransform();
        _sheetCard.RenderTransform = shift;
        PassportUi.Tween(0.32, 0, t =>
        {
            _sheetCard.Opacity = t;
            shift.Y = (1 - t) * 28;
        });
        Dispatcher.UIThread.Post(() => (FindFocusable(_sheetContent) ?? _sheetClose)?.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
    }

    private static Control? FindFocusable(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is Control { Focusable: true, IsEnabled: true, IsVisible: true } control and (Button or ToggleButton or TextBox))
            {
                return control;
            }
            if (FindFocusable(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Rebuilds the open panel in place (after a choice inside it).</summary>
    private void RefreshPassportSheet() => _sheetRebuild?.Invoke();

    private void ClosePassportSheet()
    {
        if (!PassportSheetLayer.IsVisible) return;
        PassportSheetLayer.IsVisible = false;
        _sheetContent!.Content = null;
        _sheetFooter!.Content = null;
        _sheetRebuild = null;
        var closed = _sheetClosed;
        _sheetClosed = null;
        closed?.Invoke();
        if (_page == "passport") RenderPassport();
        ApplyPassportHome();
        var back = _sheetReturnFocus as Control;
        Dispatcher.UIThread.Post(() =>
        {
            if (back is { IsEffectivelyVisible: true }) back.Focus();
            else _passportBackButton?.Focus();
        }, DispatcherPriority.Loaded);
    }

    // ── Next goal ──────────────────────────────────────────────────────────

    /// <summary>The next goal explained: what it is, how to reach it, today's numbers and a way to act.</summary>
    private void OpenNextSheet() => OpenPassportSheet(Loc.I["passport.nx.title"], () =>
    {
        var loc = Loc.I;
        var goal = NextPassportGoal();
        var (minutes, pieces) = _passport.Today;
        var panel = new StackPanel();
        Control middle = goal.Kind == PassportGoalKind.Level && goal.Code is { } code && ColituPassport.StampOf(code) is { } stamp
            ? PassportUi.StampArt(stamp, _passport.ProgressOf(stamp.Code), 70)
            : PassportUi.Icon(goal.Icon, PassportUi.Lilac, 30);
        var ring = PassportUi.Ring(goal.Fraction, 96, 6, middle);
        ring.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(ring);
        var title = PassportUi.Label(goal.Text, 17, PassportUi.Text, FontWeight.SemiBold, TextAlignment.Center);
        title.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(title);
        var country = goal.Code != null ? PassportUi.Country(goal.Code) : "";
        var body = goal.Kind switch
        {
            PassportGoalKind.Today => loc["passport.nx.today"],
            PassportGoalKind.Level => loc.Format("passport.nx.level", ("country", country), ("level", goal.Target ?? ""), ("n", goal.Left)),
            _ => loc["passport.nx.done"]
        };
        var text = PassportUi.Label(body, 13.5, PassportUi.Muted, align: TextAlignment.Center);
        text.Margin = new Thickness(0, 6, 0, 14);
        panel.Children.Add(text);
        var stats = new UniformGrid { Rows = 1, Columns = 2, Margin = new Thickness(-5, 0) };
        stats.Children.Add(GoalStat(loc["passport.nx.minutes"], minutes.ToString(loc.Culture)));
        stats.Children.Add(GoalStat(loc["passport.nx.pieces"], $"{pieces}/{ColituPassport.PieceMinutes.Length}"));
        panel.Children.Add(stats);

        var server = goal.Code != null ? PassportServerFor(goal.Code) : null;
        var connected = _vpn.Status == ColituVpnStatus.Connected;
        Control? action = null;
        if (goal.Kind == PassportGoalKind.Level && server != null && !PassportOnCountry(goal.Code))
        {
            action = PassportUi.Primary(loc["passport.connect"], () =>
            {
                ClosePassportSheet();
                _ = PassportConnectAsync(server);
            });
        }
        else if (!connected && goal.Kind != PassportGoalKind.Done)
        {
            action = PassportUi.Primary(loc["home.connect"], () =>
            {
                ClosePassportSheet();
                _ = PassportConnectAsync(null);
            });
        }
        else if (connected)
        {
            action = PassportUi.Label(loc["passport.nx.counting"], 12.5, PassportUi.Lilac, align: TextAlignment.Center);
        }
        if (action != null)
        {
            action.Margin = new Thickness(0, 16, 0, 0);
            panel.Children.Add(action);
        }
        return panel;
    });

    private static Border GoalStat(string label, string value)
    {
        var column = new StackPanel();
        column.Children.Add(PassportUi.Label(value, 18, PassportUi.Text, FontWeight.SemiBold, TextAlignment.Center));
        column.Children.Add(PassportUi.Label(label, 11.5, PassportUi.Muted, align: TextAlignment.Center));
        return new Border { Background = PassportUi.Tile, BorderBrush = PassportUi.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(8), Margin = new Thickness(5, 0), Child = column };
    }

    // ── Weekly expeditions ─────────────────────────────────────────────────

    /// <summary>This week's three goals; no countdown, a missed week costs nothing.</summary>
    private void OpenWeeklySheet() => OpenPassportSheet(Loc.I["passport.week.title"], () =>
    {
        var loc = Loc.I;
        var weekly = _passport.Weekly;
        var panel = new StackPanel();
        var total = PassportUi.Label(loc.Format("passport.week.total", ("n", weekly.Total)), 12.5, PassportUi.Lilac);
        total.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(total);
        foreach (var goal in weekly.Goals())
        {
            var target = weekly.Target(goal);
            var have = Math.Min(weekly.Progress(goal), target);
            var done = weekly.Done(goal);
            var row = PassportUi.Columns("Auto,*,Auto");
            row.Margin = new Thickness(0, 7);
            row.Children.Add(new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(11),
                Background = done ? PassportUi.LilacSoft : PassportUi.Tile,
                Child = PassportUi.Icon(done ? "check" : PassportUi.ExpeditionIcon(goal), done ? PassportUi.Lilac : PassportUi.Muted, 16)
            });
            var middle = new StackPanel { Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center };
            middle.Children.Add(PassportUi.Label(loc[$"passport.x.{goal.Id}"], 13.5, done ? PassportUi.Text : PassportUi.Muted));
            var bar = PassportUi.Bar(have / (double)target, 5, PassportUi.Accent);
            bar.Margin = new Thickness(0, 6, 0, 0);
            middle.Children.Add(bar);
            Grid.SetColumn(middle, 1);
            row.Children.Add(middle);
            var count = PassportUi.Label($"{have}/{target}", 12.5, done ? PassportUi.Lilac : PassportUi.Dim, wrap: false);
            count.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(count, 2);
            row.Children.Add(count);
            AutomationProperties.SetName(row, $"{loc[$"passport.x.{goal.Id}"]}: {have}/{target}");
            panel.Children.Add(row);
        }
        var note = PassportUi.Label(loc["passport.week.note"], 12, PassportUi.Dim);
        note.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(note);
        return panel;
    });

    // ── Collections ────────────────────────────────────────────────────────

    private void OpenSetsSheet() => OpenPassportSheet(Loc.I["passport.tab.sets"], () =>
    {
        var loc = Loc.I;
        var goals = _passport.Goals;
        var panel = new StackPanel();
        foreach (var set in PassportCollection.All)
        {
            var (have, need) = goals.Progress(set);
            var earned = goals.Completed(set);
            var hidden = set.Secret && !earned;
            var row = PassportUi.Columns("Auto,*");
            row.Children.Add(PassportUi.Seal(set, earned, 54));
            var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var top = PassportUi.Columns("*,Auto");
            top.Children.Add(PassportUi.Label(hidden ? "???" : set.Title, 14.5, PassportUi.Text, FontWeight.SemiBold, wrap: false));
            var value = new TextBlock { Text = $"{have}/{need}", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = earned ? PassportUi.Solid(PassportUi.SealColor(set)) : PassportUi.Muted };
            Grid.SetColumn(value, 1);
            top.Children.Add(value);
            text.Children.Add(top);
            if (hidden)
            {
                text.Children.Add(PassportUi.Label(loc["passport.set.secret"], 12.5, PassportUi.Muted));
            }
            else if (set == PassportCollection.GoldRush)
            {
                text.Children.Add(PassportUi.Label(loc["passport.set.goldRush"], 12.5, PassportUi.Muted));
            }
            else
            {
                var flags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var code in set.Codes)
                {
                    flags.Children.Add(PassportFlag(code, 22, _passport.UnlockedAnywhere(code)));
                }
                text.Children.Add(flags);
            }
            var reward = earned
                ? loc.Format("passport.set.done", ("date", DateTimeOffset.FromUnixTimeMilliseconds(goals.EarnedAt($"c_{set.Id}")).LocalDateTime.ToString("d", loc.Culture)))
                : set.Cover is { } cover ? loc.Format("passport.set.rewardCover", ("cover", loc[$"passport.cover.{cover.Key()}"])) : loc["passport.set.rewardSeal"];
            var rewardText = PassportUi.Label(reward, 12, earned ? PassportUi.Solid(PassportUi.SealColor(set)) : PassportUi.Dim);
            rewardText.Margin = new Thickness(0, 6, 0, 0);
            text.Children.Add(rewardText);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            AutomationProperties.SetName(row, $"{(hidden ? loc["passport.set.secret"] : set.Title)}: {have}/{need}");
            var card = PassportUi.Glass(row, padding: new Thickness(12, 10));
            card.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(card);
        }
        var next = PassportUi.Label(loc["passport.set.nextChapter"], 12, PassportUi.Muted, align: TextAlignment.Center);
        next.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(next);
        return panel;
    });

    /// <summary>A round flag (dimmed when the stamp is not there yet).</summary>
    private static Control PassportFlag(string code, double size, bool on)
    {
        var grid = new Panel { Width = size, Height = size, Margin = new Thickness(0, 0, 6, 0), Opacity = on ? 1 : 0.3 };
        if (ColituFlags.Get(code) is { } flag)
        {
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(size / 2), ClipToBounds = true, Child = new Image { Source = flag, Stretch = Stretch.UniformToFill } });
        }
        grid.Children.Add(new Ellipse { Stroke = PassportUi.Line, StrokeThickness = 1 });
        ToolTip.SetTip(grid, PassportUi.Country(code));
        AutomationProperties.SetName(grid, PassportUi.Country(code));
        return grid;
    }

    // ── Achievements ───────────────────────────────────────────────────────

    private void OpenBadgesSheet() => OpenPassportSheet(Loc.I["passport.tab.badges"], () =>
    {
        var loc = Loc.I;
        var goals = _passport.Goals;
        var panel = new StackPanel();
        foreach (var a in PassportAchievement.All)
        {
            var earned = goals.Achieved(a);
            var hidden = a.Secret && !earned;
            var inUse = goals.Title == a.Id;
            var row = PassportUi.Columns("Auto,*");
            row.Children.Add(PassportUi.Badge(a, earned, 42));
            var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(PassportUi.Label(hidden ? "???" : a.Title, 14.5, earned ? PassportUi.Text : PassportUi.Muted, FontWeight.SemiBold, wrap: false));
            text.Children.Add(PassportUi.Label(hidden ? loc["passport.ach.secret"] : loc[$"passport.ach.{a.Id}"], 12.5, PassportUi.Muted));
            if (earned)
            {
                var label = inUse ? loc["passport.ach.inUse"] : loc["passport.ach.use"];
                var chip = PassportUi.Plain(new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeight.SemiBold }, () => _ = SavePassportProfileAsync(titleId: inUse ? "" : a.Id), label);
                chip.Padding = new Thickness(14, 7);
                chip.Background = inUse ? PassportUi.LilacSoft : PassportUi.Solid(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
                chip.BorderBrush = inUse ? PassportUi.Lilac : PassportUi.Line;
                chip.BorderThickness = new Thickness(1);
                chip.HorizontalAlignment = HorizontalAlignment.Left;
                chip.Margin = new Thickness(0, 8, 0, 0);
                text.Children.Add(chip);
            }
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var card = PassportUi.Glass(row, padding: new Thickness(12, 10));
            if (inUse && card is Border border) border.BorderBrush = PassportUi.Lilac;
            card.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(card);
        }
        return panel;
    });

    // ── How it works ───────────────────────────────────────────────────────

    /// <summary>Rules in four steps and today's count (saved to the account, nothing to set here).</summary>
    private void OpenHowSheet() => OpenPassportSheet(Loc.I["passport.how"], () =>
    {
        var loc = Loc.I;
        var panel = new StackPanel();
        foreach (var (icon, title, body) in new[]
        {
            ("power", "passport.how1", "passport.how1b"),
            ("star", "passport.how2", "passport.how2b"),
            ("arrowUp", "passport.how3", "passport.how3b"),
            ("grid", "passport.how4", "passport.how4b")
        })
        {
            var row = PassportUi.Columns("Auto,*");
            row.Margin = new Thickness(0, 0, 0, 14);
            var well = PassportUi.IconWell(icon, 36);
            well.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(well);
            var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            text.Children.Add(PassportUi.Label(loc[title], 14, PassportUi.Text, FontWeight.SemiBold));
            text.Children.Add(PassportUi.Label(loc[body], 12.5, PassportUi.Muted));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        var (minutes, pieces) = _passport.Today;
        panel.Children.Add(PassportUi.Label(loc.Format("passport.today", ("m", minutes), ("p", pieces)), 12.5, PassportUi.Lilac));
        var local = PassportUi.Label(loc["passport.local"], 12, PassportUi.Dim);
        local.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(local);
        return panel;
    });

    // ── One stamp ──────────────────────────────────────────────────────────

    /// <summary>One stamp: its puzzle, level and count (the country named once); share it or connect to its country.</summary>
    private void OpenStampSheet(ColituStamp stamp) => OpenPassportSheet(PassportUi.Country(stamp.Code), () =>
    {
        var loc = Loc.I;
        var country = PassportUi.Country(stamp.Code);
        var progress = _passport.ProgressOf(stamp.Code);
        var level = progress.Level;
        var panel = new StackPanel();
        var art = PassportUi.StampArt(stamp, progress, 196, lockSize: 44, puzzle: true);
        art.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(art);
        var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        head.Children.Add(PassportUi.Label(country, 22, PassportUi.Text, FontWeight.SemiBold, wrap: false));
        if (level is { } l)
        {
            var color = PassportUi.LevelColor(l);
            head.Children.Add(new Border
            {
                Margin = new Thickness(10, 4, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(10, 3),
                CornerRadius = new CornerRadius(12),
                Background = PassportUi.Solid(Color.FromArgb(0x29, color.R, color.G, color.B)),
                BorderBrush = PassportUi.Solid(Color.FromArgb(0x8C, color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
                Child = PassportUi.Label(PassportUi.LevelName(l).ToUpper(loc.Culture), 10.5, PassportUi.Solid(color), FontWeight.Bold, wrap: false)
            });
        }
        panel.Children.Add(head);
        if (level is { } current)
        {
            var meta = progress.City is { Length: > 0 } city ? city : loc.Format("passport.stampNo", ("n", stamp.NumberText));
            var metaText = PassportUi.Label(meta, 12.5, PassportUi.Muted, align: TextAlignment.Center);
            metaText.Margin = new Thickness(0, 2, 0, 12);
            panel.Children.Add(metaText);
            if (!progress.Mastered)
            {
                var bar = PassportUi.SegmentBar(progress.LevelPieces, PassportUi.LevelColor(current));
                bar.Margin = new Thickness(16, 0, 16, 8);
                panel.Children.Add(bar);
            }
            var goal = progress.Mastered ? loc["passport.masteredShort"]
                : current == StampLevel.Diamond ? loc.Format("passport.toMaster", ("n", progress.ToNext))
                : loc.Format("passport.toNext", ("level", PassportUi.LevelName(current + 1)), ("n", progress.ToNext));
            panel.Children.Add(PassportUi.Label(goal, 14, PassportUi.LevelBrush(current), FontWeight.SemiBold, TextAlignment.Center));
        }
        else
        {
            var available = PassportServerFor(stamp.Code);
            var any = _servers.Any(s => !s.IsMultihop && ColituPassport.StampFor(s.CountryCode)?.Code == stamp.Code);
            var hint = available != null ? loc["passport.lockedHint"]
                : !any ? loc.Format("passport.planDesc", ("country", country))
                : loc.Format("passport.offlineDesc", ("country", country));
            var hintText = PassportUi.Label(hint, 13.5, PassportUi.Muted, align: TextAlignment.Center);
            hintText.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(hintText);
        }
        return panel;
    }, () =>
    {
        var loc = Loc.I;
        var progress = _passport.ProgressOf(stamp.Code);
        var buttons = new StackPanel();
        if (progress.Unlocked)
        {
            buttons.Children.Add(PassportUi.Primary(loc["passport.shareStamp"], () => SharePassportCard(stamp)));
            if (PassportServerFor(stamp.Code) is { } more && !PassportOnCountry(stamp.Code))
            {
                var connect = PassportUi.Link(loc["passport.connect"], () =>
                {
                    ClosePassportSheet();
                    _ = PassportConnectAsync(more);
                });
                connect.Margin = new Thickness(0, 10, 0, 0);
                buttons.Children.Add(connect);
            }
        }
        else if (PassportServerFor(stamp.Code) is { } server)
        {
            buttons.Children.Add(PassportUi.Primary(loc["passport.connect"], () =>
            {
                ClosePassportSheet();
                _ = PassportConnectAsync(server);
            }));
        }
        return buttons.Children.Count == 0 ? null : buttons;
    }, backdrop: true, showTitle: false, width: 440);

    // ── Passport editor ────────────────────────────────────────────────────

    private string _passportNickDraft = "";

    /// <summary>The passport editor: a preview, then name, cover, title and favourite stamp. Closing saves the name to the account.</summary>
    private void OpenIdentitySheet()
    {
        _passportNickDraft = _passport.Goals.Nickname;
        OpenPassportSheet(Loc.I["passport.id.edit"], BuildIdentity, () => PassportUi.Primary(Loc.I["passport.id.save"], ClosePassportSheet), onClosed: () =>
        {
            if (_passportNickDraft.Trim() != _passport.Goals.Nickname)
            {
                _ = SavePassportProfileAsync(nickname: _passportNickDraft);
            }
        });
    }

    private Control BuildIdentity()
    {
        var loc = Loc.I;
        var goals = _passport.Goals;
        var premium = PassportPremium;
        var panel = new StackPanel();
        var cover = PassportUi.Cover(goals.ActiveCover(premium), 116);
        cover.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(cover);

        var holder = PassportUi.Columns("*,Auto");
        var lines = new StackPanel();
        lines.Children.Add(PassportUi.Kicker(loc["passport.id.holder"], PassportUi.Dim));
        var nickPreview = PassportUi.Label(_passportNickDraft.Trim().Length > 0 ? _passportNickDraft.Trim() : "—", 18, PassportUi.Text, FontWeight.SemiBold, wrap: false);
        lines.Children.Add(nickPreview);
        var titleKicker = PassportUi.Kicker(loc["passport.id.title"], PassportUi.Dim);
        titleKicker.Margin = new Thickness(0, 8, 0, 0);
        lines.Children.Add(titleKicker);
        lines.Children.Add(PassportUi.Label(goals.TitleOf(goals.Title) ?? loc["passport.id.noTitle"], 14, PassportUi.Lilac, FontWeight.SemiBold, wrap: false));
        var collector = PassportUi.Label(loc.Format("passport.id.collector", ("n", goals.CollectorNo)), 12.5, PassportUi.Muted);
        collector.Margin = new Thickness(0, 8, 0, 0);
        lines.Children.Add(collector);
        holder.Children.Add(lines);
        if (goals.Favorite != null && ColituPassport.StampOf(goals.Favorite) is { } fav)
        {
            var art = PassportUi.StampArt(fav, _passport.ProgressOf(fav.Code), 72);
            art.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(art, 1);
            holder.Children.Add(art);
        }
        var holderCard = PassportUi.Glass(holder, padding: new Thickness(16));
        holderCard.Margin = new Thickness(0, 12, 0, 6);
        panel.Children.Add(holderCard);

        // Name
        panel.Children.Add(EditorTitle(loc["passport.id.nick"]));
        var nick = new TextBox { Text = _passportNickDraft, MaxLength = PassportGoals.NicknameMax, PlaceholderText = loc["passport.id.nickHint"], Classes = { "bare" } };
        AutomationProperties.SetName(nick, loc["passport.id.nick"]);
        AutomationProperties.SetHelpText(nick, loc["passport.id.nickHint"]);
        nick.TextChanged += (_, _) =>
        {
            _passportNickDraft = nick.Text ?? "";
            nickPreview.Text = _passportNickDraft.Trim().Length > 0 ? _passportNickDraft.Trim() : "—";
        };
        nick.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) _ = SavePassportProfileAsync(nickname: nick.Text ?? "");
        };
        panel.Children.Add(new Border { Classes = { "field" }, Child = nick });

        // Cover
        panel.Children.Add(EditorTitle(loc["passport.id.cover"]));
        var active = goals.ActiveCover(premium);
        var open = goals.Covers(premium);
        var covers = new WrapPanel();
        foreach (var theme in CoverThemes.All)
        {
            var unlocked = open.Contains(theme);
            var selected = theme == active;
            var column = new StackPanel { Width = 84 };
            var art = new Panel();
            var image = PassportUi.Cover(theme, 66);
            image.Opacity = unlocked ? 1 : 0.35;
            image.HorizontalAlignment = HorizontalAlignment.Center;
            art.Children.Add(image);
            if (!unlocked) art.Children.Add(PassportUi.Icon("lock", PassportUi.Text, 18));
            column.Children.Add(new Border
            {
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(16),
                Background = selected ? PassportUi.LilacSoft : PassportUi.Tile,
                BorderBrush = selected ? PassportUi.Lilac : PassportUi.Line,
                BorderThickness = new Thickness(selected ? 2 : 1),
                Child = art
            });
            var name = loc[$"passport.cover.{theme.Key()}"];
            column.Children.Add(PassportUi.Label(name, 12, selected ? PassportUi.Lilac : PassportUi.Text, align: TextAlignment.Center, wrap: false));
            var requirement = unlocked ? null : CoverRequirement(theme);
            if (requirement != null) column.Children.Add(PassportUi.Label(requirement, 10.5, PassportUi.Dim, align: TextAlignment.Center));
            var button = PassportUi.Plain(column, () => _ = SavePassportProfileAsync(coverId: theme.Key()), requirement == null ? name : $"{name} · {requirement}");
            button.IsEnabled = unlocked;
            button.Margin = new Thickness(0, 0, 10, 10);
            covers.Children.Add(button);
        }
        panel.Children.Add(covers);

        // Title
        panel.Children.Add(EditorTitle(loc["passport.id.title"]));
        var titles = new WrapPanel();
        void AddTitle(string label, string? id, bool earned)
        {
            object content = earned ? label : new StackPanel { Orientation = Orientation.Horizontal, Children = { PassportUi.Icon("lock", PassportUi.Dim, 12), new TextBlock { Text = " " + label } } };
            var chip = PassportUi.Chip(content, "PassportTitle", earned && goals.Title == id, label);
            chip.IsEnabled = earned;
            chip.Margin = new Thickness(0, 0, 8, 8);
            chip.IsCheckedChanged += (_, _) =>
            {
                if (chip.IsChecked != true || goals.Title == id) return;
                _ = SavePassportProfileAsync(titleId: id ?? "");
            };
            titles.Children.Add(chip);
        }
        AddTitle(loc["passport.id.noTitle"], null, true);
        foreach (var a in PassportAchievement.All)
        {
            var earned = goals.Achieved(a);
            AddTitle(earned || !a.Secret ? a.Title : "???", a.Id, earned);
        }
        panel.Children.Add(titles);

        // Favourite stamp (kept on this device)
        panel.Children.Add(EditorTitle(loc["passport.id.favorite"]));
        var unlockedStamps = ColituPassport.AllStamps.Where(s => _passport.ProgressOf(s.Code).Unlocked).ToList();
        if (unlockedStamps.Count == 0)
        {
            panel.Children.Add(PassportUi.Label(loc["passport.id.noStamps"], 12.5, PassportUi.Muted));
        }
        else
        {
            var favorites = new WrapPanel();
            foreach (var s in unlockedStamps)
            {
                var selected = goals.Favorite == s.Code;
                var button = PassportUi.Plain(PassportUi.StampArt(s, _passport.ProgressOf(s.Code), 64), () =>
                {
                    goals.UpdateFavorite(selected ? null : s.Code);
                    RefreshPassportSheet();
                }, PassportUi.Country(s.Code));
                button.Padding = new Thickness(6);
                button.Margin = new Thickness(0, 0, 8, 8);
                button.Background = selected ? PassportUi.LilacSoft : PassportUi.Tile;
                button.BorderBrush = selected ? PassportUi.Lilac : PassportUi.Line;
                button.BorderThickness = new Thickness(selected ? 2 : 1);
                favorites.Children.Add(button);
            }
            panel.Children.Add(favorites);
        }
        return panel;
    }

    private static TextBlock EditorTitle(string text)
    {
        var title = PassportUi.Label(text, 14, PassportUi.Text, FontWeight.SemiBold);
        title.Margin = new Thickness(0, 16, 0, 8);
        return title;
    }

    private static string? CoverRequirement(CoverTheme theme)
    {
        if (theme == CoverTheme.Premium) return Loc.I["passport.req.premium"];
        return PassportCollection.All.FirstOrDefault(c => c.Cover == theme) is { } set ? Loc.I.Format("passport.req.set", ("set", set.Title)) : null;
    }

    /// <summary>Saves a name, title or cover to the account; the panel's answer becomes the shown state.</summary>
    private async Task SavePassportProfileAsync(string? nickname = null, string? titleId = null, string? coverId = null)
    {
        var saved = await PassportSync.UpdateProfileAsync(nickname, titleId, coverId);
        if (!saved)
        {
            ShowToast(Loc.I["passport.saveFailed"], true);
        }
        RefreshPassportSheet();
        if (!PassportSheetLayer.IsVisible && _page == "passport") RenderPassport();
        ApplyPassportHome();
    }

    // ── Passport comparison ────────────────────────────────────────────────

    private int _duelMode;
    private DuelPassport? _duelFriend;
    private string _duelText = "";
    private bool _duelBad;

    /// <summary>Compare passports without a camera: show my QR (a phone scans it) or paste the https://colitu.com/p#… link a friend shared.</summary>
    private void OpenDuelSheet()
    {
        _duelMode = 0;
        _duelFriend = null;
        _duelText = "";
        _duelBad = false;
        OpenPassportSheet(Loc.I["passport.duel.title"], BuildDuel);
    }

    private Control BuildDuel()
    {
        var loc = Loc.I;
        var panel = new StackPanel();
        panel.Children.Add(PassportUi.Label(loc["passport.duel.sub"], 12.5, PassportUi.Muted, align: TextAlignment.Center));
        if (_duelFriend is { } them)
        {
            panel.Children.Add(DuelCompare(them));
            var again = PassportUi.Secondary(loc["passport.duel.again"], () =>
            {
                _duelFriend = null;
                _duelText = "";
                _duelMode = 1;
                RefreshPassportSheet();
            });
            again.Margin = new Thickness(0, 16, 0, 0);
            panel.Children.Add(again);
            return panel;
        }

        var modes = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(0, 12, 0, 14) };
        foreach (var (mode, key) in new[] { (0, "passport.duel.mine"), (1, "passport.duel.paste"), (2, "passport.friends") })
        {
            var chip = PassportUi.Chip(loc[key], "PassportDuelMode", _duelMode == mode, loc[key]);
            chip.Margin = new Thickness(3, 0);
            chip.IsCheckedChanged += (_, _) =>
            {
                if (chip.IsChecked != true || _duelMode == mode) return;
                _duelMode = mode;
                Dispatcher.UIThread.Post(RefreshPassportSheet);
            };
            modes.Children.Add(chip);
        }
        panel.Children.Add(modes);

        switch (_duelMode)
        {
            case 0:
            {
                var link = _passport.Social.MyLink();
                var image = new Image { Source = PassportUi.QrImage(link), Width = 172, Height = 172 };
                RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
                AutomationProperties.SetName(image, loc["passport.duel.mine"]);
                panel.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(16), Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Center, Child = image });
                var identity = PassportUi.Label(PassportIdentityLine(), 12.5, PassportUi.Lilac, align: TextAlignment.Center);
                identity.Margin = new Thickness(0, 10, 0, 14);
                panel.Children.Add(identity);
                var buttons = new UniformGrid { Rows = 1, Columns = 2 };
                var copy = PassportUi.Secondary(loc["passport.duel.copyLink"], () => _ = CopyPassportLinkAsync(link));
                copy.Margin = new Thickness(0, 0, 5, 0);
                var share = PassportUi.Secondary(loc["passport.share"], () => SharePassportCard(null));
                share.Margin = new Thickness(5, 0, 0, 0);
                buttons.Children.Add(copy);
                buttons.Children.Add(share);
                panel.Children.Add(buttons);
                break;
            }
            case 1:
            {
                var box = new TextBox { Text = _duelText, PlaceholderText = loc["passport.duel.pasteHint"], Classes = { "bare" } };
                AutomationProperties.SetName(box, loc["passport.duel.paste"]);
                panel.Children.Add(new Border { Classes = { "field" }, Child = box });
                var hint = PassportUi.Label(_duelBad ? loc["passport.duel.notPassport"] : loc["passport.duel.pasteHint"], 12, _duelBad ? Resource<IBrush>("DangerBrush") : PassportUi.Dim);
                hint.Margin = new Thickness(2, 6, 0, 14);
                panel.Children.Add(hint);
                void Compare(string text)
                {
                    _duelText = text;
                    var parsed = PassportDuel.Parse(text);
                    _duelBad = parsed == null;
                    if (parsed != null)
                    {
                        _duelFriend = parsed;
                        _passport.Social.AddFriend(parsed.ToFriend(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    }
                    RefreshPassportSheet();
                }
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) Compare(box.Text ?? "");
                };
                var buttons = new UniformGrid { Rows = 1, Columns = 2 };
                var paste = PassportUi.Secondary(loc["passport.duel.fromClipboard"], async () =>
                {
                    if (await AvaUtils.GetClipboardData(this) is { Length: > 0 } text) Compare(text.Trim());
                });
                paste.Margin = new Thickness(0, 0, 5, 0);
                var compare = PassportUi.Primary(loc["passport.duel.compare"], () => Compare(box.Text ?? ""), 46);
                compare.Margin = new Thickness(5, 0, 0, 0);
                buttons.Children.Add(paste);
                buttons.Children.Add(compare);
                panel.Children.Add(buttons);
                break;
            }
            default:
                panel.Children.Add(FriendsList());
                break;
        }
        return panel;
    }

    private async Task CopyPassportLinkAsync(string link)
    {
        await AvaUtils.SetClipboardData(this, link);
        ShowToast(Loc.I["passport.duel.linkCopied"]);
    }

    private StackPanel DuelCompare(DuelPassport them)
    {
        var loc = Loc.I;
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var top = new UniformGrid { Rows = 1, Columns = 2 };
        var mine = new StackPanel();
        mine.Children.Add(PassportUi.Label(loc["passport.duel.you"], 12.5, PassportUi.Muted, align: TextAlignment.Center));
        mine.Children.Add(PassportUi.Label($"{_passport.Count}/10", 34, PassportUi.Lilac, FontWeight.Bold, TextAlignment.Center));
        var theirs = new StackPanel();
        var name = them.Nick.Trim().Length > 0 ? them.Nick : loc.Format("passport.id.collector", ("n", them.Collector));
        theirs.Children.Add(PassportUi.Label(name, 12.5, PassportUi.Muted, align: TextAlignment.Center, wrap: false));
        theirs.Children.Add(PassportUi.Label($"{them.CountIn(ColituPassport.Stamps)}/10", 34, PassportUi.Text, FontWeight.Bold, TextAlignment.Center));
        if (PassportAchievement.Of(PassportIds.Normalize(them.Title)) is { } title)
        {
            theirs.Children.Add(PassportUi.Label(title.Title, 12, PassportUi.Lilac, align: TextAlignment.Center));
        }
        top.Children.Add(mine);
        top.Children.Add(theirs);
        panel.Children.Add(top);
        foreach (var s in ColituPassport.AllStamps)
        {
            var a = _passport.ProgressOf(s.Code).DuelLevel;
            var b = them.Levels.GetValueOrDefault(s.Code);
            if (a == 0 && b == 0) continue;
            var row = PassportUi.Columns("Auto,*,96,96");
            row.Margin = new Thickness(0, 8, 0, 0);
            row.Children.Add(PassportFlag(s.Code, 24, true));
            var country = PassportUi.Label(PassportUi.Country(s.Code), 13, PassportUi.Text, wrap: false);
            country.Margin = new Thickness(6, 0, 0, 0);
            country.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(country, 1);
            row.Children.Add(country);
            var left = PassportUi.Label(DuelLevelLabel(a), 12.5, DuelLevelBrush(a), a > b ? FontWeight.Bold : FontWeight.Normal, TextAlignment.Center, false);
            left.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(left, 2);
            row.Children.Add(left);
            var right = PassportUi.Label(DuelLevelLabel(b), 12.5, DuelLevelBrush(b), b > a ? FontWeight.Bold : FontWeight.Normal, TextAlignment.Center, false);
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 3);
            row.Children.Add(right);
            AutomationProperties.SetName(row, $"{PassportUi.Country(s.Code)}: {loc["passport.duel.you"]} {DuelLevelLabel(a)}, {name} {DuelLevelLabel(b)}");
            panel.Children.Add(row);
        }
        return panel;
    }

    private static string DuelLevelLabel(int n) => n switch
    {
        0 => "—",
        5 => Loc.I["passport.masteredShort"],
        _ => PassportUi.LevelName((StampLevel)(n - 1))
    };

    private static IBrush DuelLevelBrush(int n) => n == 0 ? PassportUi.Dim : PassportUi.LevelBrush((StampLevel)Math.Min(n - 1, 3));

    /// <summary>Friends added from their links, kept on this device; with the ranking on, you stand among them.</summary>
    private StackPanel FriendsList()
    {
        var loc = Loc.I;
        var social = _passport.Social;
        var panel = new StackPanel();
        var friends = social.Friends;
        if (friends.Count == 0)
        {
            panel.Children.Add(PassportUi.Label(loc["passport.friends.empty"], 12.5, PassportUi.Muted, align: TextAlignment.Center));
            return panel;
        }
        panel.Children.Add(PassportUi.SwitchRow(loc["passport.friends.ranking"], null, social.ShowRanking, on =>
        {
            social.ShowRanking = on;
            Dispatcher.UIThread.Post(RefreshPassportSheet);
        }));
        var me = new PassportFriend(loc["passport.duel.you"], _passport.Goals.CollectorNo, _passport.Goals.Title, "", long.MaxValue);
        var myScore = _passport.Progress.Values.Sum(p => p.DuelLevel);
        int Count(PassportFriend f) => ReferenceEquals(f, me) ? _passport.Count : f.CountIn(ColituPassport.Stamps);
        int Score(PassportFriend f) => ReferenceEquals(f, me) ? myScore : f.Score;
        var rows = social.ShowRanking ? friends.Append(me).OrderByDescending(Count).ThenByDescending(Score).ToList() : friends.ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var f = rows[i];
            var isMe = ReferenceEquals(f, me);
            var row = PassportUi.Columns("Auto,*,Auto,Auto");
            if (social.ShowRanking)
            {
                row.Children.Add(new TextBlock { Text = $"{i + 1}", Width = 26, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = i == 0 ? PassportUi.Lilac : PassportUi.Muted, VerticalAlignment = VerticalAlignment.Center });
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var label = f.Nick.Trim().Length > 0 ? f.Nick : loc.Format("passport.id.collector", ("n", f.Collector));
            text.Children.Add(PassportUi.Label(label, 14, PassportUi.Text, FontWeight.SemiBold, wrap: false));
            if (PassportAchievement.Of(PassportIds.Normalize(f.Title)) is { } title) text.Children.Add(PassportUi.Label(title.Title, 12, PassportUi.Lilac, wrap: false));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var count = new TextBlock { Text = $"{Count(f)}/10", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = PassportUi.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(count, 2);
            row.Children.Add(count);
            if (!isMe)
            {
                var collector = f.Collector;
                var remove = PassportUi.Round(PassportUi.Icon("x", PassportUi.Dim, 12), () =>
                {
                    social.RemoveFriend(collector);
                    RefreshPassportSheet();
                }, $"{loc["passport.friends.remove"]}: {label}");
                remove.Width = remove.Height = 32;
                remove.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(remove, 3);
                row.Children.Add(remove);
            }
            Control item;
            if (isMe)
            {
                item = new Border { Background = PassportUi.LilacWell, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 10), Child = row };
            }
            else
            {
                var friend = f;
                var button = PassportUi.Plain(row, () =>
                {
                    _duelFriend = DuelPassport.From(friend);
                    RefreshPassportSheet();
                }, label);
                button.Background = PassportUi.Solid(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
                button.Padding = new Thickness(12, 10);
                item = button;
            }
            item.Margin = new Thickness(0, 4);
            panel.Children.Add(item);
        }
        var local = PassportUi.Label(loc["passport.friends.local"], 11.5, PassportUi.Dim, align: TextAlignment.Center);
        local.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(local);
        return panel;
    }
}

using Avalonia.Automation;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Passport rewards (the Windows app's ColituMainWindow.PassportReveal.cs): the server's unseen
/// events play one after another while the window is open — the cinematic reveal for a new stamp
/// or level, the flying piece, the seal that lands, the turning achievement card, chapters,
/// expeditions and the Companion stamp — and each is acked once shown. With the desktop's
/// animations off they appear at once. Also the first-visit intro and the story-sized share card.
/// </summary>
public partial class ColituMainWindow
{
    private readonly List<StampEvent> _revealQueue = [];
    /// <summary>Server id of each queued reward (same order as <see cref="_revealQueue"/>), acked once it was shown.</summary>
    private readonly List<long> _revealIds = [];
    private long _revealQueuedThrough;
    private bool _revealShowing;
    private Action? _revealDone;
    private DispatcherTimer? _revealAuto;

    /// <summary>Adds the server's unseen events (oldest first) that are not queued yet; unknown kinds are acked silently.</summary>
    private void QueuePassportEvents(IReadOnlyList<PassportServerEvent> events)
    {
        foreach (var e in events.Where(e => e.Id > _revealQueuedThrough))
        {
            _revealQueuedThrough = e.Id;
            if (e.Event == null)
            {
                _ = PassportSync.AckAsync(e.Id);
                continue;
            }
            _revealQueue.Add(e.Event);
            _revealIds.Add(e.Id);
        }
    }

    /// <summary>Plays the queued rewards while the window is on screen.</summary>
    private void PollPassportRewards()
    {
        try
        {
            if (_revealShowing || !IsVisible || WindowState == WindowState.Minimized || !AppView.IsVisible)
            {
                return;
            }
            if (_revealQueue.Count > 0)
            {
                ShowNextReveal();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.PollPassportRewards", ex);
        }
    }

    private void DropRevealHead(bool ack)
    {
        if (_revealQueue.Count == 0) return;
        _revealQueue.RemoveAt(0);
        if (_revealIds.Count == 0) return;
        var id = _revealIds[0];
        _revealIds.RemoveAt(0);
        if (ack) _ = PassportSync.AckAsync(id);
    }

    private void ShowNextReveal()
    {
        while (_revealQueue.Count > 0)
        {
            var e = _revealQueue[0];
            // A Chapter 02 country before the chapter opens stays quiet; it plays when the chapter opens.
            if (e is StampEvent.Unlocked && ColituPassport.Chapter2.Any(s => s.Code == e.Code) && !_passport.Chapter2Open
                || e is StampEvent.Unlocked or StampEvent.Piece or StampEvent.LevelUp or StampEvent.Mastered && ColituPassport.StampOf(e.Code) == null)
            {
                DropRevealHead(ack: true);
                continue;
            }
            break;
        }
        if (_revealQueue.Count == 0) return;
        var next = _revealQueue[0];
        _revealShowing = true;
        _revealDone = () => DropRevealHead(ack: true);
        PassportRevealLayer.Children.Clear();
        PassportRevealLayer.IsVisible = true;
        try
        {
            switch (next)
            {
                case StampEvent.Piece piece:
                    PieceReveal(piece);
                    break;
                case StampEvent.Collected collected:
                    CollectionReveal(collected);
                    break;
                case StampEvent.Achieved achieved:
                    AchievementReveal(achieved);
                    break;
                case StampEvent.Chapter chapter:
                    ChapterReveal(chapter);
                    break;
                case StampEvent.Expedition expedition:
                    ExpeditionReveal(expedition);
                    break;
                case StampEvent.Special:
                    SpecialReveal();
                    break;
                default:
                    CinematicReveal(next);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.ShowNextReveal", ex);
            FinishReveal();
        }
    }

    private void FinishReveal()
    {
        _revealAuto?.Stop();
        _revealAuto = null;
        var done = _revealDone;
        _revealDone = null;
        done?.Invoke();
        PassportRevealLayer.Children.Clear();
        PassportRevealLayer.IsVisible = false;
        _revealShowing = false;
        ApplyPassportHome();
        if (_page == "passport" && !PassportSheetLayer.IsVisible) RenderPassport();
        if (_revealQueue.Count > 0)
        {
            Dispatcher.UIThread.Post(ShowNextReveal, DispatcherPriority.Background);
        }
    }

    // ── Building blocks ────────────────────────────────────────────────────

    private StackPanel RevealStage(string name, bool clickToClose)
    {
        var dim = new Border { Background = PassportUi.Solid(Color.FromArgb(0xF0, 0x05, 0x04, 0x0F)) };
        PassportRevealLayer.Children.Add(dim);
        PassportUi.Tween(0.29, 0, t => dim.Opacity = t);
        if (clickToClose)
        {
            dim.PointerPressed += (_, _) => FinishReveal();
        }
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460, Margin = new Thickness(28, 70, 28, 28) };
        AutomationProperties.SetName(column, name);
        AutomationProperties.SetLiveSetting(column, AutomationLiveSetting.Assertive);
        KeyboardNavigation.SetTabNavigation(column, KeyboardNavigationMode.Cycle);
        PassportRevealLayer.Children.Add(column);
        return column;
    }

    /// <summary>Fades and lifts an element in.</summary>
    private static void Rise(Control element, double begin, double seconds = 0.43, double offset = 24)
    {
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        PassportUi.Tween(seconds, begin, t =>
        {
            element.Opacity = t;
            shift.Y = (1 - t) * offset;
        });
    }

    /// <summary>Buttons at the end of a reward; usable once they are in.</summary>
    private StackPanel RevealButtons(double begin, params (string Text, Action Action, bool Primary)[] buttons)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 28, 0, 0), Width = 340, Spacing = 10 };
        foreach (var (text, action, primary) in buttons)
        {
            panel.Children.Add(primary ? PassportUi.Primary(text, action) : PassportUi.Secondary(text, action));
        }
        Rise(panel, begin, 0.4, 12);
        panel.IsEnabled = false;
        After(PassportUi.Motion ? begin + 0.2 : 0.05, () =>
        {
            panel.IsEnabled = true;
            if (panel.Children.Count > 0) panel.Children[^1].Focus(NavigationMethod.Tab);
        });
        return panel;
    }

    private void After(double seconds, Action action)
    {
        DispatcherTimer.RunOnce(() =>
        {
            if (PassportRevealLayer.IsVisible) action();
        }, TimeSpan.FromSeconds(Math.Max(0.01, seconds)));
    }

    private void AutoClose(double seconds)
    {
        _revealAuto = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _revealAuto.Tick += (_, _) => FinishReveal();
        _revealAuto.Start();
    }

    /// <summary>
    /// The reveal's big title in capitals. <paramref name="name"/>: an English name kept in every language
    /// (badge, set, chapter, Companion), upper-cased without the UI culture so Turkish does not turn it into "FİRST CONNECTİON".
    /// </summary>
    private static TextBlock RevealTitle(string text, double size = 30, bool name = false)
    {
        var title = PassportUi.Label(name ? text.ToUpperInvariant() : text.ToUpper(Loc.I.Culture), size, PassportUi.Text, FontWeight.Bold, TextAlignment.Center);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        return title;
    }

    /// <summary>A light burst and ring behind the art when the material lands.</summary>
    private static Panel Burst(Color accent, double size, double begin)
    {
        var grid = new Panel { Width = size, Height = size, IsHitTestVisible = false, RenderTransformOrigin = RelativePoint.Center, Opacity = 0 };
        grid.Children.Add(new Ellipse { Fill = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(0x73, accent.R, accent.G, accent.B), 0), new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1) } } });
        grid.Children.Add(new Ellipse { Stroke = PassportUi.Solid(Color.FromArgb(0x80, accent.R, accent.G, accent.B)), StrokeThickness = 2 });
        var scale = new ScaleTransform(0.4, 0.4);
        grid.RenderTransform = scale;
        PassportUi.Tween(0.85, begin, t =>
        {
            scale.ScaleX = scale.ScaleY = 0.4 + 0.6 * t;
            grid.Opacity = PassportUi.Motion ? 1 - 0.7 * t : 0.3;
        });
        return grid;
    }

    // ── New stamp / level / Diamond complete ──────────────────────────────

    private void CinematicReveal(StampEvent e)
    {
        var loc = Loc.I;
        var stamp = ColituPassport.StampOf(e.Code)!;
        var after = _passport.ProgressOf(stamp.Code);
        var levelUp = e is not StampEvent.Unlocked;
        var before = levelUp ? after with { Pieces = Math.Max(0, after.Pieces - 1) } : StampProgress.None;
        var country = PassportUi.Country(stamp.Code);
        var title = e switch
        {
            StampEvent.LevelUp up => loc.Format("passport.r.levelUnlocked", ("level", PassportUi.LevelName(up.Level))),
            StampEvent.Mastered => loc["passport.masteredShort"],
            _ => loc.Format("passport.r.unlocked", ("country", country))
        };
        var accent = after.Level is { } l ? PassportUi.LevelColor(l) : Color.FromRgb(0xC4, 0xB5, 0xFD);

        var column = RevealStage($"{title}. {country}", clickToClose: false);
        const double size = 280;
        var art = new Panel { Width = size, Height = size, RenderTransformOrigin = RelativePoint.Center };
        var burst = Burst(accent, 440, 1.44);
        burst.Margin = new Thickness(-80);
        art.Children.Add(burst);
        art.Children.Add(PassportUi.StampArt(stamp, before, size));
        var material = PassportUi.StampArt(stamp, after, size);
        art.Children.Add(material);
        var glintMove = new TranslateTransform(-60, 0);
        var glint = new Rectangle
        {
            Width = 52,
            HorizontalAlignment = HorizontalAlignment.Left,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), 0.75), new GradientStop(Colors.Transparent, 1) }
            },
            RenderTransform = glintMove,
            IsHitTestVisible = false,
            Opacity = 0
        };
        var glintHost = new Panel { Width = size, Height = size, OpacityMask = PassportUi.Mask(PassportUi.StampImageName(stamp.Code, after.Level)) };
        glintHost.Children.Add(glint);
        art.Children.Add(glintHost);
        column.Children.Add(art);

        var scale = new ScaleTransform();
        art.RenderTransform = scale;
        PassportUi.Tween(0.58, 0.19, t =>
        {
            art.Opacity = t;
            scale.ScaleY = 0.82 + 0.18 * t;
            if (!levelUp) scale.ScaleX = 0.82 + 0.18 * t;
        });
        if (levelUp)
        {
            // A level-up turns once while the new material comes in (a flat turn: width through zero).
            PassportUi.Tween(1.0, 0.48, t => scale.ScaleX = Math.Cos(t * 2 * Math.PI), t => t);
        }
        // The new material sweeps in from the left with a glint on its edge.
        material.Clip = new RectangleGeometry(new Rect(0, 0, 0, size));
        PassportUi.Tween(0.72, 0.77, t =>
        {
            material.Clip = new RectangleGeometry(new Rect(0, 0, size * t, size));
            glintMove.X = -40 + (size + 28) * t;
            glint.Opacity = t is > 0.01 and < 0.99 ? 1 : 0;
        }, t => t);

        var words = new StackPanel { Margin = new Thickness(0, 22, 0, 0), Opacity = 0 };
        words.Children.Add(PassportUi.Kicker(levelUp ? loc["passport.r.newLevel"] : loc["passport.r.newStamp"], PassportUi.Solid(accent), TextAlignment.Center));
        var big = RevealTitle(title);
        big.Margin = new Thickness(0, 8, 0, 6);
        words.Children.Add(big);
        words.Children.Add(PassportUi.Label($"{country} · {loc.Format("passport.stampNo", ("n", stamp.NumberText))}", 14, PassportUi.Solid(accent), align: TextAlignment.Center));
        Rise(words, 1.49);
        column.Children.Add(words);
        column.Children.Add(RevealButtons(1.97,
            (loc["passport.r.story"], () =>
            {
                SharePassportCard(stamp);
                FinishReveal();
            }, true),
            (loc["passport.r.add"], FinishReveal, false)));
    }

    // ── One puzzle piece ───────────────────────────────────────────────────

    private void PieceReveal(StampEvent.Piece e)
    {
        var loc = Loc.I;
        var stamp = ColituPassport.StampOf(e.Code)!;
        var progress = _passport.ProgressOf(stamp.Code);
        // The level the piece belongs to: a 12th piece completes the level that came before.
        var level = e.LevelPieces == StampProgress.PiecesPerLevel && !progress.Mastered && progress.Level is { } now && now > StampLevel.Bronze ? now - 1 : progress.Level ?? StampLevel.Bronze;
        var color = PassportUi.LevelColor(level);
        var filled = e.LevelPieces;

        var column = RevealStage($"{loc["passport.r.piece"]}. {PassportUi.Country(stamp.Code)}", clickToClose: true);
        const double size = 260;
        var art = new Panel { Width = size, Height = size };
        var shown = progress with { Pieces = (int)level * StampProgress.PiecesPerLevel + Math.Max(0, filled - 1), UnlockedAt = Math.Max(1, progress.UnlockedAt) };
        art.Children.Add(PassportUi.StampArt(stamp, shown, size, puzzle: true, piecesOverride: Math.Max(0, filled - 1)));
        var slot = PassportUi.SlotRect(size, PassportUi.PieceSlot(filled));
        var source = PassportUi.Image(PassportUi.StampImageName(stamp.Code, level));
        var k = source == null ? 1 : source.Size.Width / size;
        // The piece: the stamp's own pixels of that slot, flying up from below into place.
        var piece = new Rectangle
        {
            Width = slot.Width,
            Height = slot.Height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(slot.X, slot.Y, 0, 0),
            Fill = source == null ? null : new ImageBrush(source)
            {
                Stretch = Stretch.Fill,
                SourceRect = new RelativeRect(slot.X * k, slot.Y * k, slot.Width * k, slot.Height * k, RelativeUnit.Absolute)
            },
            Stroke = PassportUi.Solid(color),
            StrokeThickness = 1.5
        };
        var fly = new TranslateTransform();
        piece.RenderTransform = fly;
        art.Children.Add(piece);
        var flash = new Rectangle { Width = slot.Width, Height = slot.Height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = piece.Margin, Fill = Brushes.White, Opacity = 0, IsHitTestVisible = false };
        art.Children.Add(flash);
        column.Children.Add(art);
        PassportUi.Tween(0.7, 0.15, t =>
        {
            fly.Y = (1 - t) * (size - slot.Y + 120);
            piece.Opacity = Math.Min(1, t * 4);
        }, PassportUi.Back);
        if (PassportUi.Motion)
        {
            PassportUi.Tween(0.5, 0.85, t => flash.Opacity = t < 0.1 ? t * 6.5 : 0.65 * (1 - t), t => t);
        }

        var words = new StackPanel { Margin = new Thickness(0, 22, 0, 0), Opacity = 0 };
        words.Children.Add(PassportUi.Label(loc["passport.r.piece"], 34, PassportUi.Solid(color), FontWeight.Bold, TextAlignment.Center));
        words.Children.Add(PassportUi.Label($"{PassportUi.Country(stamp.Code)} · {PassportUi.LevelName(level)} · {loc.Format("passport.pieces", ("n", filled))}", 14, PassportUi.Muted, align: TextAlignment.Center));
        Rise(words, 0.85);
        column.Children.Add(words);
        var hint = PassportUi.Label(loc["passport.r.click"], 12.5, PassportUi.Dim, align: TextAlignment.Center);
        hint.Margin = new Thickness(0, 18, 0, 0);
        hint.Opacity = 0;
        Rise(hint, 1.45, 0.3, 0);
        column.Children.Add(hint);
        column.PointerPressed += (_, _) => FinishReveal();
        AutoClose(PassportUi.Motion ? 2.6 : 2.2);
    }

    // ── Collection complete ────────────────────────────────────────────────

    private void CollectionReveal(StampEvent.Collected e)
    {
        var loc = Loc.I;
        if (PassportCollection.Of(e.Code) is not { } set)
        {
            FinishReveal();
            return;
        }
        var color = PassportUi.SealColor(set);
        var column = RevealStage($"{loc["passport.r.setDone"]}: {set.Title}", clickToClose: false);
        var stage = new Panel { Width = 240, Height = 240 };
        var ring = new Ellipse { Stroke = PassportUi.Solid(color), StrokeThickness = 3, RenderTransformOrigin = RelativePoint.Center, Opacity = 0 };
        var ringScale = new ScaleTransform(0.5, 0.5);
        ring.RenderTransform = ringScale;
        stage.Children.Add(ring);
        var seal = PassportUi.Seal(set, true, 190);
        seal.RenderTransformOrigin = RelativePoint.Center;
        var sealScale = new ScaleTransform();
        seal.RenderTransform = new TransformGroup { Children = { sealScale, new RotateTransform(-12) } };
        stage.Children.Add(seal);
        column.Children.Add(stage);
        // The seal lands like a stamp ("thud"), then the ink ring spreads.
        PassportUi.Tween(0.36, 0.1, t =>
        {
            seal.Opacity = Math.Min(1, t * 3);
            sealScale.ScaleX = sealScale.ScaleY = 2.4 - 1.4 * t;
        }, PassportUi.Back);
        if (PassportUi.Motion)
        {
            PassportUi.Tween(0.7, 0.42, t =>
            {
                ringScale.ScaleX = ringScale.ScaleY = 0.5 + 0.85 * t;
                ring.Opacity = 0.9 * (1 - t);
            });
        }
        var words = new StackPanel { Margin = new Thickness(0, 18, 0, 0), Opacity = 0 };
        words.Children.Add(PassportUi.Kicker(loc["passport.r.setDone"], PassportUi.Solid(color), TextAlignment.Center));
        var big = RevealTitle(set.Title, name: true);
        big.Margin = new Thickness(0, 8, 0, 6);
        words.Children.Add(big);
        if (set.Cover is { } cover)
        {
            words.Children.Add(PassportUi.Label(loc.Format("passport.r.newCover", ("cover", loc[$"passport.cover.{cover.Key()}"])), 14, PassportUi.Solid(color), align: TextAlignment.Center));
        }
        Rise(words, 0.9);
        column.Children.Add(words);
        var buttons = new List<(string, Action, bool)>();
        if (set.Cover is { } useCover)
        {
            buttons.Add((loc["passport.r.useCover"], () =>
            {
                _ = SavePassportProfileAsync(coverId: useCover.Key());
                FinishReveal();
            }, true));
        }
        buttons.Add((loc["passport.r.great"], FinishReveal, set.Cover == null));
        column.Children.Add(RevealButtons(1.4, buttons.ToArray()));
    }

    // ── Achievement ────────────────────────────────────────────────────────

    private void AchievementReveal(StampEvent.Achieved e)
    {
        var loc = Loc.I;
        if (PassportAchievement.Of(e.Code) is not { } a)
        {
            FinishReveal();
            return;
        }
        var column = RevealStage($"{(a.Secret ? loc["passport.r.secretAch"] : loc["passport.r.ach"])}: {a.Title}", clickToClose: false);
        var back = PassportUi.Label("???", 40, PassportUi.Dim, FontWeight.Bold, TextAlignment.Center);
        back.VerticalAlignment = VerticalAlignment.Center;
        var front = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var badge = PassportUi.Badge(a, true, 96);
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        front.Children.Add(badge);
        var name = PassportUi.Label(a.Title, 14, PassportUi.Text, FontWeight.SemiBold, TextAlignment.Center);
        name.Margin = new Thickness(12, 10, 12, 0);
        front.Children.Add(name);
        var card = new Border
        {
            Width = 180,
            Height = 220,
            CornerRadius = new CornerRadius(24),
            Background = PassportUi.GlassFill,
            BorderBrush = PassportUi.Lilac,
            BorderThickness = new Thickness(1.5),
            RenderTransformOrigin = RelativePoint.Center,
            Child = PassportUi.Motion ? back : front
        };
        var flip = new ScaleTransform();
        card.RenderTransform = flip;
        column.Children.Add(card);
        PassportUi.Tween(0.3, 0.1, t => card.Opacity = t);
        if (PassportUi.Motion)
        {
            // The card turns from "???" to the achievement.
            PassportUi.Tween(0.52, 0.55, t =>
            {
                if (t >= 0.42 && card.Child == back) card.Child = front;
                flip.ScaleX = Math.Abs(Math.Cos(t * Math.PI));
            }, t => t);
        }
        var words = new StackPanel { Margin = new Thickness(0, 22, 0, 0), Opacity = 0 };
        words.Children.Add(PassportUi.Kicker(a.Secret ? loc["passport.r.secretAch"] : loc["passport.r.ach"], align: TextAlignment.Center));
        var big = RevealTitle(a.Title, 28, name: true);
        big.Margin = new Thickness(0, 8, 0, 6);
        words.Children.Add(big);
        words.Children.Add(PassportUi.Label(loc[$"passport.ach.{a.Id}"], 14, PassportUi.Muted, align: TextAlignment.Center));
        Rise(words, 1.2);
        column.Children.Add(words);
        column.Children.Add(RevealButtons(1.6,
            (loc["passport.ach.use"], () =>
            {
                _ = SavePassportProfileAsync(titleId: a.Id);
                FinishReveal();
            }, true),
            (loc["passport.r.great"], FinishReveal, false)));
    }

    // ── Chapter, expedition, Companion ─────────────────────────────────────

    private void ChapterReveal(StampEvent.Chapter e)
    {
        var loc = Loc.I;
        var column = RevealStage(loc[$"passport.ch{e.Code}"], clickToClose: false);
        var kicker = PassportUi.Kicker(loc["passport.r.chapterOpen"], align: TextAlignment.Center);
        kicker.Opacity = 0;
        Rise(kicker, 0.3);
        column.Children.Add(kicker);
        var big = RevealTitle(loc[$"passport.ch{e.Code}"], 32, name: true);
        big.Margin = new Thickness(0, 10, 0, 10);
        big.Opacity = 0;
        Rise(big, 0.55, 0.6, 30);
        column.Children.Add(big);
        var body = PassportUi.Label(loc[$"passport.r.chapterBody{e.Code}"], 14, PassportUi.Muted, align: TextAlignment.Center);
        body.Opacity = 0;
        Rise(body, 1.2);
        column.Children.Add(body);
        column.Children.Add(RevealButtons(1.8, (loc["passport.r.great"], () =>
        {
            if (int.TryParse(e.Code, out var chapter)) _passportChapter = chapter;
            FinishReveal();
        }, true)));
    }

    private void ExpeditionReveal(StampEvent.Expedition e)
    {
        var loc = Loc.I;
        if (ExpeditionGoal.Of(e.Code) is not { } goal)
        {
            FinishReveal();
            return;
        }
        var column = RevealStage($"{loc["passport.r.expedition"]}: {loc[$"passport.x.{goal.Id}"]}", clickToClose: true);
        var visa = VisaStamp(goal, 200);
        var scale = new ScaleTransform();
        visa.RenderTransform = new TransformGroup { Children = { scale, new RotateTransform(-8) } };
        visa.RenderTransformOrigin = RelativePoint.Center;
        column.Children.Add(visa);
        PassportUi.Tween(0.35, 0.1, t =>
        {
            visa.Opacity = Math.Min(1, t * 3);
            scale.ScaleX = scale.ScaleY = 1.8 - 0.8 * t;
        }, PassportUi.Back);
        var words = new StackPanel { Margin = new Thickness(0, 22, 0, 0), Opacity = 0 };
        words.Children.Add(PassportUi.Kicker(loc["passport.r.expedition"], align: TextAlignment.Center));
        var title = PassportUi.Label(loc[$"passport.x.{goal.Id}"], 18, PassportUi.Text, FontWeight.SemiBold, TextAlignment.Center);
        title.Margin = new Thickness(0, 8, 0, 4);
        words.Children.Add(title);
        words.Children.Add(PassportUi.Label(loc.Format("passport.week.total", ("n", _passport.Weekly.Total)), 12.5, PassportUi.Lilac, align: TextAlignment.Center));
        Rise(words, 0.6);
        column.Children.Add(words);
        column.PointerPressed += (_, _) => FinishReveal();
        AutoClose(2.4);
    }

    /// <summary>A rectangular ink visa with a dashed inner frame, the week and the goal's icon.</summary>
    private Panel VisaStamp(ExpeditionGoal goal, double size)
    {
        var grid = new Panel { Width = size, Height = size * 0.66 };
        grid.Children.Add(new Border { BorderBrush = PassportUi.Lilac, BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(10) });
        grid.Children.Add(new Rectangle { Margin = new Thickness(7), RadiusX = 6, RadiusY = 6, Stroke = PassportUi.Lilac, StrokeThickness = 1.2, StrokeDashArray = [5, 3] });
        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        column.Children.Add(PassportUi.Label(Loc.I["passport.week.visa"], size * 0.08, PassportUi.Lilac, FontWeight.Bold, TextAlignment.Center));
        column.Children.Add(PassportUi.Icon(PassportUi.ExpeditionIcon(goal), PassportUi.Lilac, size * 0.16));
        column.Children.Add(PassportUi.Label(_passport.Weekly.WeekKey(), size * 0.065, PassportUi.Lilac, align: TextAlignment.Center));
        grid.Children.Add(column);
        return grid;
    }

    private void SpecialReveal()
    {
        var loc = Loc.I;
        var column = RevealStage($"{loc["passport.r.special"]}: Companion", clickToClose: false);
        var stamp = PassportUi.CompanionStamp(true, 200);
        stamp.RenderTransformOrigin = RelativePoint.Center;
        var scale = new ScaleTransform();
        stamp.RenderTransform = scale;
        column.Children.Add(stamp);
        PassportUi.Tween(0.36, 0.1, t =>
        {
            stamp.Opacity = Math.Min(1, t * 3);
            scale.ScaleX = scale.ScaleY = 2.2 - 1.2 * t;
        }, PassportUi.Back);
        var words = new StackPanel { Margin = new Thickness(0, 22, 0, 0), Opacity = 0 };
        words.Children.Add(PassportUi.Kicker(loc["passport.r.special"], align: TextAlignment.Center));
        var big = RevealTitle("Companion", name: true);
        big.Margin = new Thickness(0, 8, 0, 6);
        words.Children.Add(big);
        words.Children.Add(PassportUi.Label(loc["passport.companion.body"], 14, PassportUi.Muted, align: TextAlignment.Center));
        Rise(words, 0.8);
        column.Children.Add(words);
        column.Children.Add(RevealButtons(1.3, (loc["passport.r.great"], FinishReveal, true)));
    }

    // ── First visit ────────────────────────────────────────────────────────

    /// <summary>The cover turns in, the passport is named, the three steps arrive one by one, then "Start". Shown once.</summary>
    private void ShowPassportIntro()
    {
        if (_revealShowing) return;
        var loc = Loc.I;
        _revealShowing = true;
        _revealDone = () => _passport.Social.IntroSeen = true;
        PassportRevealLayer.Children.Clear();
        PassportRevealLayer.IsVisible = true;
        PassportRevealLayer.Children.Add(new Border { Background = PassportUi.Solid(Color.FromRgb(0x0B, 0x0A, 0x22)) });
        PassportRevealLayer.Children.Add(new Image { Source = PassportUi.Image("passport_bg.jpg"), Stretch = Stretch.UniformToFill, Opacity = 0.7, IsHitTestVisible = false });
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 420, Margin = new Thickness(28, 70, 28, 28) };
        AutomationProperties.SetName(column, loc["passport.intro.title"]);
        PassportRevealLayer.Children.Add(column);

        var hero = new Panel { Height = 220 };
        hero.Children.Add(new Ellipse
        {
            Width = 260,
            Height = 260,
            Margin = new Thickness(0, -20),
            Fill = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(0x55, 0x9F, 0x8C, 0xFF), 0), new GradientStop(Color.FromArgb(0, 0x9F, 0x8C, 0xFF), 1) } }
        });
        var cover = PassportUi.Cover(_passport.Goals.ActiveCover(PassportPremium), 200);
        cover.RenderTransformOrigin = RelativePoint.Center;
        var turn = new ScaleTransform();
        cover.RenderTransform = turn;
        hero.Children.Add(cover);
        column.Children.Add(hero);
        PassportUi.Tween(0.9, 0, t =>
        {
            hero.Opacity = t;
            turn.ScaleX = 0.25 + 0.75 * t;
            turn.ScaleY = 0.8 + 0.2 * t;
        });

        var words = new StackPanel { Margin = new Thickness(0, 26, 0, 0), Opacity = 0 };
        words.Children.Add(PassportUi.Kicker("COLITU PASSPORT", align: TextAlignment.Center));
        var title = PassportUi.Label(loc["passport.intro.title"], 28, PassportUi.Text, FontWeight.SemiBold, TextAlignment.Center);
        title.Margin = new Thickness(0, 8, 0, 6);
        words.Children.Add(title);
        words.Children.Add(PassportUi.Label(loc["passport.intro.body"], 14, PassportUi.Muted, align: TextAlignment.Center));
        Rise(words, 0.78, 0.5, 20);
        column.Children.Add(words);

        var stepsBox = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        var index = 0;
        foreach (var (icon, top, bottom) in new[] { ("power", "passport.step1", "passport.step1b"), ("star", "passport.step2", "passport.step2b"), ("arrowUp", "passport.step3", "passport.step3b") })
        {
            var row = PassportUi.Columns("Auto,*,Auto");
            row.Children.Add(PassportUi.IconWell(icon, 36));
            var a = PassportUi.Label(loc[top], 14, PassportUi.Text, FontWeight.SemiBold);
            a.Margin = new Thickness(12, 0, 8, 0);
            a.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(a, 1);
            row.Children.Add(a);
            var b = PassportUi.Label(loc[bottom], 12.5, PassportUi.Lilac, wrap: false);
            b.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(b, 2);
            row.Children.Add(b);
            var card = new Border { Background = PassportUi.Solid(Color.FromArgb(0x14, 0x9F, 0x8C, 0xFF)), CornerRadius = new CornerRadius(16), Padding = new Thickness(12), Margin = new Thickness(0, 5), Child = row, Opacity = 0 };
            var shift = new TranslateTransform();
            card.RenderTransform = shift;
            PassportUi.Tween(0.42, 1.3 + index++ * 0.31, t =>
            {
                card.Opacity = t;
                shift.X = (1 - t) * 40;
            });
            stepsBox.Children.Add(card);
        }
        column.Children.Add(stepsBox);
        var start = RevealButtons(2.35, (loc["passport.intro.start"], FinishReveal, true));
        start.Width = double.NaN;
        column.Children.Add(start);
    }

    // ── Share card ─────────────────────────────────────────────────────────

    /// <summary>
    /// A story-sized card (1080 × 1920): one stamp in its material with "GOLD UNLOCKED", or the whole
    /// passport when <paramref name="stamp"/> is null, with the comparison QR. Saved under Pictures/Colitu.
    /// </summary>
    private void SharePassportCard(ColituStamp? stamp)
    {
        var loc = Loc.I;
        try
        {
            const int w = 1080;
            const int h = 1920;
            var typeface = new Typeface(Resource<FontFamily>("ColituFont"));
            var bold = new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.Bold);
            var medium = new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.Medium);
            using var bitmap = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            using (var dc = bitmap.CreateDrawingContext())
            {
                if (PassportUi.Image("passport_share_bg.jpg") is { } bg) dc.DrawImage(bg, new Rect(0, 0, w, h));
                void Text(string value, double y, double size, Color color, Typeface face)
                {
                    var text = new FormattedText(value, loc.Culture, FlowDirection.LeftToRight, face, size, PassportUi.Solid(color));
                    // Long titles shrink to fit the card.
                    for (var fit = size; text.Width > w - 120 && fit > 40;)
                    {
                        fit -= 4;
                        text = new FormattedText(value, loc.Culture, FlowDirection.LeftToRight, face, fit, PassportUi.Solid(color));
                    }
                    dc.DrawText(text, new Point((w - text.Width) / 2, y - text.Baseline));
                }
                var lilac = Color.FromRgb(0xC4, 0xB5, 0xFD);
                var white = Color.FromRgb(0xF4, 0xF3, 0xFA);
                var total = ColituPassport.Stamps.Count;
                Text("COLITU PASSPORT", 170, 40, lilac, medium);
                if (stamp != null)
                {
                    var progress = _passport.ProgressOf(stamp.Code);
                    if (PassportUi.Image(PassportUi.StampImageName(stamp.Code, progress.Level)) is { } art) dc.DrawImage(art, new Rect(220, 270, 640, 640));
                    var headline = progress.Level is { } level
                        ? loc.Format("passport.r.levelUnlocked", ("level", PassportUi.LevelName(level)))
                        : loc.Format("passport.r.unlocked", ("country", PassportUi.Country(stamp.Code)));
                    Text(headline.ToUpper(loc.Culture), 1080, 92, white, bold);
                    Text($"{PassportUi.Country(stamp.Code)} · {loc.Format("passport.stampNo", ("n", stamp.NumberText))}", 1170, 46, progress.Level is { } l ? PassportUi.LevelColor(l) : lilac, medium);
                }
                else
                {
                    if (PassportUi.Image($"passport_cover_{_passport.Goals.ActiveCover(PassportPremium).Key()}.png") is { } cover)
                    {
                        const double height = 780;
                        var width = height * cover.Size.Width / cover.Size.Height;
                        dc.DrawImage(cover, new Rect((w - width) / 2, 250, width, height));
                    }
                    Text((_passport.Complete ? loc["passport.complete"] : loc["passport.title"]).ToUpper(loc.Culture), 1170, 88, white, bold);
                }
                dc.DrawRectangle(PassportUi.Solid(Color.FromArgb(0x5A, 0x3A, 0x2F, 0x8F)), new Pen(PassportUi.Solid(Color.FromArgb(0x66, 0x9F, 0x8C, 0xFF)), 3), new Rect(120, 1300, 840, 240), 48, 48);
                Text($"{_passport.Count} / {total}", 1420, 92, white, bold);
                Text(loc["passport.s.collected"].ToUpper(loc.Culture), 1495, 34, lilac, medium);
                if (PassportUi.QrModules(_passport.Social.MyLink()) is { } m)
                {
                    const double side = 190;
                    var n = m.GetLength(0);
                    var cell = side / n;
                    var left = (w - side) / 2;
                    const double top = 1580;
                    dc.DrawRectangle(Brushes.White, null, new Rect(left - 14, top - 14, side + 28, side + 28), 18, 18);
                    var dot = PassportUi.Solid(Color.FromRgb(0x16, 0x15, 0x3A));
                    for (var x = 0; x < n; x++)
                    {
                        for (var y = 0; y < n; y++)
                        {
                            if (m[x, y]) dc.DrawRectangle(dot, null, new Rect(left + x * cell, top + y * cell, cell + 0.4, cell + 0.4));
                        }
                    }
                }
                Text(PassportIdentityLine(), 1832, 32, lilac, medium);
                Text(loc["passport.s.cta"], 1880, 32, Color.FromRgb(0xA9, 0xAD, 0xC2), medium);
            }
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures)) pictures = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            var folder = System.IO.Path.Combine(pictures, "Colitu");
            Directory.CreateDirectory(folder);
#pragma warning disable CS0618 // Save(path) writes PNG; the encoder-options overload adds nothing here.
            bitmap.Save(System.IO.Path.Combine(folder, $"colitu-passport-{DateTime.Now:yyyyMMdd-HHmmss}.png"));
#pragma warning restore CS0618
            _ = PassportSync.ReportShareAsync();
            ShowToast(loc["passport.shareSaved"]);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SharePassportCard", ex);
            ShowToast(loc["passport.shareFailed"], true);
        }
    }
}

using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media.Immutable;
using QRCoder;
using v2rayN.Desktop.Services;
using Path = Avalonia.Controls.Shapes.Path;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Building blocks of the Colitu Passport screens (the Windows app's ColituPassportUi.cs, colitu-android
/// screens/Passport*.kt): stamp art with its puzzle, covers, seals, badges, rings, bars, icons and a
/// small tween helper for the reveals.
/// </summary>
internal static class PassportUi
{
    public static readonly IBrush Text = new ImmutableSolidColorBrush(Color.FromRgb(0xF4, 0xF3, 0xFA));
    public static readonly IBrush Muted = new ImmutableSolidColorBrush(Color.FromRgb(0xA9, 0xAD, 0xC2));
    public static readonly IBrush Dim = new ImmutableSolidColorBrush(Color.FromRgb(0x6F, 0x71, 0x86));
    public static readonly IBrush Lilac = new ImmutableSolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD));
    public static readonly IBrush Line = new ImmutableSolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush Glass10 = new ImmutableSolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush Tile = new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush LilacWell = new ImmutableSolidColorBrush(Color.FromArgb(0x26, 0x9F, 0x8C, 0xFF));
    public static readonly IBrush LilacSoft = new ImmutableSolidColorBrush(Color.FromArgb(0x33, 0x9F, 0x8C, 0xFF));
    public static readonly IBrush GlassEdge = new ImmutableSolidColorBrush(Color.FromArgb(0x33, 0x9F, 0x8C, 0xFF));

    public static IBrush Accent => Gradient(Color.FromRgb(0xB4, 0xA4, 0xFF), Color.FromRgb(0x8B, 0x78, 0xFF), horizontal: true);

    public static IBrush GlassFill => Gradient(Color.FromArgb(0x22, 0x9F, 0x8C, 0xFF), Color.FromArgb(0x0F, 0x9F, 0x8C, 0xFF), horizontal: false);

    public static IBrush Gradient(Color from, Color to, bool horizontal) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = horizontal ? new RelativePoint(1, 0, RelativeUnit.Relative) : new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) }
    };

    public static IBrush Solid(Color color) => new ImmutableSolidColorBrush(color);

    // ── Motion ───────────────────────────────────────────────────────────

    private static readonly Lazy<bool> MotionSetting = new(ReadMotionSetting);

    /// <summary>The desktop's "animations" setting (GNOME enable-animations); on when it cannot be read.</summary>
    public static bool Motion => MotionSetting.Value;

    private static bool ReadMotionSetting()
    {
        try
        {
            var start = new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface enable-animations")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(start);
            if (process == null || !process.WaitForExit(1500)) return true;
            return !process.StandardOutput.ReadToEnd().Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Drives <paramref name="apply"/> from 0 to 1 over <paramref name="seconds"/> after
    /// <paramref name="delay"/> (cubic ease-out unless <paramref name="ease"/>); without motion it jumps to 1.
    /// </summary>
    public static void Tween(double seconds, double delay, Action<double> apply, Func<double, double>? ease = null)
    {
        if (!Motion)
        {
            apply(1);
            return;
        }
        apply(0);
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var t = (watch.Elapsed.TotalSeconds - delay) / Math.Max(0.01, seconds);
            if (t < 0) return;
            var done = t >= 1;
            t = Math.Clamp(t, 0, 1);
            apply(ease != null ? ease(t) : 1 - Math.Pow(1 - t, 3));
            if (done) timer.Stop();
        };
        timer.Start();
    }

    /// <summary>Overshoot ease (the stamp "thud").</summary>
    public static double Back(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    // ── Levels ───────────────────────────────────────────────────────────

    public static Color LevelColor(StampLevel level) => level switch
    {
        StampLevel.Bronze => Color.FromRgb(0xDD, 0xA2, 0x7A),
        StampLevel.Silver => Color.FromRgb(0xD3, 0xDA, 0xE8),
        StampLevel.Gold => Color.FromRgb(0xF2, 0xC7, 0x6E),
        _ => Color.FromRgb(0xC4, 0xB5, 0xFD)
    };

    public static IBrush LevelBrush(StampLevel level) => Solid(LevelColor(level));

    public static string LevelName(StampLevel level) => Loc.I[$"passport.level.{level.Key()}"];

    public static string Country(string code) => ColituServerNames.CountryName(code) ?? code;

    // ── Images ───────────────────────────────────────────────────────────

    private static readonly Dictionary<string, Bitmap?> Images = [];

    public static Bitmap? Image(string name)
    {
        if (Images.TryGetValue(name, out var cached)) return cached;
        Bitmap? image = null;
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://ColituVPN/Assets/Passport/{name}"));
            image = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"PassportUi.Image {name}", ex);
        }
        Images[name] = image;
        return image;
    }

    public static string StampImageName(string code, StampLevel? level) =>
        $"stamp_{code.ToLowerInvariant()}_{level?.Key() ?? "locked"}.png";

    public static Image Picture(string name, double width, double height = double.NaN)
    {
        var image = new Image { Source = Image(name), Width = width, Stretch = Stretch.Uniform };
        if (!double.IsNaN(height)) image.Height = height;
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        return image;
    }

    public static Image Cover(CoverTheme theme, double height)
    {
        var image = Picture($"passport_cover_{theme.Key()}.png", height * 720 / 900, height);
        return image;
    }

    public static ImageBrush? Mask(string name) => Image(name) is { } bitmap ? new ImageBrush(bitmap) { Stretch = Stretch.Uniform } : null;

    // ── Stamp art ────────────────────────────────────────────────────────

    /// <summary>Fixed order in which the 12 puzzle pieces fill (row-major indices).</summary>
    public static readonly int[] PieceOrder = [4, 0, 11, 6, 2, 9, 7, 1, 10, 3, 8, 5];

    public static int PieceSlot(int filled) => PieceOrder[Math.Clamp(filled - 1, 0, PieceOrder.Length - 1)];

    /// <summary>One stamp: grey and locked, or in its level's material; <paramref name="puzzle"/> shades the missing pieces.</summary>
    public static Panel StampArt(ColituStamp stamp, StampProgress progress, double size, double lockSize = 0, bool puzzle = false, int? piecesOverride = null)
    {
        var level = progress.Level;
        var name = StampImageName(stamp.Code, level);
        var grid = new Panel { Width = size, Height = size };
        var picture = Picture(name, size, size);
        AutomationProperties.SetName(picture, Country(stamp.Code));
        grid.Children.Add(picture);
        if (puzzle && level != null && !progress.Mastered)
        {
            var canvas = PuzzleOverlay(size, piecesOverride ?? progress.LevelPieces);
            canvas.OpacityMask = Mask(name);
            grid.Children.Add(canvas);
        }
        if (level == null && lockSize > 0)
        {
            grid.Children.Add(LockBadge(lockSize));
        }
        return grid;
    }

    public static Panel LockBadge(double size)
    {
        var badge = new Panel { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        badge.Children.Add(new Ellipse { Fill = Solid(Color.FromArgb(0xCC, 0x14, 0x14, 0x1B)), Stroke = Solid(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 });
        badge.Children.Add(Icon("lock", Text, size * 0.46));
        return badge;
    }

    /// <summary>3 × 4 jigsaw over the stamp; missing pieces shaded.</summary>
    public static Canvas PuzzleOverlay(double size, int filled)
    {
        var canvas = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
        var inset = size * 0.06;
        var w = size - inset * 2;
        var h = size - inset * 2;
        var cw = w / 3;
        var ch = h / 4;
        var done = PieceOrder.Take(filled).ToHashSet();
        var shade = Solid(Color.FromArgb(0xC7, 0x05, 0x04, 0x0F));
        for (var i = 0; i < StampProgress.PiecesPerLevel; i++)
        {
            if (done.Contains(i)) continue;
            var rect = new Rectangle { Width = cw + 0.5, Height = ch + 0.5, Fill = shade };
            Canvas.SetLeft(rect, inset + i % 3 * cw);
            Canvas.SetTop(rect, inset + i / 3 * ch);
            canvas.Children.Add(rect);
        }
        canvas.Children.Add(new Path { Data = Jigsaw(inset, w, h), Stroke = Solid(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.3 });
        return canvas;
    }

    public static Rect SlotRect(double size, int slot)
    {
        var inset = size * 0.06;
        var cw = (size - inset * 2) / 3;
        var ch = (size - inset * 2) / 4;
        return new Rect(inset + slot % 3 * cw, inset + slot / 3 * ch, cw, ch);
    }

    private static Geometry Jigsaw(double o, double w, double h)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var cw = w / 3;
            var ch = h / 4;
            var k = Math.Min(cw, ch) * 0.15;
            for (var i = 1; i < 3; i++)
            {
                var x = o + cw * i;
                for (var j = 0; j < 4; j++)
                {
                    var y0 = o + ch * j;
                    var ym = y0 + ch / 2;
                    var d = (i + j) % 2 == 0 ? 1 : -1;
                    g.BeginFigure(new Point(x, y0), false);
                    g.LineTo(new Point(x, ym - k));
                    g.CubicBezierTo(new Point(x + d * k * 2.4, ym - k * 1.9), new Point(x + d * k * 2.4, ym + k * 1.9), new Point(x, ym + k));
                    g.LineTo(new Point(x, y0 + ch));
                    g.EndFigure(false);
                }
            }
            for (var j = 1; j < 4; j++)
            {
                var y = o + ch * j;
                for (var i = 0; i < 3; i++)
                {
                    var x0 = o + cw * i;
                    var xm = x0 + cw / 2;
                    var d = (i + j) % 2 == 0 ? -1 : 1;
                    g.BeginFigure(new Point(x0, y), false);
                    g.LineTo(new Point(xm - k, y));
                    g.CubicBezierTo(new Point(xm - k * 1.9, y + d * k * 2.4), new Point(xm + k * 1.9, y + d * k * 2.4), new Point(xm + k, y));
                    g.LineTo(new Point(x0 + cw, y));
                    g.EndFigure(false);
                }
            }
        }
        return geometry;
    }

    // ── Seals, badges, Companion ─────────────────────────────────────────

    public static Control Seal(PassportCollection set, bool earned, double size)
    {
        if (set.Secret && !earned) return QuestionMark(size, Dim);
        var image = Picture($"seal_{set.Id}{(earned ? "" : "_grey")}.png", size, size);
        image.Opacity = earned ? 1 : 0.4;
        AutomationProperties.SetName(image, set.Title);
        return image;
    }

    public static Control Badge(PassportAchievement a, bool earned, double size)
    {
        if (a.Secret && !earned) return QuestionMark(size, Dim);
        var image = Picture($"badge_{a.Id}{(earned ? "" : "_grey")}.png", size, size);
        image.Opacity = earned ? 1 : 0.35;
        AutomationProperties.SetName(image, a.Title);
        return image;
    }

    public static Panel QuestionMark(double size, IBrush brush)
    {
        var grid = new Panel { Width = size, Height = size };
        grid.Children.Add(new Ellipse { Fill = Solid(Color.FromRgb(0x1C, 0x1C, 0x25)), Stroke = Solid(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.5 });
        grid.Children.Add(new TextBlock { Text = "?", FontSize = size * 0.4, FontWeight = FontWeight.Bold, Foreground = brush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return grid;
    }

    /// <summary>The Companion stamp: two linked holders in a perforated lilac frame.</summary>
    public static Panel CompanionStamp(bool earned, double size)
    {
        var color = earned ? Lilac : Dim;
        var grid = new Panel { Width = size, Height = size, Opacity = earned ? 1 : 0.45 };
        var inset = size * 0.1;
        grid.Children.Add(new Rectangle { Margin = new Thickness(inset), RadiusX = 4, RadiusY = 4, Fill = Solid(Color.FromRgb(0x1B, 0x18, 0x40)), Stroke = color, StrokeThickness = 3 });
        grid.Children.Add(new Rectangle { Margin = new Thickness(inset + 6), RadiusX = 3, RadiusY = 3, Stroke = color, StrokeThickness = 1, Opacity = 0.5, StrokeDashArray = new AvaloniaList<double> { 3, 2 } });
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var people = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        people.Children.Add(Icon("person", color, size * 0.22));
        people.Children.Add(Icon("person", color, size * 0.22));
        column.Children.Add(people);
        column.Children.Add(new TextBlock { Text = "COMPANION", Margin = new Thickness(0, 4, 0, 0), FontSize = Math.Max(8, size * 0.09), FontWeight = FontWeight.SemiBold, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center });
        grid.Children.Add(column);
        return grid;
    }

    // ── Rings and bars ───────────────────────────────────────────────────

    public static Panel Ring(double fraction, double size, double stroke, Control? content)
    {
        var grid = new Panel { Width = size, Height = size };
        grid.Children.Add(new Ellipse { Stroke = Glass10, StrokeThickness = stroke });
        var arc = new Path { Stroke = Accent, StrokeThickness = stroke, StrokeLineCap = PenLineCap.Round, Data = Arc(size, stroke, fraction) };
        grid.Children.Add(arc);
        if (content != null)
        {
            content.HorizontalAlignment = HorizontalAlignment.Center;
            content.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(content);
        }
        return grid;
    }

    private static Geometry Arc(double size, double stroke, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 0.9999);
        var geometry = new StreamGeometry();
        if (fraction <= 0) return geometry;
        var r = (size - stroke) / 2;
        var c = size / 2;
        var angle = fraction * 2 * Math.PI;
        using var g = geometry.Open();
        g.BeginFigure(new Point(c, c - r), false);
        g.ArcTo(new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle)), new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise);
        g.EndFigure(false);
        return geometry;
    }

    public static Panel Bar(double fraction, double height, IBrush fill, double width = double.NaN)
    {
        var grid = new Panel { Height = height, Width = width };
        grid.Children.Add(new Border { Background = Glass10, CornerRadius = new CornerRadius(height / 2) });
        var bar = new Border { Background = fill, CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left };
        grid.SizeChanged += (_, e) => bar.Width = Math.Max(0, e.NewSize.Width * Math.Clamp(fraction, 0, 1));
        grid.Children.Add(bar);
        return grid;
    }

    public static UniformGrid SegmentBar(int filled, Color color)
    {
        var grid = new UniformGrid { Rows = 1, Columns = StampProgress.PiecesPerLevel };
        var on = Solid(color);
        for (var i = 0; i < StampProgress.PiecesPerLevel; i++)
        {
            grid.Children.Add(new Border { Height = 6, Margin = new Thickness(1.5, 0), CornerRadius = new CornerRadius(3), Background = i < filled ? on : Glass10 });
        }
        AutomationProperties.SetName(grid, Loc.I.Format("passport.pieces", ("n", filled)));
        return grid;
    }

    // ── Text and controls ────────────────────────────────────────────────

    public static TextBlock Label(string text, double size = 14, IBrush? brush = null, FontWeight? weight = null, TextAlignment align = TextAlignment.Left, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush ?? Text,
        FontWeight = weight ?? FontWeight.Normal,
        TextAlignment = align,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
    };

    public static TextBlock Kicker(string text, IBrush? brush = null, TextAlignment align = TextAlignment.Left) =>
        Label(text.ToUpper(Loc.I.Culture), 11.5, brush ?? Lilac, FontWeight.SemiBold, align, false);

    private static ControlTheme Theme(string key) =>
        Application.Current!.TryFindResource(key, Application.Current.ActualThemeVariant, out var value) && value is ControlTheme theme
            ? theme
            : throw new KeyNotFoundException(key);

    public static Control Glass(Control content, Action? onClick = null, string? name = null, Thickness? padding = null)
    {
        if (onClick == null)
        {
            return new Border { Background = GlassFill, BorderBrush = GlassEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(22), Padding = padding ?? new Thickness(18), Child = content };
        }
        var button = Plain(content, onClick, name);
        button.Background = GlassFill;
        button.BorderBrush = GlassEdge;
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(22);
        button.Padding = padding ?? new Thickness(18);
        return button;
    }

    public static Button Plain(object content, Action onClick, string? name = null)
    {
        var button = new Button { Content = content, Theme = Theme("PassportPlain") };
        button.Click += (_, _) => onClick();
        if (name != null) AutomationProperties.SetName(button, name);
        return button;
    }

    public static Button Round(Control content, Action onClick, string name)
    {
        var button = new Button { Content = content, Theme = Theme("PassportRound") };
        button.Click += (_, _) => onClick();
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        return button;
    }

    public static Button Primary(string text, Action onClick, double height = 48)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 14.5, TextTrimming = TextTrimming.CharacterEllipsis },
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Theme = Theme("PrimaryButton")
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Button Secondary(string text, Action onClick, double height = 46)
    {
        var button = Primary(text, onClick, height);
        button.Theme = Theme("SecondaryButton");
        return button;
    }

    public static Button Link(string text, Action onClick)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Center, Theme = Theme("LinkButton") };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static RadioButton Chip(object content, string group, bool selected, string name)
    {
        var chip = new RadioButton { Content = content, GroupName = group, IsChecked = selected, Theme = Theme("PassportChip") };
        AutomationProperties.SetName(chip, name);
        return chip;
    }

    public static RadioButton ChapterTab(object content, bool selected, string name)
    {
        var tab = new RadioButton { Content = content, GroupName = "PassportChapter", IsChecked = selected, Theme = Theme("PassportChapterTab"), Margin = new Thickness(10, 0) };
        AutomationProperties.SetName(tab, name);
        return tab;
    }

    /// <summary>The app's toggle switch with a label; the label names the switch for screen readers.</summary>
    public static Grid SwitchRow(string label, string? body, bool value, Action<bool> onChange)
    {
        var row = new Grid { Margin = new Thickness(0, 6), MinHeight = 40, ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Label(label, 14, Text, FontWeight.SemiBold));
        if (body != null) text.Children.Add(Label(body, 12, Muted));
        row.Children.Add(text);
        var toggle = new CheckBox { IsChecked = value, Theme = Theme("Switch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        AutomationProperties.SetName(toggle, label);
        toggle.IsCheckedChanged += (_, _) => onChange(toggle.IsChecked == true);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    public static Grid Columns(string definitions) => new() { ColumnDefinitions = new ColumnDefinitions(definitions) };

    // ── Icons (24 × 24 strokes) ──────────────────────────────────────────

    private static readonly Dictionary<string, string> IconData = new()
    {
        ["power"] = "M12 3v8 M6.3 6.7a8 8 0 1 0 11.4 0",
        ["star"] = "M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1-4.4-4.3 6.1-.9z",
        ["arrowUp"] = "M12 20V5 M6 11l6-6 6 6",
        ["grid"] = "M4 4h6.5v6.5H4z M13.5 4H20v6.5h-6.5z M4 13.5h6.5V20H4z M13.5 13.5H20V20h-6.5z",
        ["check"] = "M5 12.5l4.5 4.5L19 7.5",
        ["lock"] = "M6 11h12v10H6z M8.5 11V8a3.5 3.5 0 0 1 7 0v3",
        ["send"] = "M21 3L10.5 13.5 M21 3l-6.5 18-4-7.5-7.5-4z",
        ["sealCheck"] = "M12 2.8l2.2 1.6 2.7-.1.8 2.6 2.2 1.6-.9 2.6.9 2.6-2.2 1.6-.8 2.6-2.7-.1L12 21.2l-2.2-1.6-2.7.1-.8-2.6-2.2-1.6.9-2.6-.9-2.6 2.2-1.6.8-2.6 2.7.1z M8.6 12.2l2.3 2.3 4.6-4.8",
        ["globe"] = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z M3 12h18 M12 3c2.5 2.6 3.8 5.6 3.8 9s-1.3 6.4-3.8 9c-2.5-2.6-3.8-5.6-3.8-9s1.3-6.4 3.8-9z",
        ["person"] = "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8z M4.5 20.5c1.2-3.6 4-5.5 7.5-5.5s6.3 1.9 7.5 5.5",
        ["eye"] = "M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z",
        ["bolt"] = "M13 2L4.5 13.5H12L11 22l8.5-11.5H12z",
        ["shield"] = "M12 2.5L4.5 5.5v6c0 5 3.3 9 7.5 10.5 4.2-1.5 7.5-5.5 7.5-10.5v-6z M8.8 12l2.2 2.2 4.4-4.6",
        ["arrowUpRight"] = "M7 17L17 7 M8 7h9v9",
        ["chevron"] = "M9 5l7 7-7 7",
        ["back"] = "M15 5l-7 7 7 7",
        ["qr"] = "M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h2v2h-2z M18 18h2v2h-2z M14 18h2 M18 14h2",
        ["info"] = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z M12 11v6 M12 7.5v.5",
        ["x"] = "M6 6l12 12 M18 6L6 18"
    };

    public static Path Icon(string name, IBrush brush, double size) => new()
    {
        Data = StreamGeometry.Parse(IconData.GetValueOrDefault(name, IconData["star"])),
        Stroke = brush,
        StrokeThickness = 1.8,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        Stretch = Stretch.Uniform,
        Width = size,
        Height = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };

    public static Panel IconWell(string icon, double size)
    {
        var grid = new Panel { Width = size, Height = size };
        grid.Children.Add(new Ellipse { Fill = LilacWell });
        grid.Children.Add(Icon(icon, Lilac, size * 0.45));
        return grid;
    }

    public static string ExpeditionIcon(ExpeditionGoal goal) => goal.Id switch
    {
        "new_country" => "globe",
        "share" => "send",
        "customize" => "person",
        "days10" => "power",
        "set_stamp" => "grid",
        _ => "star"
    };

    public static Color SealColor(PassportCollection set) => set.Id switch
    {
        "nordic" => Color.FromRgb(0x7F, 0xD6, 0xE0),
        "baltic" => Color.FromRgb(0xE8, 0xB0, 0x67),
        "atlantic" => Color.FromRgb(0x8F, 0xB2, 0xFF),
        "pacific" => Color.FromRgb(0xFF, 0xA9, 0x8A),
        "gold_rush" => Color.FromRgb(0xF2, 0xC7, 0x6E),
        _ => Color.FromRgb(0xC4, 0xB5, 0xFD)
    };

    // ── QR ───────────────────────────────────────────────────────────────

    /// <summary>The QR modules of <paramref name="text"/> (true = dark), quiet zone included.</summary>
    public static bool[,]? QrModules(string text)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var rows = data.ModuleMatrix;
            var n = rows.Count;
            var modules = new bool[n, n];
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++) modules[x, y] = rows[y][x];
            }
            return modules;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("PassportUi.QrModules", ex);
            return null;
        }
    }

    /// <summary>The QR as a crisp vector image (white quiet zone, indigo modules).</summary>
    public static IImage? QrImage(string text)
    {
        if (QrModules(text) is not { } m) return null;
        var n = m.GetLength(0);
        var geometry = new GeometryGroup();
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                if (m[x, y]) geometry.Children.Add(new RectangleGeometry(new Rect(x, y, 1, 1)));
            }
        }
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing { Brush = Brushes.White, Geometry = new RectangleGeometry(new Rect(0, 0, n, n)) });
        group.Children.Add(new GeometryDrawing { Brush = Solid(Color.FromRgb(0x16, 0x15, 0x3A)), Geometry = geometry });
        return new DrawingImage(group);
    }
}

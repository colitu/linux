using System.Diagnostics;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace v2rayN.Desktop.Views;

/// <summary>Colour set of the ColituGecko label: Dark for dark surfaces, Light for light ones, Auto follows the theme variant.</summary>
public enum ColituGeckoPalette
{
    Auto,
    Dark,
    Light,
}

/// <summary>
/// Whether the desktop asks for less motion. Avalonia has no such setting of its own, so on Linux this
/// reads GNOME's "enable-animations" (gsettings, once, in the background; other desktops answer
/// "animated") and the COLITU_REDUCE_MOTION=1 environment variable.
/// </summary>
internal static class ColituReducedMotion
{
    private static volatile bool _detected;
    private static int _started;

    /// <summary>Tests: true/false forces reduced motion on/off; null follows the desktop.</summary>
    public static bool? Override { get; set; }

    public static event Action? Changed;

    public static bool IsOn
    {
        get
        {
            if (Override is { } forced)
            {
                return forced;
            }
            Start();
            return _detected;
        }
    }

    private static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        if (Environment.GetEnvironmentVariable("COLITU_REDUCE_MOTION") == "1")
        {
            _detected = true;
            return;
        }
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var info = new ProcessStartInfo("gsettings", ["get", "org.gnome.desktop.interface", "enable-animations"])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(info);
                if (process == null)
                {
                    return;
                }
                if (!process.WaitForExit(2000))
                {
                    process.Kill();
                    return;
                }
                var off = process.StandardOutput.ReadToEnd().Trim() == "false";
                if (off != _detected)
                {
                    _detected = off;
                    Changed?.Invoke();
                }
            }
            catch
            {
                // No gsettings (not GNOME, a minimal system): animated.
            }
        });
    }
}

/// <summary>
/// The word "ColituGecko" as a brand-gradient text that slowly slides across the letters (3.5 s a
/// loop), plus an optional breathing dot (opacity 0.45 to 1, scale 0.85 to 1, 2 s). Only this label
/// is animated, in the app's own font. It animates only while it is visible in a window that is not
/// minimised, and not at all when the desktop asks for reduced motion (static gradient and dot).
/// </summary>
public sealed class ColituGeckoLabel : StackPanel
{
    public static readonly StyledProperty<ColituGeckoPalette> PaletteProperty =
        AvaloniaProperty.Register<ColituGeckoLabel, ColituGeckoPalette>(nameof(Palette), ColituGeckoPalette.Auto);

    public static readonly StyledProperty<bool> ShowDotProperty =
        AvaloniaProperty.Register<ColituGeckoLabel, bool>(nameof(ShowDot), true);

    public const double SlideSeconds = 3.5;
    /// <summary>One breath: 1 s up and 1 s back.</summary>
    public const double BreathSeconds = 2.0;
    private const double DotSize = 6.5;

    private readonly TextBlock _text = new() { Text = "ColituGecko", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Ellipse _dot;
    private readonly LinearGradientBrush _brush = new() { SpreadMethod = GradientSpreadMethod.Repeat };
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private Window? _window;
    private bool _attached;

    public ColituGeckoLabel()
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal;
        _dot = new Ellipse
        {
            Width = DotSize,
            Height = DotSize,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = _scale
        };
        Children.Add(_dot);
        Children.Add(_text);
        _text.Foreground = _brush;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Apply(_clock.Elapsed);
        Rebuild();
    }

    public ColituGeckoPalette Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    public bool ShowDot
    {
        get => GetValue(ShowDotProperty);
        set => SetValue(ShowDotProperty, value);
    }

    /// <summary>The gradient text brush (the word's Foreground).</summary>
    public IBrush? TextBrush => _text.Foreground;

    /// <summary>The word itself (tests and automation).</summary>
    public TextBlock Word => _text;

    /// <summary>The dot (tests and automation).</summary>
    public Ellipse Dot => _dot;

    /// <summary>True while the frame timer runs.</summary>
    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Current gradient slide, 0 to 1 of one loop.</summary>
    public double SlideOffset { get; private set; }

    public static bool ReducedMotion => ColituReducedMotion.IsOn;

    /// <summary>Darkest stop, light stop and dot colour (dark = on a dark surface).</summary>
    public static (Color Deep, Color Light, Color Dot) Colors(bool dark) => dark
        ? (Color.FromRgb(0x8F, 0x7C, 0xF0), Color.FromRgb(0xC4, 0xB8, 0xFF), Color.FromRgb(0xB4, 0xA4, 0xFF))
        : (Color.FromRgb(0x26, 0x22, 0x80), Color.FromRgb(0x66, 0x50, 0xCC), Color.FromRgb(0x66, 0x50, 0xCC));

    /// <summary>Gradient slide (0 to 1) at <paramref name="time"/> into the loop.</summary>
    public static double SlideAt(TimeSpan time) => time.TotalSeconds % SlideSeconds / SlideSeconds;

    /// <summary>Dot opacity and scale at <paramref name="time"/>: sine ease in and out, 0.45 to 1 and 0.85 to 1 and back.</summary>
    public static (double Opacity, double Scale) BreathAt(TimeSpan time)
    {
        var wave = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (time.TotalSeconds % BreathSeconds) / BreathSeconds);
        return (0.45 + 0.55 * wave, 0.85 + 0.15 * wave);
    }

    /// <summary>Freezes the label at a moment of its loop (tests, screenshots).</summary>
    public void SeekTo(TimeSpan time)
    {
        _timer.Stop();
        _clock.Reset();
        Apply(time);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ColituReducedMotion.Changed += OnReducedMotionChanged;
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window != null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
        }
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        ColituReducedMotion.Changed -= OnReducedMotionChanged;
        if (_window != null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PaletteProperty || change.Property == ShowDotProperty || change.Property.Name == nameof(ActualThemeVariant))
        {
            Rebuild();
        }
        else if (change.Property.Name == nameof(IsEffectivelyVisible))
        {
            Refresh();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            Refresh();
        }
    }

    private void OnReducedMotionChanged() => Dispatcher.UIThread.Post(Refresh);

    private bool IsDark() => Palette != ColituGeckoPalette.Auto
        ? Palette == ColituGeckoPalette.Dark
        : ActualThemeVariant != ThemeVariant.Light;

    private void Rebuild()
    {
        var (deep, light, dot) = Colors(IsDark());
        _brush.GradientStops.Clear();
        _brush.GradientStops.Add(new GradientStop(deep, 0));
        _brush.GradientStops.Add(new GradientStop(light, 0.5));
        _brush.GradientStops.Add(new GradientStop(deep, 1));
        _dot.Fill = new SolidColorBrush(dot);
        _dot.IsVisible = ShowDot;
        Apply(_timer.IsEnabled ? _clock.Elapsed : TimeSpan.Zero);
        Refresh();
    }

    private void Refresh()
    {
        var minimized = _window is { WindowState: WindowState.Minimized };
        if (_attached && IsEffectivelyVisible && !minimized && !ReducedMotion)
        {
            if (!_timer.IsEnabled)
            {
                _clock.Start();
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
            _clock.Reset();
            Apply(TimeSpan.Zero);
        }
    }

    private void Apply(TimeSpan time)
    {
        SlideOffset = SlideAt(time);
        _brush.StartPoint = new RelativePoint(SlideOffset, 0, RelativeUnit.Relative);
        _brush.EndPoint = new RelativePoint(SlideOffset + 1, 0, RelativeUnit.Relative);
        var (opacity, scale) = _timer.IsEnabled || time != TimeSpan.Zero ? BreathAt(time) : (1.0, 1.0);
        _dot.Opacity = opacity;
        _scale.ScaleX = _scale.ScaleY = scale;
    }
}

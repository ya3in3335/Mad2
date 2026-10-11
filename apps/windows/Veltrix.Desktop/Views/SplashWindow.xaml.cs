using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Veltrix.Desktop.Views;

/// <summary>
/// Cinematic startup: navy fade-in with drifting particles, blade rise, circuit trace draw, node pop, one soft core glow,
/// wordmark + tagline reveal, then a thin indeterminate line only while real startup work is still running.
/// Skippable (click / any key). Reduced motion shows the static logo with a 150 ms fade.
/// </summary>
public partial class SplashWindow : Window
{
    private static readonly KeySpline Brand = new(0.2, 0.8, 0.2, 1); // cubic-bezier(0.2, 0.8, 0.2, 1)
    private readonly bool _reducedMotion;
    private readonly bool _firstLaunch;
    private readonly TaskCompletionSource _skip = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Storyboard? _intro;
    private Storyboard? _particles;

    public SplashWindow(bool reducedMotion, bool firstLaunch)
    {
        _reducedMotion = reducedMotion;
        _firstLaunch = firstLaunch;
        InitializeComponent();
        KeyDown += (_, _) => _skip.TrySetResult();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1) _skip.TrySetResult();
        };
        Loaded += (_, _) => Focus();
    }

    /// <summary>Plays the intro and returns once both the minimum brand moment and <paramref name="work"/> are done.</summary>
    public async Task PlayAsync(Task work)
    {
        TimeSpan minimum;
        if (_reducedMotion)
        {
            SkipHint.Visibility = Visibility.Collapsed;
            minimum = TimeSpan.FromMilliseconds(400);
            Root.Opacity = 0;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        }
        else
        {
            BuildParticles();
            StartAmbient();
            _intro = BuildIntro();
            // Later launches compress the same choreography into ≈1.2 s.
            _intro.SpeedRatio = _firstLaunch ? 1.0 : 2.1 / 1.2;
            _intro.Begin(this, true);
            minimum = TimeSpan.FromSeconds(_firstLaunch ? 2.1 : 1.2);
        }

        var brandMoment = Task.Delay(minimum);
        await Task.WhenAny(brandMoment, _skip.Task);
        if (_skip.Task.IsCompleted) _intro?.SkipToFill(this);

        if (!work.IsCompleted)
        {
            // Phase 4: shown only while real startup tasks are still running. No fake percentages.
            Progress.Visibility = Visibility.Visible;
            try { await work; } catch { /* reported by the caller */ }
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Phase 5: cross-fade (250 ms, 150 ms with reduced motion) into the main window.</summary>
    public async Task CrossFadeToAsync(Window main)
    {
        var duration = TimeSpan.FromMilliseconds(_reducedMotion ? 150 : 250);
        main.Opacity = 0;
        main.Show();
        main.Activate();
        var tcs = new TaskCompletionSource();
        var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        fadeIn.Completed += (_, _) => tcs.TrySetResult();
        main.BeginAnimation(OpacityProperty, fadeIn);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
        await tcs.Task;
        main.BeginAnimation(OpacityProperty, null);
        main.Opacity = 1;
        _particles?.Stop(this);
        _ambient?.Stop(this);
        _intro?.Stop(this);
        Close();
    }

    private Storyboard? _ambient;

    /// <summary>Looping background motion (aurora drift + counter-rotating rings). Disabled with reduced motion.</summary>
    private void StartAmbient()
    {
        _ambient = new Storyboard();
        var slow = TimeSpan.FromSeconds(9);
        Add(_ambient, AuroraBlue, "(UIElement.RenderTransform).(TranslateTransform.X)", new DoubleAnimation(0, 140, slow) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        Add(_ambient, AuroraBlue, "(UIElement.RenderTransform).(TranslateTransform.Y)", new DoubleAnimation(0, 60, slow) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        Add(_ambient, AuroraPurple, "(UIElement.RenderTransform).(TranslateTransform.X)", new DoubleAnimation(0, -160, slow) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        Add(_ambient, AuroraPurple, "(UIElement.RenderTransform).(TranslateTransform.Y)", new DoubleAnimation(0, -70, slow) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        Add(_ambient, RingOuter, "(UIElement.RenderTransform).(RotateTransform.Angle)", new DoubleAnimation(0, 360, TimeSpan.FromSeconds(18)) { RepeatBehavior = RepeatBehavior.Forever });
        Add(_ambient, RingInner, "(UIElement.RenderTransform).(RotateTransform.Angle)", new DoubleAnimation(360, 0, TimeSpan.FromSeconds(12)) { RepeatBehavior = RepeatBehavior.Forever });
        _ambient.Begin(this, true);
        Aurora.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)));
    }

    private Storyboard BuildIntro()
    {
        var sb = new Storyboard { FillBehavior = FillBehavior.HoldEnd };

        // Phase 1 (0–450 ms): background fades in.
        Add(sb, Root, "Opacity", Fade(0, 1, 0, 450));

        // Phase 2 (300–1500 ms): blades rise 24 px and fade in, the right one 150 ms later.
        foreach (var (blade, delay) in new[] { (LeftBlade, 300), (RightBlade, 450) })
        {
            Add(sb, blade, "Opacity", Spline(0, 1, delay, 900));
            Add(sb, blade, "(UIElement.RenderTransform).(TranslateTransform.Y)", Spline(24, 0, delay, 900));
        }
        // Circuit trace draws (dash offset 30 → 0, in stroke-thickness units) then nodes pop 0.6 → 1.
        Add(sb, Trace, "StrokeDashOffset", Spline(30, 0, 800, 700));
        Add(sb, Trace, "Opacity", Fade(0, 0.85, 800, 100));
        foreach (var node in new[] { Node1, Node2 }) Pop(sb, node, 1100, 350);

        // Phase 3 (1300–2100 ms): core node pops and glows once (blur 0 → 12 → 6, no flashing).
        Pop(sb, Core, 1300, 400);
        var glow = new DoubleAnimationUsingKeyFrames { BeginTime = Ms(1400) };
        glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        glow.KeyFrames.Add(new SplineDoubleKeyFrame(12, KeyTime.FromTimeSpan(Ms(600)), new KeySpline(0.42, 0, 0.58, 1)));
        glow.KeyFrames.Add(new SplineDoubleKeyFrame(6, KeyTime.FromTimeSpan(Ms(1200)), new KeySpline(0.42, 0, 0.58, 1)));
        Add(sb, Core, "(UIElement.Effect).(DropShadowEffect.BlurRadius)", glow);

        // Wordmark fades in with 12 px upward movement, then the tagline.
        Add(sb, Wordmark, "Opacity", Fade(0, 1, 1500, 700));
        Add(sb, Wordmark, "(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)", Spline(12, 0, 1500, 700));
        Add(sb, Tagline, "Opacity", Fade(0, 1, 1800, 500));
        Add(sb, SkipHint, "Opacity", Fade(0, 0.6, 600, 400));

        // Motion graphics: core energy pulse (1500 ms) and a single light sweep across the brand (1900–2700 ms).
        Add(sb, Pulse, "Opacity", Fade(0.7, 0, 1500, 900));
        Add(sb, Pulse, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", Spline(0.5, 4, 1500, 900));
        Add(sb, Pulse, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", Spline(0.5, 4, 1500, 900));
        Add(sb, Sweep, "Opacity", Fade(0, 1, 1900, 100));
        Add(sb, Sweep, "(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.X)", Spline(0, 900, 1900, 800));
        Add(sb, Sweep, "Opacity", Fade(1, 0, 2600, 150));
        Add(sb, RingOuter, "Opacity", Fade(0, 1, 600, 800));
        Add(sb, RingInner, "Opacity", Fade(0, 1, 800, 800));

        // Initial state before the storyboard reaches each element.
        foreach (UIElement e in new UIElement[] { LeftBlade, RightBlade, Trace, Node1, Node2, Core, Wordmark, Tagline }) e.Opacity = 0;
        return sb;
    }

    private void BuildParticles()
    {
        // 14 faint silver particles (opacity ≤ 0.35) drifting upwards; GPU-composited transforms only.
        _particles = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var rnd = new Random(7);
        for (var i = 0; i < 14; i++)
        {
            var dot = new Ellipse { Width = 3, Height = 3, Fill = new SolidColorBrush(i % 3 == 0 ? Color.FromRgb(0x8A, 0x90, 0xA0) : Color.FromRgb(0xE6, 0xE9, 0xF0)), Opacity = 0.15 + rnd.NextDouble() * 0.2 };
            var t = new TranslateTransform();
            dot.RenderTransform = t;
            Canvas.SetLeft(dot, (i * 97 % 640) + rnd.Next(0, 30));
            Canvas.SetTop(dot, 120 + (i * 53 % 380));
            Particles.Children.Add(dot);
            var drift = new DoubleAnimation(0, -60, TimeSpan.FromSeconds(6)) { BeginTime = TimeSpan.FromMilliseconds(-rnd.Next(0, 6000)) };
            Storyboard.SetTarget(drift, dot);
            Storyboard.SetTargetProperty(drift, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            _particles.Children.Add(drift);
        }
        Particles.Opacity = 0;
        Particles.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)));
        _particles.Begin(this, true);
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static DoubleAnimation Fade(double from, double to, int begin, int duration) =>
        new(from, to, Ms(duration)) { BeginTime = Ms(begin), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };

    private static DoubleAnimationUsingKeyFrames Spline(double from, double to, int begin, int duration)
    {
        var a = new DoubleAnimationUsingKeyFrames { BeginTime = Ms(begin) };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(Ms(duration)), Brand));
        return a;
    }

    private static void Pop(Storyboard sb, FrameworkElement e, int begin, int duration)
    {
        Add(sb, e, "Opacity", Fade(0, 1, begin, duration));
        Add(sb, e, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", Spline(0.6, 1, begin, duration));
        Add(sb, e, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", Spline(0.6, 1, begin, duration));
    }

    private static void Add(Storyboard sb, DependencyObject target, string property, Timeline animation)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        sb.Children.Add(animation);
    }
}

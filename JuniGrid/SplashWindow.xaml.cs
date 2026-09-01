using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace JuniGrid;

/// <summary>
/// PCL-style transparent splash window: logo + "JuniGrid" wordmark filled glyph by glyph.
/// The stroke is set to transparent (Stroke=Transparent) and only drives the per-glyph
/// animation timing; visually only the white fill appears in rhythm. The lifecycle is
/// controlled by App: the logo fades in, floats up and shifts left while the wordmark
/// fills in; when everything completes, IntroCompleted fires.
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>Fires when the logo entrance and wordmark fill have both completed.</summary>
    public event Action? IntroCompleted;

    private bool _introDone;
    private Geometry _logoGeo = Geometry.Empty;
    private Rect _bounds;
    private const double LogoSize = 140;
    private const double Gap = 16;

    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PlayIntro();
    }

    private void PlayIntro()
    {
        BuildWordGeometry();

        double stageW = Stage.Width;    // 560 (fixed, see XAML)
        double stageH = Stage.Height;   // 240
        double halfH = stageH / 2;

        // logo is static at the far left; "JuniGrid" sits one Gap to its right
        double wordX = LogoSize + Gap;
        double wordY = halfH - _bounds.Height / 2;

        Canvas.SetLeft(LogoImg, 0);
        Canvas.SetTop(LogoImg, halfH - LogoSize / 2);
        Canvas.SetLeft(StrokePath, wordX);
        Canvas.SetTop(StrokePath, wordY);
        Canvas.SetLeft(FillPath, wordX);
        Canvas.SetTop(FillPath, wordY);

        // v0.22.0: freeze the geometry to avoid per-frame clones / dispatcher checks
        if (_logoGeo.CanFreeze) _logoGeo.Freeze();

        StrokePath.Data = _logoGeo;
        FillPath.Data = _logoGeo;
        FillPath.Opacity = 0;   // fully transparent at first, raised one frame before the wipe

        // v0.27.0: animate RectangleGeometry.RectProperty directly — the clip region updates for real each frame.
        // v0.22 used a ScaleTransform on Geometry.Transform, but when WPF processes Geometry via the
        // Freezable optimized path, Transform animations don't necessarily update the clip region each
        // frame, making letters "pop in" instead of sweeping left to right. RectProperty is the path
        // explicitly supported by Freezable animation; performance is fine for an 8-letter Path.
        var wipeClip = new RectangleGeometry(new Rect(0, 0, 0, _bounds.Height));
        FillPath.Clip = wipeClip;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // a) Per-glyph stroke tracing (transparent, drives timing only): starts at 0.3s → 1.5s
        var dash = Math.Max(_bounds.Width, _bounds.Height) * 3 + 30;
        StrokePath.StrokeDashArray = new DoubleCollection { dash, dash };
        StrokePath.StrokeDashOffset = dash;
        AnimateUI(StrokePath, Shape.StrokeDashOffsetProperty, dash, 0, 0.3, 1.5, ease);

        // b) White fill wipe: ScaleX 0 → 1 (GPU-direct, extremely smooth)
        //    starts at 1.8s → 0.9s; one frame before it starts, FillPath.Opacity is snapped to 1
        var opa = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromMilliseconds(1795)
        };
        opa.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        FillPath.BeginAnimation(OpacityProperty, opa);

        // v0.28.0: the wipe is now a uniform linear sweep — CubicEase EaseOut is fast-then-slow,
        // covering 65% of the width in the first 30% of the time (the first 7 letters revealed
        // almost instantly, looking like they "pop in"), leaving only the final d sweeping slowly.
        // Switched to no easing (linear) and lengthened to 1.3s so all 8 letters sweep in evenly.
        var rectAnim = new RectAnimation
        {
            From = new Rect(0, 0, 0, _bounds.Height),
            To = new Rect(0, 0, _bounds.Width, _bounds.Height),
            BeginTime = TimeSpan.FromMilliseconds(1800),
            Duration = TimeSpan.FromMilliseconds(1300)
            // No EasingFunction — the default is uniform linear
        };
        // After the wipe completes, dwell 800ms so the full wordmark is visible before firing IntroCompleted
        rectAnim.Completed += (_, _) =>
        {
            var dwell = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(800) };
            dwell.Tick += (_, _) => { dwell.Stop(); FinishIntro(); };
            dwell.Start();
        };
        wipeClip.BeginAnimation(RectangleGeometry.RectProperty, rectAnim);
    }

    private void BuildWordGeometry()
    {
        var typeface = new Typeface(
            new FontFamily("Segoe UI"), FontStyles.Normal,
            FontWeights.ExtraBold, FontStretches.Normal);
        var ft = new FormattedText(
            "JuniGrid", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, 96, Brushes.Black, 1.0);

        var geo = ft.BuildGeometry(new Point(0, 0));
        var b = geo.Bounds;
        // Translate the glyphs to the top-left corner (0,0) for easier positioning within the Stage
        geo.Transform = new TranslateTransform(-b.X, -b.Y);
        _bounds = new Rect(0, 0, geo.Bounds.Width, geo.Bounds.Height);
        _logoGeo = geo;
    }

    private void FinishIntro()
    {
        if (_introDone) return;
        _introDone = true;
        IntroCompleted?.Invoke();
    }

    public bool IntroDone => _introDone;

    /// <summary>Straight close: no fade-out animation; the splash closes immediately after the animation finishes, handing off to the main window slide-in.</summary>
    public void FadeOutAndClose()
    {
        Close();
    }

    private static void AnimateUI(UIElement target, DependencyProperty dp,
        double from, double to, double beginSec, double durSec, EasingFunctionBase ease)
    {
        var a = new DoubleAnimation
        {
            From = from, To = to,
            BeginTime = TimeSpan.FromSeconds(beginSec),
            Duration = TimeSpan.FromSeconds(durSec),
            EasingFunction = ease
        };
        target.BeginAnimation(dp, a);
    }
}
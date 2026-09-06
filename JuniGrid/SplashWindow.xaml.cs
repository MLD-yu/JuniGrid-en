using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace JuniGrid;

/// <summary>
/// PCL-style transparent splash window: logo plus a "JuniGrid" wordmark outlined and filled stroke by stroke.
/// The stroke is set to transparent (Stroke=Transparent) and only drives the timing of the
/// per-letter animation; visually only the white fill appears in rhythm. The lifetime is
/// controlled by App: the logo fades in, floats up and slides left while the wordmark fill
/// appears; when everything completes, IntroCompleted is raised.
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>Raised when the logo entrance and the wordmark fill have both finished.</summary>
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

        // Logo sits statically at the far left; "JuniGrid" is placed one Gap to its right
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
        FillPath.Opacity = 0;   // starts fully transparent, raised one frame before the wipe

        // v0.27.0: animate RectangleGeometry.RectProperty directly so the clip region is really
        // updated every frame. v0.22 wrapped a ScaleTransform in Geometry.Transform, but in WPF,
        // when a Geometry goes through the Freezable optimized path, a Transform animation does
        // not necessarily update the clip region every frame, making letters "pop in" instead of
        // sweeping left to right. RectProperty is a path explicitly supported by Freezable
        // animations, and a Path with 8 letters performs perfectly well.
        var wipeClip = new RectangleGeometry(new Rect(0, 0, 0, _bounds.Height));
        FillPath.Clip = wipeClip;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // a) Letter-by-letter outline (transparent, only drives timing): starts at 0.3s, runs 1.5s
        var dash = Math.Max(_bounds.Width, _bounds.Height) * 3 + 30;
        StrokePath.StrokeDashArray = new DoubleCollection { dash, dash };
        StrokePath.StrokeDashOffset = dash;
        AnimateUI(StrokePath, Shape.StrokeDashOffsetProperty, dash, 0, 0.3, 1.5, ease);

        // b) White fill wipe: ScaleX from 0 to 1 (directly GPU-driven, very smooth)
        //    starts at 1.8s, runs 0.9s; one frame before it starts, FillPath.Opacity is set to 1 instantly
        var opa = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromMilliseconds(1795)
        };
        opa.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        FillPath.BeginAnimation(OpacityProperty, opa);

        // v0.28.0: the wipe now sweeps linearly at constant speed — CubicEase EaseOut is
        // fast-then-slow, covering 65% of the width in the first 30% of the time (the first
        // 7 letters reveal instantly, visually "popping in"), leaving only the final d to
        // sweep slowly. Switched to no easing (linear) and stretched to 1.3s so the 8 letters
        // sweep in evenly.
        var rectAnim = new RectAnimation
        {
            From = new Rect(0, 0, 0, _bounds.Height),
            To = new Rect(0, 0, _bounds.Width, _bounds.Height),
            BeginTime = TimeSpan.FromMilliseconds(1800),
            Duration = TimeSpan.FromMilliseconds(1300)
            // No EasingFunction — the default is linear at constant speed
        };
        // After the wipe finishes, dwell another 800ms so the full wordmark is readable, then raise IntroCompleted
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
        // Translate the glyphs to the top-left corner (0,0) for easier positioning inside the Stage
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

    /// <summary>Straight-out: no fade-out animation; closes the Splash immediately after the animation so the main window can slide in.</summary>
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
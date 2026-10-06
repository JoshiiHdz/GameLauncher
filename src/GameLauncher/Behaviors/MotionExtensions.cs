using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace GameLauncher.Behaviors;

/// <summary>Additions to the existing Motion vocabulary for the Console-tile and Console-ribbon themes. Follows Motion.cs rules:
/// nothing animates when Windows "Animation effects" is off (values snap to final), and entrances end on real values.
/// Durations/easings are read from the active theme's tokens (DurFocus, EaseFocus, …) via FindResource at play time.</summary>
public static class MotionExtensions
{
    private static bool On => SystemParameters.ClientAreaAnimation;

    private static Duration Dur(FrameworkElement e, string key, double fallbackMs) =>
        e.TryFindResource(key) is Duration d ? d : new Duration(TimeSpan.FromMilliseconds(fallbackMs));

    private static IEasingFunction Ease(FrameworkElement e, string key) =>
        e.TryFindResource(key) as IEasingFunction ?? new CubicEase { EasingMode = EasingMode.EaseOut };

    // ---- FocusScale: scale the element (centre origin) while IsFocused is true -------------------------------------

    public static readonly DependencyProperty IsFocusedProperty = DependencyProperty.RegisterAttached(
        "IsFocused", typeof(bool), typeof(MotionExtensions), new PropertyMetadata(false, OnIsFocusedChanged));
    public static void SetIsFocused(DependencyObject d, bool v) => d.SetValue(IsFocusedProperty, v);
    public static bool GetIsFocused(DependencyObject d) => (bool)d.GetValue(IsFocusedProperty);

    /// <summary>Target scale when focused. Tokens: FocusScale (Axis 1.0, Tile 1.06, Ribbon 1.05).</summary>
    public static readonly DependencyProperty FocusScaleProperty = DependencyProperty.RegisterAttached(
        "FocusScale", typeof(double), typeof(MotionExtensions), new PropertyMetadata(1.0));
    public static void SetFocusScale(DependencyObject d, double v) => d.SetValue(FocusScaleProperty, v);
    public static double GetFocusScale(DependencyObject d) => (double)d.GetValue(FocusScaleProperty);

    private static void OnIsFocusedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        if (el.RenderTransform is not ScaleTransform st) el.RenderTransform = st = new ScaleTransform(1, 1);
        var to = (bool)e.NewValue ? GetFocusScale(el) : 1.0;
        Raise(el, (bool)e.NewValue);
        if (!On) { st.BeginAnimation(ScaleTransform.ScaleXProperty, null); st.BeginAnimation(ScaleTransform.ScaleYProperty, null); st.ScaleX = st.ScaleY = 1; return; }
        var a = new DoubleAnimation(to, Dur(el, "DurFocus", 140)) { EasingFunction = Ease(el, "EaseFocus"), FillBehavior = FillBehavior.HoldEnd };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>Z-order is per child of a panel, and a card sits inside a ContentPresenter: lift the child that the panel actually holds,
    /// so a scaled focus card draws over its neighbours instead of under them.</summary>
    private static void Raise(FrameworkElement el, bool up)
    {
        DependencyObject child = el;
        while (VisualTreeHelper.GetParent(child) is { } parent && parent is not Panel)
            child = parent;

        if (child is UIElement ui && VisualTreeHelper.GetParent(child) is Panel)
            Panel.SetZIndex(ui, up ? 10 : 0);
        else
            Panel.SetZIndex(el, up ? 10 : 0);
    }

    // ---- Breathe: pulse a DropShadowEffect glow a few times when it appears, then let it rest (Console-ribbon focus) ----------------------------------

    public static readonly DependencyProperty BreatheProperty = DependencyProperty.RegisterAttached(
        "Breathe", typeof(bool), typeof(MotionExtensions), new PropertyMetadata(false, OnBreatheChanged));
    public static void SetBreathe(DependencyObject d, bool v) => d.SetValue(BreatheProperty, v);
    public static bool GetBreathe(DependencyObject d) => (bool)d.GetValue(BreatheProperty);

    private static void OnBreatheChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.BeginAnimation(UIElement.OpacityProperty, null);
        if (!(bool)e.NewValue) { el.Effect = null; el.CacheMode = null; return; }
        var color = el.TryFindResource("AccentGlowColor") is Color c ? c : Color.FromRgb(0x60, 0xA5, 0xFA);
        // The glow is drawn once and cached as a texture; only its opacity breathes, which the GPU composites for almost nothing. (Animating
        // the blur radius re-rendered the effect on every frame for every focused icon, the main cause of the ribbon lagging.) It pulses twice and then
        // stays lit, so a screen left alone costs nothing to draw.)
        el.Effect = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 24, Opacity = 0.65 };
        el.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        if (!On) return; // static glow when animations are off
        var period = Dur(el, "DurBreathe", 2400).TimeSpan;
        var half = new Duration(TimeSpan.FromTicks(period.Ticks / 2));
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.55, 1, half) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    // ---- Recede: scale + dim the content root while an overlay is open (Console-ribbon) ----------------------------

    public static readonly DependencyProperty RecedeProperty = DependencyProperty.RegisterAttached(
        "Recede", typeof(bool), typeof(MotionExtensions), new PropertyMetadata(false, OnRecedeChanged));
    public static void SetRecede(DependencyObject d, bool v) => d.SetValue(RecedeProperty, v);
    public static bool GetRecede(DependencyObject d) => (bool)d.GetValue(RecedeProperty);

    /// <summary>Set on the content root's child dim overlay (a black Rectangle, IsHitTestVisible=False).</summary>
    public static readonly DependencyProperty DimTargetProperty = DependencyProperty.RegisterAttached(
        "DimTarget", typeof(UIElement), typeof(MotionExtensions), new PropertyMetadata(null));
    public static void SetDimTarget(DependencyObject d, UIElement v) => d.SetValue(DimTargetProperty, v);
    public static UIElement GetDimTarget(DependencyObject d) => (UIElement)d.GetValue(DimTargetProperty);

    private static void OnRecedeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var on = (bool)e.NewValue;
        el.RenderTransformOrigin = new Point(0.5, 0.4);
        if (el.RenderTransform is not ScaleTransform st) el.RenderTransform = st = new ScaleTransform(1, 1);
        var scale = on ? (el.TryFindResource("RecedeScale") is double s ? s : 0.965) : 1.0;
        var dim = on ? (el.TryFindResource("RecedeDimOpacity") is double o ? o : 0.55) : 0.0;
        var target = GetDimTarget(el);
        if (!On)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null); st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            st.ScaleX = st.ScaleY = scale;
            if (target != null) { target.BeginAnimation(UIElement.OpacityProperty, null); target.Opacity = dim; }
            return;
        }
        var dur = new Duration(TimeSpan.FromMilliseconds(420));
        var ease = Ease(el, "EaseSoft");
        var a = new DoubleAnimation(scale, dur) { EasingFunction = ease };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        target?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(dim, dur) { EasingFunction = ease });
    }

    // ---- Enter: entrance presets for pages, sheets, dialogs, menus, toasts ------------------------------------------

    public enum EnterKind { None, Fade, Rise, RiseLarge, SlideLeft, SlideRight, SlideUp, DropIn, ScaleIn }

    public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
        "Enter", typeof(EnterKind), typeof(MotionExtensions), new PropertyMetadata(EnterKind.None, OnEnterChanged));
    public static void SetEnter(DependencyObject d, EnterKind v) => d.SetValue(EnterProperty, v);
    public static EnterKind GetEnter(DependencyObject d) => (EnterKind)d.GetValue(EnterProperty);

    /// <summary>Token key for the duration (e.g. "DurPage", "DurPanel", "DurDialog", "DurMenu", "DurToast").</summary>
    public static readonly DependencyProperty EnterDurationKeyProperty = DependencyProperty.RegisterAttached(
        "EnterDurationKey", typeof(string), typeof(MotionExtensions), new PropertyMetadata("DurPage"));
    public static void SetEnterDurationKey(DependencyObject d, string v) => d.SetValue(EnterDurationKeyProperty, v);
    public static string GetEnterDurationKey(DependencyObject d) => (string)d.GetValue(EnterDurationKeyProperty);

    public static readonly DependencyProperty EnterDelayProperty = DependencyProperty.RegisterAttached(
        "EnterDelay", typeof(double), typeof(MotionExtensions), new PropertyMetadata(0d));
    public static void SetEnterDelay(DependencyObject d, double v) => d.SetValue(EnterDelayProperty, v);
    public static double GetEnterDelay(DependencyObject d) => (double)d.GetValue(EnterDelayProperty);

    /// <summary>Plays the element's entrance again whenever this value changes (the Console-ribbon hero and cards when the focus moves).</summary>
    public static readonly DependencyProperty ReplayOnProperty = DependencyProperty.RegisterAttached(
        "ReplayOn", typeof(object), typeof(MotionExtensions), new PropertyMetadata(null, OnReplayOnChanged));
    public static void SetReplayOn(DependencyObject d, object? v) => d.SetValue(ReplayOnProperty, v);
    public static object? GetReplayOn(DependencyObject d) => d.GetValue(ReplayOnProperty);

    private static void OnReplayOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement el && el.IsLoaded && e.OldValue is not null && GetEnter(el) != EnterKind.None)
            Play(el, null!);
    }

    private static void OnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.Loaded -= Play; el.IsVisibleChanged -= PlayOnVisible;
        if ((EnterKind)e.NewValue == EnterKind.None) return;
        el.Loaded += Play; el.IsVisibleChanged += PlayOnVisible;
    }

    /// <summary>How far an element must travel to start just off its edge. An element shown for the first time has not been laid out
    /// yet (its ActualWidth is 0), so a width or height it was given is used first, then a sensible fallback.</summary>
    private static double ExtentOf(FrameworkElement el, bool horizontal)
    {
        var set = horizontal ? el.Width : el.Height;
        if (!double.IsNaN(set) && set > 0)
            return set;

        var actual = horizontal ? el.ActualWidth : el.ActualHeight;
        return actual > 0 ? actual : horizontal ? 340 : 64;
    }

    private static void PlayOnVisible(object s, DependencyPropertyChangedEventArgs e) { if ((bool)e.NewValue) Play(s, null!); }

    private static void Play(object sender, RoutedEventArgs _)
    {
        var el = (FrameworkElement)sender;
        if (!On) return;
        var kind = GetEnter(el);
        var dur = Dur(el, GetEnterDurationKey(el), 260);
        // Each theme's EaseStandard is its entrance curve (Axis ≈ CubicEase out, Tile .2,.8,.2,1, Ribbon .2,.7,.2,1).
        var ease = Ease(el, "EaseStandard");
        var begin = TimeSpan.FromMilliseconds(GetEnterDelay(el));
        var rise = el.TryFindResource("RiseDistance") is double r ? r : 10;
        var pageRise = el.TryFindResource("PageRiseDistance") is double pr ? pr : 10;

        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.Stop });

        var tg = new TransformGroup();
        var tt = new TranslateTransform(); var sc = new ScaleTransform(1, 1);
        tg.Children.Add(sc); tg.Children.Add(tt);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = tg;

        DoubleAnimation A(double from) => new(from, 0, dur) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        switch (kind)
        {
            case EnterKind.Rise: tt.BeginAnimation(TranslateTransform.YProperty, A(rise)); break;
            case EnterKind.RiseLarge: tt.BeginAnimation(TranslateTransform.YProperty, A(pageRise)); break;
            case EnterKind.DropIn: tt.BeginAnimation(TranslateTransform.YProperty, A(-Math.Max(4, rise))); break;
            case EnterKind.SlideLeft: tt.BeginAnimation(TranslateTransform.XProperty, A(-ExtentOf(el, horizontal: true))); break;
            case EnterKind.SlideRight: tt.BeginAnimation(TranslateTransform.XProperty, A(ExtentOf(el, horizontal: true))); break;
            case EnterKind.SlideUp: tt.BeginAnimation(TranslateTransform.YProperty, A(ExtentOf(el, horizontal: false))); break;
            case EnterKind.ScaleIn:
                var s = new DoubleAnimation(0.94, 1, dur) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.Stop };
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, s); sc.BeginAnimation(ScaleTransform.ScaleYProperty, s); break;
        }
    }
}

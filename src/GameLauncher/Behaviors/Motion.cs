using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GameLauncher.Behaviors;

/// <summary>The app's small, shared motion vocabulary, as attached properties so XAML can ask for it in one attribute:
/// <list type="bullet">
/// <item><c>Motion.FadeIn</c> - fades an element in and lifts it a few pixels when it first loads.</item>
/// <item><c>Motion.Replay</c> - replays that fade whenever the bound value changes (a new featured game, a new view heading).</item>
/// <item><c>Motion.AnimatedValue</c> - sets a ProgressBar's value with a short ease instead of a jump.</item>
/// </list>
/// Everything is skipped when Windows' "Animation effects" setting is off (the same switch the card hover motion honours), and every
/// animation ends back on the element's real value (FillBehavior.Stop), so an interrupted or failed animation can never leave anything
/// invisible or off-position.</summary>
public static class Motion
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration BarDuration = new(TimeSpan.FromMilliseconds(520));
    private const double RiseDistance = 10;

    /// <summary>Whether to animate at all. Windows' own setting in the app; tests replace it so timing never matters to them.</summary>
    internal static Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    // ---- FadeIn ------------------------------------------------------------------------------------

    public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached(
        "FadeIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeInChanged));

    public static bool GetFadeIn(DependencyObject d) => (bool)d.GetValue(FadeInProperty);

    public static void SetFadeIn(DependencyObject d, bool value) => d.SetValue(FadeInProperty, value);

    /// <summary>Milliseconds to wait before the fade starts - lets a few neighbouring elements arrive one after another.</summary>
    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(double), typeof(Motion), new PropertyMetadata(0d));

    public static double GetDelay(DependencyObject d) => (double)d.GetValue(DelayProperty);

    public static void SetDelay(DependencyObject d, double value) => d.SetValue(DelayProperty, value);

    private static void OnFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        element.Loaded -= OnFadeInLoaded;
        if ((bool)e.NewValue)
        {
            element.Loaded += OnFadeInLoaded;
            if (element.IsLoaded)
                Play(element);
        }
    }

    private static void OnFadeInLoaded(object sender, RoutedEventArgs e) => Play((FrameworkElement)sender);

    // ---- Replay ------------------------------------------------------------------------------------

    public static readonly DependencyProperty ReplayProperty = DependencyProperty.RegisterAttached(
        "Replay", typeof(object), typeof(Motion), new PropertyMetadata(null, OnReplayChanged));

    public static object? GetReplay(DependencyObject d) => d.GetValue(ReplayProperty);

    public static void SetReplay(DependencyObject d, object? value) => d.SetValue(ReplayProperty, value);

    private static void OnReplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // The first value arrives with the binding, before the element is on screen; only a change to a shown element replays.
        if (d is FrameworkElement { IsLoaded: true } element && !Equals(e.OldValue, e.NewValue))
            Play(element);
    }

    // ---- the fade itself ---------------------------------------------------------------------------

    /// <summary>Raised each time a fade starts - lets tests see that one began without reading frames off the animation clock.</summary>
    internal static event Action<FrameworkElement>? FadeStarted;

    /// <summary>Plays the same fade-and-lift on demand - for something that is shown by toggling visibility, so Loaded never fires again.</summary>
    internal static void PlayFade(FrameworkElement element) => Play(element);

    private static void Play(FrameworkElement element)
    {
        if (!AnimationsEnabled())
            return;

        FadeStarted?.Invoke(element);

        var delay = TimeSpan.FromMilliseconds(Math.Max(0, GetDelay(element)));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Fades to whatever opacity the element really has (some are dimmed on purpose), then stops back on it.
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, element.Opacity, FadeDuration)
        {
            BeginTime = delay,
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        });

        // The lift needs a transform of its own; an element that already has one (a card's hover motion lives on a child, but
        // others may use theirs) is faded only, never have its transform replaced.
        if (element.RenderTransform is null || element.RenderTransform == Transform.Identity)
            element.RenderTransform = new TranslateTransform();

        if (element.RenderTransform is TranslateTransform lift)
        {
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(RiseDistance, 0, FadeDuration)
            {
                BeginTime = delay,
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop,
            });
        }
    }

    // ---- AnimatedValue -----------------------------------------------------------------------------

    public static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.RegisterAttached(
        "AnimatedValue", typeof(double), typeof(Motion), new PropertyMetadata(0d, OnAnimatedValueChanged));

    public static double GetAnimatedValue(DependencyObject d) => (double)d.GetValue(AnimatedValueProperty);

    public static void SetAnimatedValue(DependencyObject d, double value) => d.SetValue(AnimatedValueProperty, value);

    private static void OnAnimatedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RangeBase bar)
            return;

        var target = (double)e.NewValue;
        if (!AnimationsEnabled())
        {
            bar.BeginAnimation(RangeBase.ValueProperty, null); // drop any animation still holding an older value
            bar.Value = target;
            return;
        }

        // A bar that has never shown anything grows from empty; one that has moves from where it is now.
        bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(bar.Value, target, BarDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        });
    }
}

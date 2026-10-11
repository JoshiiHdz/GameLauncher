using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GameLauncher.Behaviors;

/// <summary>A short swell-and-settle that says "the pad is here": played on a control the pad has just come back to, when what it left was lit as well and nothing else would
/// show that the pad has moved (a card that stays selected while the pad is on Play; the Library's filters when the pad returns from the grid). Not with Windows' animations
/// off.</summary>
internal static class PadPulse
{
    /// <summary>How many pulses have been played (the tests read it).</summary>
    internal static int Total { get; private set; }

    /// <summary>Swells <paramref name="element"/> to <paramref name="scale"/> and lets it settle, growing from the point of it at <paramref name="originX"/> (0 left, 1 right).</summary>
    internal static void Play(FrameworkElement element, double scale = 1.06, double originX = 0.5, double settle = 1.0)
    {
        if (!Motion.AnimationsEnabled())
            return;

        Total++;
        if (element.RenderTransform is not ScaleTransform transform || transform.IsFrozen)
        {
            transform = new ScaleTransform(1, 1);
            element.RenderTransform = transform;
        }

        element.RenderTransformOrigin = new Point(originX, 0.5);
        var animation = new DoubleAnimation(scale, settle, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>Lets a control that was held larger (<c>Play</c> with a <c>settle</c> above 1) go back to its own size.</summary>
    internal static void Rest(FrameworkElement element)
    {
        if (element.RenderTransform is not ScaleTransform transform || transform.IsFrozen)
            return;

        if (!Motion.AnimationsEnabled())
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleX = 1;
            transform.ScaleY = 1;
            return;
        }

        var back = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, back);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, back);
    }
}

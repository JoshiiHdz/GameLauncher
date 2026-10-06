using System.Windows;
using System.Windows.Media.Animation;
using GameLauncher.Services;

namespace GameLauncher.Behaviors;

/// <summary>The active theme's durations and easing curves, for XAML that cannot use a DynamicResource: animations inside a template's
/// triggers are frozen when the template is built, so they read these with <c>{x:Static}</c>. A template is rebuilt when the theme is
/// swapped (implicit styles are looked up again), at which point the new theme's values are read. The numbers live only in the theme's
/// token dictionary (<c>DurHover</c>, <c>EaseFocus</c>, ...); nothing here repeats them.
/// With Windows' "Animation effects" off every duration is zero, so a storyboard lands on its end value at once and nothing moves.</summary>
public static class ThemeTiming
{
    private static Duration Dur(string key)
    {
        if (!SystemParameters.ClientAreaAnimation)
            return new Duration(TimeSpan.Zero);

        return ThemeManager.Token(key) is Duration d ? d : new Duration(TimeSpan.FromMilliseconds(150));
    }

    private static IEasingFunction Ease(string key) =>
        ThemeManager.Token(key) as IEasingFunction ?? new CubicEase { EasingMode = EasingMode.EaseOut };

    public static Duration Hover => Dur("DurHover");
    public static Duration Focus => Dur("DurFocus");
    public static Duration Menu => Dur("DurMenu");
    public static Duration Panel => Dur("DurPanel");
    public static Duration Dialog => Dur("DurDialog");
    public static Duration Page => Dur("DurPage");

    public static IEasingFunction EaseStandard => Ease("EaseStandard");
    public static IEasingFunction EaseFocus => Ease("EaseFocus");
    public static IEasingFunction EaseOutSnap => Ease("EaseOutSnap");
    public static IEasingFunction EaseSoft => Ease("EaseSoft");
}

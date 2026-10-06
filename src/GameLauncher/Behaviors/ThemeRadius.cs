using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameLauncher.Services;

namespace GameLauncher.Behaviors;

/// <summary>Gives a Border the corner radius a theme token names, without the "huge radius" distortion: WPF does not clamp a CornerRadius to
/// the element's size, so a pill (token value 999) on a 40 px button would draw as an ellipse. This sets each corner to the token's value
/// capped at half the shorter side, and sets it again when the element is resized or the theme changes.
/// <code>&lt;Border beh:ThemeRadius.Key="RadiusButton" /&gt;</code>
/// The special key <c>Pill</c> always means "fully rounded" (half the shorter side), whatever the theme.</summary>
public static class ThemeRadius
{
    private static readonly List<WeakReference<Border>> Live = [];
    private static bool _subscribed;

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(ThemeRadius), new PropertyMetadata(null, OnKeyChanged));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);

    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);

    /// <summary>Also clips the Border's content to its rounded outline (WPF clips to the rectangle only), sized to the Border and kept sized.</summary>
    public static readonly DependencyProperty RoundClipProperty = DependencyProperty.RegisterAttached(
        "RoundClip", typeof(bool), typeof(ThemeRadius), new PropertyMetadata(false, (d, _) => { if (d is Border b) Update(b); }));

    public static bool GetRoundClip(DependencyObject d) => (bool)d.GetValue(RoundClipProperty);

    public static void SetRoundClip(DependencyObject d, bool value) => d.SetValue(RoundClipProperty, value);

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
            return;

        border.SizeChanged -= OnSizeChanged;
        border.Loaded -= OnLoaded;
        border.Unloaded -= OnUnloaded;
        if (e.NewValue is not string)
            return;

        border.SizeChanged += OnSizeChanged;
        border.Loaded += OnLoaded;
        border.Unloaded += OnUnloaded;
        if (border.IsLoaded)
            Track(border);

        Update(border);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((Border)sender);

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        Track((Border)sender);
        Update((Border)sender); // the theme may have changed while it was away
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Untrack((Border)sender);

    private static void Track(Border border)
    {
        if (!_subscribed)
        {
            ThemeManager.ThemeChanged += (_, _) => UpdateAll();
            _subscribed = true;
        }

        Live.RemoveAll(w => !w.TryGetTarget(out var b) || ReferenceEquals(b, border));
        Live.Add(new WeakReference<Border>(border));
    }

    private static void Untrack(Border border) => Live.RemoveAll(w => !w.TryGetTarget(out var b) || ReferenceEquals(b, border));

    private static void UpdateAll()
    {
        foreach (var weak in Live.ToArray())
        {
            if (weak.TryGetTarget(out var border))
                Update(border);
        }
    }

    private static void Update(Border border)
    {
        if (GetKey(border) is not { } key)
            return;

        var limit = Math.Min(border.ActualWidth, border.ActualHeight) / 2;
        if (key == "Pill")
        {
            if (limit > 0 && border.CornerRadius != new CornerRadius(limit))
                border.CornerRadius = new CornerRadius(limit);

            return;
        }

        if (border.TryFindResource(key) is not CornerRadius token)
            return;

        // Before the first layout there is no size to clamp to yet: a pill token must not be applied raw.
        if (limit <= 0 && Math.Max(Math.Max(token.TopLeft, token.TopRight), Math.Max(token.BottomRight, token.BottomLeft)) > 100)
            return;

        double Cap(double r) => limit > 0 ? Math.Min(r, limit) : r;
        var clamped = new CornerRadius(Cap(token.TopLeft), Cap(token.TopRight), Cap(token.BottomRight), Cap(token.BottomLeft));
        if (border.CornerRadius != clamped)
            border.CornerRadius = clamped;

        if (GetRoundClip(border) && border.ActualWidth > 0 && border.ActualHeight > 0)
            border.Clip = new RectangleGeometry(new Rect(0, 0, border.ActualWidth, border.ActualHeight), clamped.TopLeft, clamped.TopLeft);
    }
}

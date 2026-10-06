using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GameLauncher.Services;

namespace GameLauncher.Controls;

/// <summary>The full-bleed cover behind a console theme's home. When <see cref="Source"/> changes the new image fades in over the old one
/// (Console-tile: a plain crossfade; Console-ribbon: it also drifts in from <see cref="DriftFrom"/> down to 1, slowly). A null source
/// leaves the plain background. With Windows' "Animation effects" off the swap is instant.</summary>
public sealed class ArtBackdrop : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ArtBackdrop), new PropertyMetadata(null, OnSourceChanged));

    /// <summary>The scale a new image starts at while it fades in (1.08 in Console-ribbon, 1 for none).</summary>
    public static readonly DependencyProperty DriftFromProperty = DependencyProperty.Register(
        nameof(DriftFrom), typeof(double), typeof(ArtBackdrop), new PropertyMetadata(1.0));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double DriftFrom
    {
        get => (double)GetValue(DriftFromProperty);
        set => SetValue(DriftFromProperty, value);
    }

    public ArtBackdrop() => ClipToBounds = true;

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ArtBackdrop)d).Swap(e.NewValue as ImageSource);

    private void Swap(ImageSource? source)
    {
        if (source is null)
        {
            // No cover (an icon-fallback game): the plain background shows. Fade what was there out rather than blinking it off.
            foreach (var old in Children.OfType<Image>().ToList())
                FadeOutAndRemove(old);

            return;
        }

        // Hovering through the strip swaps the cover many times a second: keep only the cover on show beneath the new one, or the fading layers pile up.
        while (Children.Count > 1)
            Children.RemoveAt(0);

        var layer = new Image { Source = source, Stretch = Stretch.UniformToFill, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };
        // A full-window cover drifting in: cheap linear scaling (no BitmapCache - re-rasterising a full-window cover on every change cost far more than it saved).
        RenderOptions.SetBitmapScalingMode(layer, BitmapScalingMode.Linear);

        var scale = new ScaleTransform(1, 1);
        layer.RenderTransform = scale;
        Children.Add(layer);

        if (!SystemParameters.ClientAreaAnimation)
        {
            RemoveBelow(layer);
            return;
        }

        var duration = ThemeManager.Token("DurBackground") is Duration d && d.HasTimeSpan ? d.TimeSpan : TimeSpan.FromMilliseconds(260);
        var ease = (ThemeManager.Token(DriftFrom > 1 ? "EaseSoft" : "EaseStandard") as IEasingFunction) ?? new CubicEase { EasingMode = EasingMode.EaseOut };

        // Drift variant: opacity finishes in the first 35% of the duration while the scale takes the whole time.
        var fade = new DoubleAnimation(0, 1, new Duration(DriftFrom > 1 ? TimeSpan.FromTicks((long)(duration.Ticks * 0.35)) : duration))
        { EasingFunction = ease };
        fade.Completed += (_, _) => RemoveBelow(layer);
        layer.BeginAnimation(OpacityProperty, fade);
        if (DriftFrom > 1)
        {
            var drift = new DoubleAnimation(DriftFrom, 1, new Duration(duration)) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, drift);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, drift);
        }
    }

    private void RemoveBelow(UIElement layer)
    {
        var at = Children.IndexOf(layer);
        for (var i = at - 1; i >= 0; i--)
            Children.RemoveAt(i);
    }

    private void FadeOutAndRemove(Image layer)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Children.Remove(layer);
            return;
        }

        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(260)));
        fade.Completed += (_, _) => Children.Remove(layer);
        layer.BeginAnimation(OpacityProperty, fade);
    }
}

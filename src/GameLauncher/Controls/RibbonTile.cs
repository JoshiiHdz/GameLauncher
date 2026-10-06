using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using GameLauncher.Services;

namespace GameLauncher.Controls;

/// <summary>An icon in the Console-ribbon strip. It is a square of <c>ActiveRibbonItemSize</c> (144 px, 70 small) that grows to
/// <c>ActiveRibbonItemFocusedSize</c> (216, 108) while <see cref="IsSelectedItem"/> is true; width and height animate together, so the
/// neighbours slide over to make room. With Windows' animations off it jumps. The numbers and timing are the theme's tokens.</summary>
public sealed class RibbonTile : Button
{
    public static readonly DependencyProperty IsSelectedItemProperty = DependencyProperty.Register(
        nameof(IsSelectedItem), typeof(bool), typeof(RibbonTile), new PropertyMetadata(false, (d, e) => ((RibbonTile)d).Resize(animate: true)));

    public bool IsSelectedItem
    {
        get => (bool)GetValue(IsSelectedItemProperty);
        set => SetValue(IsSelectedItemProperty, value);
    }

    public RibbonTile()
    {
        Loaded += (_, _) =>
        {
            ThemeLayout.Changed += OnLayoutChanged;
            Resize(animate: false);
        };
        Unloaded += (_, _) => ThemeLayout.Changed -= OnLayoutChanged;
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => Resize(animate: false);

    private double Target() =>
        TryFindResource(IsSelectedItem ? "ActiveRibbonItemFocusedSize" : "ActiveRibbonItemSize") is double size ? size : IsSelectedItem ? 216 : 144;

    private void Resize(bool animate)
    {
        var target = Target();
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        if (!animate || !IsLoaded || !SystemParameters.ClientAreaAnimation || double.IsNaN(Width))
        {
            Width = target;
            Height = target;
            return;
        }

        var duration = ThemeManager.Token("DurFocus") is Duration d ? d : new Duration(TimeSpan.FromMilliseconds(320));
        var ease = ThemeManager.Token("EaseFocus") as IEasingFunction ?? new CubicEase { EasingMode = EasingMode.EaseOut };
        var from = ActualWidth > 0 ? ActualWidth : Width;
        var animation = new DoubleAnimation(from, target, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            Width = target;
            Height = target;
        };
        BeginAnimation(WidthProperty, animation);
        BeginAnimation(HeightProperty, animation);
    }
}

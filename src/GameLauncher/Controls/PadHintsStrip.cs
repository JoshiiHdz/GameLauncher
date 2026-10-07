using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameLauncher.ViewModels;

namespace GameLauncher.Controls;

/// <summary>The line of hints: each one is the button (drawn like the real thing, see <see cref="PadGlyphView"/>) and what it does, in a row that wraps if the window is narrow.</summary>
public sealed class PadHintsStrip : ContentControl
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable<PadHintItem>), typeof(PadHintsStrip), new PropertyMetadata(null, (d, _) => ((PadHintsStrip)d).Rebuild()));

    public IEnumerable<PadHintItem>? Items
    {
        get => (IEnumerable<PadHintItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public PadHintsStrip()
    {
        Focusable = false;
        IsTabStop = false;
        IsHitTestVisible = false;
    }

    private void Rebuild()
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var item in Items ?? [])
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 22, 2), VerticalAlignment = VerticalAlignment.Center };
            foreach (var glyph in item.Glyphs)
                row.Children.Add(new PadGlyphView { Glyph = glyph, Margin = new Thickness(0, 0, 3, 0) });

            row.Children.Add(new TextBlock
            {
                Text = item.Label,
                Margin = new Thickness(item.Glyphs.Count > 0 ? 5 : 0, 0, 0, 0),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            });
            wrap.Children.Add(row);
        }

        Content = wrap;

        // The pill round the strip was sized for the (empty) strip it had a moment ago: tell the layout this one is different, all the way up, or the pill stays a speck.
        InvalidateMeasure();
        InvalidateArrange();
        for (var parent = VisualTreeHelper.GetParent(this); parent is UIElement element; parent = VisualTreeHelper.GetParent(element))
            element.InvalidateMeasure();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GameLauncher.ViewModels;

namespace GameLauncher.Controls;

/// <summary>A controller button drawn the way it looks on an Xbox pad: A green, B red, X blue and Y yellow as coloured discs with their letter; Start as a circle with three horizontal
/// lines; the bumpers and triggers as small rounded labels; the right stick as a ring with its cap. Drawn in code, at 22 pixels, so the same glyph serves the hints bar and the
/// keyboard's help line.</summary>
public sealed class PadGlyphView : ContentControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(PadGlyph), typeof(PadGlyphView), new PropertyMetadata(PadGlyph.A, (d, _) => ((PadGlyphView)d).Rebuild()));

    public PadGlyph Glyph
    {
        get => (PadGlyph)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public PadGlyphView()
    {
        Focusable = false;
        IsTabStop = false;
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        Rebuild();
    }

    /// <summary>The disc colour of a face button (Xbox's own): A green, B red, X blue, Y yellow; null for the others.</summary>
    internal static Color? FaceColor(PadGlyph glyph) => glyph switch
    {
        PadGlyph.A => Color.FromRgb(0x3D, 0xB5, 0x49),
        PadGlyph.B => Color.FromRgb(0xE5, 0x39, 0x2B),
        PadGlyph.X => Color.FromRgb(0x2D, 0x7F, 0xF9),
        PadGlyph.Y => Color.FromRgb(0xF5, 0xB8, 0x00),
        _ => null,
    };

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private void Rebuild()
    {
        Content = Glyph switch
        {
            PadGlyph.A or PadGlyph.B or PadGlyph.X or PadGlyph.Y => Face(),
            PadGlyph.Start => StartButton(),
            PadGlyph.RightStick => Stick(),
            _ => Shoulder(PadHintItem.NameOf(Glyph)),
        };
        System.Windows.Automation.AutomationProperties.SetName(this, PadHintItem.NameOf(Glyph));
    }

    private static TextBlock Letter(string text, Brush foreground, double size = 12.5) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeights.Bold,
        Foreground = foreground,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private UIElement Face()
    {
        var color = FaceColor(Glyph)!.Value;
        var grid = new Grid { Width = 22, Height = 22 };
        grid.Children.Add(new Ellipse { Fill = new SolidColorBrush(color), Stroke = Brush(0x55, 0, 0, 0), StrokeThickness = 1 });
        // Yellow needs a dark letter to be read; the others take a white one.
        grid.Children.Add(Letter(Glyph.ToString(), Glyph == PadGlyph.Y ? Brush(0xFF, 0x1B, 0x1B, 0x1B) : Brush(0xFF, 0xFF, 0xFF, 0xFF)));
        return grid;
    }

    /// <summary>Start (Menu): a circle with three horizontal lines.</summary>
    private static UIElement StartButton()
    {
        var grid = new Grid { Width = 22, Height = 22 };
        grid.Children.Add(new Ellipse { Fill = Brush(0xFF, 0x2B, 0x2F, 0x38), Stroke = Brush(0xDD, 0xFF, 0xFF, 0xFF), StrokeThickness = 1.5 });
        foreach (var y in new[] { 7.0, 11.0, 15.0 })
        {
            grid.Children.Add(new Line
            {
                X1 = 6, X2 = 16, Y1 = y, Y2 = y,
                Stroke = Brush(0xFF, 0xFF, 0xFF, 0xFF),
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            });
        }

        return grid;
    }

    private static UIElement Shoulder(string name) => new Border
    {
        Height = 20,
        MinWidth = 30,
        Padding = new Thickness(7, 0, 7, 0),
        CornerRadius = new CornerRadius(7),
        Background = Brush(0xFF, 0x3A, 0x3F, 0x4B),
        BorderBrush = Brush(0x55, 0xFF, 0xFF, 0xFF),
        BorderThickness = new Thickness(1),
        Child = Letter(name, Brush(0xFF, 0xFF, 0xFF, 0xFF), 11),
    };

    /// <summary>The right stick: a ring with a cap and an R.</summary>
    private static UIElement Stick()
    {
        var grid = new Grid { Width = 22, Height = 22 };
        grid.Children.Add(new Ellipse { Fill = Brush(0xFF, 0x2B, 0x2F, 0x38), Stroke = Brush(0xDD, 0xFF, 0xFF, 0xFF), StrokeThickness = 1.5 });
        grid.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = Brush(0xFF, 0x4A, 0x50, 0x5E) });
        grid.Children.Add(Letter("R", Brush(0xFF, 0xFF, 0xFF, 0xFF), 10));
        return grid;
    }
}

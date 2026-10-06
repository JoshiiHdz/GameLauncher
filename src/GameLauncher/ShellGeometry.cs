using System.Windows;

namespace GameLauncher;

/// <summary>Where an element sits inside another, for anchoring menus next to the thing they belong to.</summary>
internal static class ShellGeometry
{
    public static Rect RectOf(FrameworkElement element, UIElement relativeTo)
    {
        try
        {
            var origin = element.TransformToVisual(relativeTo).Transform(new Point(0, 0));
            return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        }
    }
}

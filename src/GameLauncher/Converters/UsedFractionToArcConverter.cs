using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GameLauncher.Converters;

/// <summary>Turns a drive's 0-1 used fraction into the arc drawn over its track ring. WPF has no conic-gradient or arc-percentage primitive, so the geometry is built by
/// hand: start at twelve o'clock and sweep clockwise, which is what makes a part-full drive read as part-full at a glance instead of needing the tooltip.
///
/// The arc has to sit exactly on the track ring or it runs outside it (it did: the numbers were those of one theme's ring, used in both). The ring is an Ellipse of a
/// given width and stroke in a cell of a given size, so its centre is the middle of the cell and its stroke's centre line is (width - stroke) / 2 from that centre.
/// The ConverterParameter says those four numbers - "cellWidth,cellHeight,ellipseWidth,strokeThickness" - and the arc is drawn on the same line.</summary>
public sealed class UsedFractionToArcConverter : IValueConverter
{
    public static readonly UsedFractionToArcConverter Instance = new();

    // With no parameter: a 32 px ring with a 2.5 px stroke in a 36 px cell (the first design).
    private static readonly (double CellWidth, double CellHeight, double EllipseWidth, double Stroke) Default = (36, 36, 32, 2.5);

    /// <summary>The centre and radius of the arc for a ring described as "cellWidth,cellHeight,ellipseWidth,strokeThickness" (null or unreadable: the default ring).</summary>
    internal static (double CenterX, double CenterY, double Radius) Circle(object? parameter)
    {
        var (cellWidth, cellHeight, ellipseWidth, stroke) = Default;
        if (parameter is string text)
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 4
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var e)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                (cellWidth, cellHeight, ellipseWidth, stroke) = (w, h, e, s);
        }

        return (cellWidth / 2, cellHeight / 2, Math.Max(0.5, (ellipseWidth - stroke) / 2));
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value switch
        {
            double d => d,
            float f => f,
            _ => 0d,
        };

        if (double.IsNaN(fraction) || fraction <= 0)
            return Geometry.Empty;

        var (centerX, centerY, radius) = Circle(parameter);

        // A full circle can't be expressed as a single ArcSegment (start and end point coincide, and
        // the arc degenerates to nothing) - at that point it IS just the ellipse.
        if (fraction >= 1)
            return new EllipseGeometry(new System.Windows.Point(centerX, centerY), radius, radius);

        var sweepDegrees = fraction * 360;
        var start = new System.Windows.Point(centerX, centerY - radius);
        var endAngle = (sweepDegrees - 90) * Math.PI / 180;
        var end = new System.Windows.Point(
            centerX + radius * Math.Cos(endAngle),
            centerY + radius * Math.Sin(endAngle));

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new System.Windows.Size(radius, radius),
            IsLargeArc = sweepDegrees > 180,
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

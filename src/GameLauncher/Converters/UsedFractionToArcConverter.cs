using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GameLauncher.Converters;

/// <summary>Turns a drive's 0-1 used fraction into the arc the rail draws over its track ring. WPF has
/// no conic-gradient or arc-percentage primitive, so the geometry is built by hand: start at twelve
/// o'clock and sweep clockwise, which is what makes a part-full drive read as part-full at a glance
/// instead of needing the tooltip.</summary>
public sealed class UsedFractionToArcConverter : IValueConverter
{
    public static readonly UsedFractionToArcConverter Instance = new();

    // Matches the Ellipse the arc is drawn over (32px across, centred in a 36px cell).
    private const double Radius = 16;
    private const double CenterX = 18;
    private const double CenterY = 18;

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

        // A full circle can't be expressed as a single ArcSegment (start and end point coincide, and
        // the arc degenerates to nothing) - at that point it IS just the ellipse.
        if (fraction >= 1)
            return new EllipseGeometry(new System.Windows.Point(CenterX, CenterY), Radius, Radius);

        var sweepDegrees = fraction * 360;
        var start = new System.Windows.Point(CenterX, CenterY - Radius);
        var endAngle = (sweepDegrees - 90) * Math.PI / 180;
        var end = new System.Windows.Point(
            CenterX + Radius * Math.Cos(endAngle),
            CenterY + Radius * Math.Sin(endAngle));

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new System.Windows.Size(Radius, Radius),
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

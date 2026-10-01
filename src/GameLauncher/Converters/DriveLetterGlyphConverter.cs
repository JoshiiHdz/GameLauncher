using System.Globalization;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>"G:" -> "G". Inside the rail's 32px ring there's only room for the letter itself, and the
/// colon reads as noise at that size; the full "G: ..." form still appears in the tooltip and every
/// other place the drive is named.</summary>
public sealed class DriveLetterGlyphConverter : IValueConverter
{
    public static readonly DriveLetterGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value as string is { Length: > 0 } letter ? letter.TrimEnd(':', '\\') : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

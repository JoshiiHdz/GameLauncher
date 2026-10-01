using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>An install path -> the drive it lives on ("G:"), for the hero's meta line. Mirrors
/// LibraryViewModel's own DriveLetterOf so the hero names a drive exactly the way the rail and the
/// drive filter do.</summary>
public sealed class InstallDirToDriveConverter : IValueConverter
{
    public static readonly InstallDirToDriveConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string installDir || string.IsNullOrWhiteSpace(installDir))
            return string.Empty;

        try
        {
            return Path.GetPathRoot(installDir)?.TrimEnd('\\') ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

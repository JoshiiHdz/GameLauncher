using System.Globalization;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>Label for the update banner's action button - same button and command either way
/// (LibraryViewModel.DownloadUpdateCommand), only the text changes while a download is in flight.</summary>
public sealed class UpdatingButtonTextConverter : IValueConverter
{
    public static readonly UpdatingButtonTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Updating..." : "Update Now";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

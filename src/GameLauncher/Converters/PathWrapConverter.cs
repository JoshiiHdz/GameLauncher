using System.Globalization;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>Lets a long folder path wrap instead of being cut off. WPF only breaks a line at a space, and a path such as
/// "C:\Program Files (x86)\Steam\steamapps\common\Some Game" has few, so a card too narrow for it just clipped the end. A zero-width space after each
/// separator is a place to break that no one can see. Used only for what is shown: the path itself (to open it, to copy it) is never changed.</summary>
public sealed class PathWrapConverter : IValueConverter
{
    public static readonly PathWrapConverter Instance = new();

    private const char Break = '\u200B';

    public static string Wrappable(string? path) =>
        string.IsNullOrEmpty(path) ? string.Empty : path.Replace("\\", "\\" + Break).Replace("/", "/" + Break);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Wrappable(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

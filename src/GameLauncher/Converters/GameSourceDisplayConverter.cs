using System.Globalization;
using System.Windows.Data;
using GameLauncher.Models;

namespace GameLauncher.Converters;

/// <summary>GameSource.ToString() reads fine for Steam/Epic/Manual but wrong for acronyms
/// (Gog -> "Gog", Ea -> "Ea") - this fixes those for display.</summary>
public sealed class GameSourceDisplayConverter : IValueConverter
{
    public static readonly GameSourceDisplayConverter Instance = new();

    /// <summary>The display name for code that needs the text itself rather than a binding.</summary>
    public static string Name(GameSource source) => source switch
    {
        GameSource.Gog => "GOG",
        GameSource.Ea => "EA",
        GameSource.Ubisoft => "Ubisoft Connect",
        GameSource.BattleNet => "Battle.net",
        GameSource.Rockstar => "Rockstar Games",
        GameSource.AmazonGames => "Amazon Games",
        _ => source.ToString(),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is GameSource source ? Name(source) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

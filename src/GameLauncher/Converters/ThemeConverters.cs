using System.Globalization;
using System.Windows.Data;
using GameLauncher.Models;

namespace GameLauncher.Converters;

/// <summary>How a launcher is named and abbreviated on cards, badges and pills in the themes. Manual games (a folder with no launcher)
/// read "No launcher" with the initial "M", as in the sidebar.</summary>
public static class LauncherText
{
    public static string Name(GameSource source) =>
        source == GameSource.Manual ? "No launcher" : GameSourceDisplayConverter.Name(source);

    public static string Initials(GameSource source) => source switch
    {
        GameSource.Steam => "S",
        GameSource.Epic => "E",
        GameSource.Gog => "G",
        GameSource.Xbox => "X",
        GameSource.Ea => "EA",
        GameSource.Ubisoft => "U",
        GameSource.BattleNet => "B",
        GameSource.Rockstar => "R",
        GameSource.AmazonGames => "A",
        _ => "M",
    };

    /// <summary>"1.5 h tracked · Today", or "Not played yet".</summary>
    public static string Tracked(GameEntry game) =>
        game.HasPlayTime ? $"{game.PlayTimeDisplay} · {game.LastPlayedDisplay}" : "Not played yet";

    /// <summary>"Steam · 1.5 h · Today", or "Steam · Not played yet" (the Axis card caption).</summary>
    public static string CardMeta(GameEntry game) => game.HasPlayTime
        ? $"{Name(game.Source)} · {Hours(game)} · {game.LastPlayedDisplay}"
        : $"{Name(game.Source)} · Not played yet";

    private static string Hours(GameEntry game) => game.PlayTimeDisplay.Replace(" tracked", string.Empty, StringComparison.Ordinal);
}

public sealed class LauncherNameConverter : IValueConverter
{
    public static readonly LauncherNameConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is GameSource source ? LauncherText.Name(source) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LauncherUpperConverter : IValueConverter
{
    public static readonly LauncherUpperConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is GameSource source ? LauncherText.Name(source).ToUpperInvariant() : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LauncherInitialsConverter : IValueConverter
{
    public static readonly LauncherInitialsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is GameSource source ? LauncherText.Initials(source) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A card caption that follows the game's play time: bind (Source, TotalPlaySeconds, LastPlayedDisplay) and pass "card" for
/// "Steam · 1.5 h · Today" or "tracked" for "1.5 h tracked · Today". The extra bindings only make it refresh when play time changes.</summary>
public sealed class GameMetaConverter : IMultiValueConverter
{
    public static readonly GameMetaConverter Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not GameEntry game)
            return string.Empty;

        return parameter as string == "tracked" ? LauncherText.Tracked(game) : LauncherText.CardMeta(game);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Whether a game card is the one the theme has focused: bind (the card's game, the focused game, the card's row, the focused row).
/// The rows are optional; when given they must match too, since one game can sit in two shelves.</summary>
public sealed class SameGameConverter : IMultiValueConverter
{
    public static readonly SameGameConverter Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is null || !ReferenceEquals(values[0], values[1]))
            return false;

        return values.Length < 4 || Equals(values[2], values[3]);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when an enum value (or any object) equals the parameter's text: <c>{Binding Tab, Converter=..., ConverterParameter=Home}</c>.</summary>
public sealed class EqualsParameterConverter : IValueConverter
{
    public static readonly EqualsParameterConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A top-level tab is "active" only while no page covers it: bind (Tab, IsPageOpen) and pass the tab's name.</summary>
public sealed class TabActiveConverter : IMultiValueConverter
{
    public static readonly TabActiveConverter Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length >= 2 && values[1] is false && string.Equals(values[0]?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"12 games" / "1 game" for a count.</summary>
public sealed class GameCountTextConverter : IValueConverter
{
    public static readonly GameCountTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n ? (n == 1 ? "1 game" : $"{n} games") : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The drive letter and size of a game's install, "C: · 14 GB", or just "C:" while the size is not measured.</summary>
public sealed class DriveAndSizeConverter : IValueConverter
{
    public static readonly DriveAndSizeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not GameEntry game)
            return string.Empty;

        var drive = InstallDirToDriveConverter.Instance.Convert(game.InstallDir, typeof(string), null, culture) as string ?? string.Empty;
        return game.HasInstallSize ? $"{drive} · {game.InstallSizeDisplay}" : drive;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when an enum value (or any object) equals the parameter's text, else collapsed: shows one of several tab bodies.</summary>
public sealed class TabVisibilityConverter : IValueConverter
{
    public static readonly TabVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the text is not empty, else collapsed (a detail line that may not exist).</summary>
public sealed class HasTextVisibilityConverter : IValueConverter
{
    public static readonly HasTextVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is not null, else collapsed.</summary>
public sealed class HasValueVisibilityConverter : IValueConverter
{
    public static readonly HasValueVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A number minus the parameter (a maximum height that follows the window).</summary>
public sealed class SubtractConverter : IValueConverter
{
    public static readonly SubtractConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? Math.Max(0, d - p) : double.PositiveInfinity;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

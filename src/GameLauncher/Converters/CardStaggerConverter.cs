using System.Globalization;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>The delay, in milliseconds, before the card at this position (an ItemsControl's AlternationIndex) comes in: the first dozen follow one another, the rest arrive
/// together with the last of them, so a long library does not take seconds to appear.</summary>
public sealed class CardStaggerConverter : IValueConverter
{
    public static readonly CardStaggerConverter Instance = new();

    /// <summary>Milliseconds between one card and the next, and how many cards are staggered.</summary>
    internal const double StepMs = 26;
    internal const int Staggered = 12;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index ? Math.Min(Math.Max(index, 0), Staggered) * StepMs : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

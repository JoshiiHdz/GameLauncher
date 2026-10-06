using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace GameLauncher.Converters;

/// <summary>A symbol name ("Desktop24") to the WPF-UI symbol; a name that is not a symbol gives a plain desktop icon rather than an error.</summary>
public sealed class SymbolNameConverter : IValueConverter
{
    public static readonly SymbolNameConverter Instance = new();

    public static SymbolRegular Parse(string? name) =>
        Enum.TryParse<SymbolRegular>(name, ignoreCase: false, out var symbol) && Enum.IsDefined(symbol) ? symbol : SymbolRegular.Desktop24;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Parse(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

using System.Windows;
using System.Windows.Controls;
using GameLauncher.Services;

namespace GameLauncher;

/// <summary>The three theme option cards in Settings &gt; Appearance (see ThemePicker.xaml).</summary>
public partial class ThemePicker : UserControl
{
    public ThemePicker()
    {
        InitializeComponent();
        if (!ThemeManager.IsAvailable(ThemeId.ConsoleTile))
        {
            TileCard.Visibility = Visibility.Collapsed; // the Xbox style is not offered yet
            Cards.Columns = 2;
        }
    }
}

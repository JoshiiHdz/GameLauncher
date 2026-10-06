using System.Windows.Controls;

namespace GameLauncher;

/// <summary>The Play time page (see PlayTimePage.xaml). Its DataContext is a <see cref="ViewModels.PlayTimeViewModel"/>.</summary>
public partial class PlayTimePage : UserControl
{
    public PlayTimePage()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowFallbackWhenNoCover();
    }

    // The same rule the ribbon's posters use: a game with no cover art shows the launcher's logo on a plain panel instead of a stretched icon.
    private void ShowFallbackWhenNoCover()
    {
        if (DataContext is ViewModels.PlayTimeViewModel { Game.IsCoverArt: false })
        {
            Art.Visibility = System.Windows.Visibility.Collapsed;
            Fallback.Visibility = System.Windows.Visibility.Visible;
        }
    }
}

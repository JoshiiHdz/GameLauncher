using GameLauncher.ViewModels;
using Wpf.Ui.Controls;

namespace GameLauncher;

// No SteamGridDB/IGDB credential inputs here (removed at the owner's request: with the project's relay giving every install IGDB
// access and a built-in SteamGridDB key already shipping, the fields were unused input space, not a capability anyone needs from
// this window). LibraryViewModel.SteamGridDbApiKey/IgdbClientId/SaveIgdbClientSecret still exist and are still fully tested - an
// override remains possible by hand-editing settings.json (SteamGridDbApiKey, IgdbClientId) and IgdbCredentialStore's protected
// file, exactly as a build with no relay embedded already required before any Settings UI existed for it.
public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(LibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

using System.Windows;
using System.Windows.Controls;
using GameLauncher.Behaviors;
using GameLauncher.ViewModels;

namespace GameLauncher;

// No SteamGridDB/IGDB credential inputs here (removed at the owner's request: with the project's relay giving every install IGDB
// access and a built-in SteamGridDB key already shipping, the fields were unused input space, not a capability anyone needs from
// this page). LibraryViewModel.SteamGridDbApiKey/IgdbClientId/SaveIgdbClientSecret still exist and are still fully tested - an
// override remains possible by hand-editing settings.json (SteamGridDbApiKey, IgdbClientId) and IgdbCredentialStore's protected
// file, exactly as a build with no relay embedded already required before any Settings UI existed for it.

/// <summary>Settings, grouped: a category list on the left and one category's panel on the right. All settings bind to the library
/// view model (this page's DataContext); the only thing the page itself does is show the chosen panel.</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        CategoryList.SelectedIndex = 0;
        ShowSelectedSection(animate: false);

        // Coming back to Settings lands on the category the user left it on (kept on the library for as long as the app runs).
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is LibraryViewModel library)
                CategoryList.SelectedIndex = Math.Clamp(library.SettingsCategory, 0, CategoryList.Items.Count - 1);
        };
    }

    /// <summary>The panel for each category, in the same order as the list.</summary>
    private FrameworkElement[] Sections => new FrameworkElement[]
    {
        GeneralSection, LibrarySection, AppearanceSection, PerformanceSection, IntegrationsSection, BackupSection, AboutSection,
    };

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelectedSection(animate: true);

    private void ShowSelectedSection(bool animate)
    {
        var index = CategoryList.SelectedIndex;
        if (index < 0)
            return;

        if (DataContext is LibraryViewModel library)
            library.SettingsCategory = index;

        var sections = Sections;
        for (var i = 0; i < sections.Length; i++)
            sections[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

        if (animate && IsLoaded)
            Motion.PlayFade(sections[index]);
    }
}

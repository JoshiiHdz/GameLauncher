using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>
/// In-app pages. Settings, Stats, Optimize, drive storage, game details and "Pick a game" no longer open their own windows: one of
/// them at a time fills the main window's content area, beside the sidebar, with a Back arrow. The window picks the control for
/// <see cref="CurrentPage"/> by its type (see the DataTemplates in MainWindow.xaml).
/// </summary>
public partial class LibraryViewModel
{
    public const string SettingsPageKey = "settings";
    public const string StatsPageKey = "stats";
    public const string OptimizePageKey = "optimize";
    public const string PickPageKey = "pick";
    public const string StoragePageKey = "storage";
    public const string DetailsPageKey = "details";
    public const string MyPcPageKey = "mypc";

    /// <summary>The page now showing over the library, or null when the library itself is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageOpen))]
    private object? _currentPage;

    [ObservableProperty]
    private string _currentPageTitle = "";

    /// <summary>Which page is open (one of the *PageKey constants), so the sidebar can mark its entry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsPageOpen), nameof(IsStatsPageOpen), nameof(IsOptimizePageOpen), nameof(IsPickPageOpen), nameof(IsMyPcPageOpen))]
    private string _currentPageKey = "";

    public bool IsPageOpen => CurrentPage is not null;
    public bool IsSettingsPageOpen => CurrentPageKey == SettingsPageKey;
    public bool IsStatsPageOpen => CurrentPageKey == StatsPageKey;
    public bool IsOptimizePageOpen => CurrentPageKey == OptimizePageKey;
    public bool IsPickPageOpen => CurrentPageKey == PickPageKey;
    public bool IsMyPcPageOpen => CurrentPageKey == MyPcPageKey;

    /// <summary>The Settings category last open (an index into its category list), so reopening Settings lands where the user left off.
    /// Not saved: a new session starts on General.</summary>
    internal int SettingsCategory { get; set; }

    /// <summary>What to do once the open page has closed - read the answer back out of its view model, launch a game, and so on.</summary>
    private Action? _pageClosed;

    /// <summary>Shows a page, closing whichever one was open first (which runs that page's own close step).</summary>
    internal void OpenPage(string key, string title, object page, Action? onClosed = null)
    {
        ClosePage();
        _pageClosed = onClosed;
        CurrentPageKey = key;
        CurrentPageTitle = title;
        CurrentPage = page;
    }

    /// <summary>Closes the open page and returns to the library. Safe to call when no page is open.</summary>
    [RelayCommand]
    internal void ClosePage()
    {
        if (CurrentPage is not { } page)
            return;

        var closed = _pageClosed;
        _pageClosed = null;
        CurrentPage = null;
        CurrentPageKey = "";
        CurrentPageTitle = "";

        try
        {
            closed?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error("A page's close step failed.", ex);
            StatusText = $"Something went wrong closing that page: {ex.Message}";
        }
        finally
        {
            // Pages that own timers or event hookups (storage, details) release them here, whichever way the page closed.
            if (!ReferenceEquals(page, this))
                (page as IDisposable)?.Dispose();
        }
    }

    [RelayCommand]
    private void ShowSettings()
    {
        if (IsSettingsPageOpen)
            return;

        OpenPage(SettingsPageKey, "Settings", this);
    }

    /// <summary>Opens Settings on its Performance category (the Optimize card and the before-launch switches), for the Optimize shortcut.</summary>
    [RelayCommand]
    private void ShowOptimizeSettings() => OpenSettingsAt(PerformanceSettingsCategory);

    /// <summary>Opens Settings on a given category (an index into its list), even when Settings is already open on another.</summary>
    internal void OpenSettingsAt(int category)
    {
        SettingsCategory = category;
        if (IsSettingsPageOpen)
            ClosePage(); // reopen so the page lands on the category

        ShowSettings();
    }

    // The Settings category list, in order (see SettingsPage.xaml).
    internal const int GeneralSettingsCategory = 0;
    internal const int LibrarySettingsCategory = 1;
    internal const int AppearanceSettingsCategory = 2;
    internal const int PerformanceSettingsCategory = 3;
    internal const int BackupSettingsCategory = 4;
    internal const int AboutSettingsCategory = 5;
}

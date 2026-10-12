using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>The Optimize page (free RAM, clear storage clutter) and the "free up memory before launching a game" setting.</summary>
public partial class LibraryViewModel
{
    /// <summary>The memory side of Optimize; replaced in tests so nothing real is ever trimmed.</summary>
    internal IMemoryOptimizer MemoryOptimizerService { get; set; } = new MemoryOptimizer();

    /// <summary>The clutter Optimize knows about; replaced in tests so nothing real is ever scanned or deleted.</summary>
    internal Func<IReadOnlyList<CleanCategory>> CleanCategoriesFactory { get; set; } = StorageCleaner.DefaultCategories;

    /// <summary>Test seam: receives the page's view model instead of opening the window.</summary>
    internal Action<OptimizeViewModel>? OptimizeDialogForTest { get; set; }

    /// <summary>Off by default: trimming other apps' memory is harmless but not something to do behind someone's back.</summary>
    [ObservableProperty]
    private bool _optimizeBeforeLaunch;

    partial void OnOptimizeBeforeLaunchChanged(bool value)
    {
        _settings.OptimizeBeforeLaunch = value;
        _settingsService.Save(_settings);
        RefreshGameTweakRows(); // the list shows the same switch
    }

    /// <summary>The install folder of the game being played, if any - memory trimming must never touch that game.</summary>
    private IReadOnlyList<string> RunningGameFolders() =>
        _runningGameId is { } id && _allGames.FirstOrDefault(g => g.Id == id) is { } running && !string.IsNullOrWhiteSpace(running.InstallDir)
            ? [running.InstallDir]
            : [];

    /// <summary>The fraction of RAM that must be available for the pre-launch trim to be skipped.</summary>
    internal const double MemoryPressureThreshold = 0.30;

    internal void FreeMemoryBeforeLaunch()
    {
        try
        {
            // Trimming only helps when memory is actually short: when plenty is free it just makes the apps that were trimmed page their memory back in. So it is
            // skipped (and logged) unless less than a third of the RAM is available.
            var status = MemoryOptimizerService.GetStatus();
            if (status.TotalBytes > 0 && (double)status.AvailableBytes / status.TotalBytes >= MemoryPressureThreshold)
            {
                Logger.Info($"Optimize before launch: skipped, plenty of memory is free ({status.Describe()}).");
                return;
            }

            var result = MemoryOptimizerService.Trim(RunningGameFolders());
            Logger.Info($"Optimize before launch: {result.Describe()}");
        }
        catch (Exception ex)
        {
            // Never let a memory tidy-up stop a game from starting.
            Logger.Warn("Optimize before launch failed; launching anyway.", ex);
        }
    }

    /// <summary>Makes what the gaming health check reads. Tests replace it.</summary>
    internal static Func<IPowerPlans, Func<MemoryStatus>, IAdvisorFacts> AdvisorFactsFactory { get; set; } = (plans, memory) => new SystemAdvisorFacts(plans, memory);

    /// <summary>Makes the startup entries the Optimize page lists. Tests replace it, so no test reads or changes a real PC's startup list.</summary>
    internal static Func<IStartupSource> StartupSourceFactory { get; set; } = () => new SystemStartupSource();

    /// <summary>What measures Windows.old for the Optimize page. Tests replace it (the real one walks a very large folder).</summary>
    internal static Func<long?> WindowsOldSizerFactory { get; set; } = () => WindowsOld.Measure();

    private OptimizeViewModel CreateOptimizeViewModel() =>
        new(MemoryOptimizerService, CleanCategoriesFactory(), RunningGameFolders(),
            question => AppShell.Confirm("Clear these files?", question, yes: "Clear files", no: "Cancel", warning: true),
            () => _allGames.ToList(),
            game => UninstallGameCommand.Execute(game),
            _runningGameId) { Tweaks = this, AdvisorFacts = AdvisorFactsFactory(PowerPlansService, () => MemoryOptimizerService.GetStatus()), StartupSource = StartupSourceFactory(), WindowsOldSizer = WindowsOldSizerFactory };

    /// <summary>The Optimize tabs, shown inside Settings > Performance. Made when Optimize is first asked for (or shown in Settings), and again whenever the Optimize
    /// shortcut is used, so a visit starts from what the PC is like now.</summary>
    [ObservableProperty]
    private OptimizeViewModel? _optimizePage;

    /// <summary>Makes the page's view model and reads what it shows (health check, startup apps, Windows.old; what Windows has for each tweak). Reading only.</summary>
    internal OptimizeViewModel PrepareOptimizePage()
    {
        var page = CreateOptimizeViewModel();
        OptimizePage = page;
        page.CheckGamingHealth(); // (also reads what Windows has for each tweak)
        page.RefreshStartup();
        _ = page.RefreshWindowsOldAsync(); // measured in the background: the page does not wait for it
        return page;
    }

    /// <summary>Settings > Performance was shown: make the page if it has not been, otherwise read the things that can have changed since it was last looked at.</summary>
    internal void ShowOptimizeTabs()
    {
        try
        {
            if (OptimizePage is null)
                PrepareOptimizePage();
            else
                OptimizePage.Revisit();
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't prepare the Performance tabs.", ex);
            StatusText = $"Couldn't read the Performance details: {ex.Message}";
        }
    }

    /// <summary>The Optimize shortcut (sidebar, palette, the PlayStation tile, a controller button): there is no separate page any more - Optimize is the Performance
    /// section of Settings, with its tabs.</summary>
    [RelayCommand]
    private void ShowOptimize()
    {
        try
        {
            var page = PrepareOptimizePage();
            if (OptimizeDialogForTest is { } seam)
                seam(page);
            else
                OpenSettingsAt(PerformanceSettingsCategory);
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't open the Optimize page.", ex);
            StatusText = $"Couldn't open the Optimize page: {ex.Message}";
        }
    }
}

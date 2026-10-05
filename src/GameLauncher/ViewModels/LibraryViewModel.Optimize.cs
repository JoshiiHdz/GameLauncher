using System.Diagnostics;
using System.IO;
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

    /// <summary>Test seam: stands in for starting a tool or link from the page.</summary>
    internal Action<string>? OpenOptimizeToolForTest { get; set; }

    /// <summary>Off by default: trimming other apps' memory is harmless but not something to do behind someone's back.</summary>
    [ObservableProperty]
    private bool _optimizeBeforeLaunch;

    partial void OnOptimizeBeforeLaunchChanged(bool value)
    {
        _settings.OptimizeBeforeLaunch = value;
        _settingsService.Save(_settings);
    }

    /// <summary>The install folder of the game being played, if any - memory trimming must never touch that game.</summary>
    private IReadOnlyList<string> RunningGameFolders() =>
        _runningGameId is { } id && _allGames.FirstOrDefault(g => g.Id == id) is { } running && !string.IsNullOrWhiteSpace(running.InstallDir)
            ? [running.InstallDir]
            : [];

    private void FreeMemoryBeforeLaunch()
    {
        try
        {
            var result = MemoryOptimizerService.Trim(RunningGameFolders());
            Logger.Info($"Optimize before launch: {result.Describe()}");
        }
        catch (Exception ex)
        {
            // Never let a memory tidy-up stop a game from starting.
            Logger.Warn("Optimize before launch failed; launching anyway.", ex);
        }
    }

    private void OpenOptimizeTool(string target)
    {
        try
        {
            if (OpenOptimizeToolForTest is { } seam)
                seam(target);
            else
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Logger.Warn($"Optimize: couldn't open '{target}'.", ex);
            StatusText = $"Couldn't open {target}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ShowOptimize()
    {
        try
        {
            var page = new OptimizeViewModel(MemoryOptimizerService, CleanCategoriesFactory(), RunningGameFolders(), OptimizeTools.Detect(),
                question => AppShell.Confirm("Clear these files?", question, yes: "Clear files", no: "Cancel", warning: true),
                OpenOptimizeTool);

            if (OptimizeDialogForTest is { } seam)
                seam(page);
            else
                OpenPage(OptimizePageKey, "Optimize", page);
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't open the Optimize page.", ex);
            StatusText = $"Couldn't open the Optimize page: {ex.Message}";
        }
    }
}

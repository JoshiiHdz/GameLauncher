using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>The tabs of the Optimize page.</summary>
public enum OptimizeTab
{
    /// <summary>What happens before a game starts (memory, power plan, Windows settings, services) and memory right now.</summary>
    Boost,

    /// <summary>The read-only gaming health check.</summary>
    Health,

    /// <summary>Programs that start with Windows.</summary>
    Startup,

    /// <summary>Storage clutter, the previous Windows installation and the space saver.</summary>
    CleanUp,
}

/// <summary>One tick-box row on the Optimize page: a kind of leftover, how much of it there is, and whether to clear it.</summary>
public sealed partial class CleanItem : ObservableObject
{
    public CleanItem(CleanCategory category)
    {
        Category = category;
        _isSelected = category.SelectedByDefault;
    }

    public CleanCategory Category { get; }

    public string Name => Category.Name;

    public string Description => Category.Description;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>"Not scanned yet", "Nothing to clear", or "1.2 GB in 350 files" - and "Cleared 1.2 GB" afterwards.</summary>
    [ObservableProperty]
    private string _sizeText = "Not scanned yet";

    public long Bytes { get; set; }
}

/// <summary>The Optimize page: free up RAM, clear storage clutter (always with a size shown first and a question before anything is
/// deleted) and the games worth uninstalling. Works through interfaces and delegates so none of it needs a real PC to test.</summary>
public sealed partial class OptimizeViewModel : ObservableObject
{
    private readonly IMemoryOptimizer _memory;
    private readonly IReadOnlyList<string> _protectedFolders;
    private readonly Func<string, bool> _confirm;
    private readonly Func<IReadOnlyList<GameEntry>>? _games;
    private readonly Action<GameEntry>? _uninstall;
    private readonly string? _runningGameId;

    /// <summary>The library, for the game tweaks card (its switches and buttons live there, shared with Settings); null when the page was made without one.</summary>
    public LibraryViewModel? Tweaks { get; init; }

    public bool HasTweaks => Tweaks is not null;

    // ---- The page is four tabs instead of one long scroll: what to do before a game, the health check, startup apps, clean-up ----------

    private bool _scanRequested;

    /// <summary>Measures the storage clutter once, the first time the page is in view (an Optimize page nobody looks at does not walk the disk).</summary>
    internal void EnsureScanned()
    {
        if (_scanRequested)
            return;

        _scanRequested = true;
        ScanCommand.Execute(null);
    }

    /// <summary>The page is shown again: read what can have changed since (the health check, the startup list, what Windows has for each tweak, memory). The storage scan
    /// is the person's to repeat.</summary>
    internal void Revisit()
    {
        CheckGamingHealth();
        RefreshStartup();
        RefreshMemory();
    }

    /// <summary>The tab showing. Game boost is first because it is the thing the page is for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoostTab), nameof(IsHealthTab), nameof(IsStartupTab), nameof(IsCleanUpTab))]
    private OptimizeTab _tab = OptimizeTab.Boost;

    public bool IsBoostTab
    {
        get => Tab == OptimizeTab.Boost;
        set { if (value) Tab = OptimizeTab.Boost; }
    }

    public bool IsHealthTab
    {
        get => Tab == OptimizeTab.Health;
        set { if (value) Tab = OptimizeTab.Health; }
    }

    public bool IsStartupTab
    {
        get => Tab == OptimizeTab.Startup;
        set { if (value) Tab = OptimizeTab.Startup; }
    }

    public bool IsCleanUpTab
    {
        get => Tab == OptimizeTab.CleanUp;
        set { if (value) Tab = OptimizeTab.CleanUp; }
    }

    /// <summary>The tab names carry a number where there is one to give: what the health check says can be improved, how many programs start with Windows.</summary>
    public string HealthTabText => AdvisorRows.Count(r => r.IsAdvice) is > 0 and var n ? $"Health check ({n})" : "Health check";

    public string StartupTabText => StartupRows.Count > 0 ? $"Startup apps ({StartupRows.Count(r => r.IsOn)})" : "Startup apps";

    // ---- The previous Windows installation (Windows.old): its size, and the way to remove it with Windows' own tool ----------------

    /// <summary>Measures the folder (null when there is none). Null here leaves the card out.</summary>
    public Func<long?>? WindowsOldSizer { get; init; }

    /// <summary>Opens Windows' own cleanup; replaced in tests.</summary>
    public Action OpenWindowsCleanupAction { get; init; } = WindowsOld.OpenWindowsCleanup;

    [ObservableProperty]
    private bool _hasWindowsOld;

    [ObservableProperty]
    private string _windowsOldText = "";

    [RelayCommand]
    internal async Task RefreshWindowsOldAsync()
    {
        if (WindowsOldSizer is not { } sizer)
            return;

        try
        {
            HasWindowsOld = false;
            var size = await Task.Run(sizer);
            if (size is { } bytes)
            {
                WindowsOldText = $"About {ByteFormat.Size(bytes)}. A Windows upgrade keeps your previous installation for a few days in case you want to go back; Windows removes it by itself after about ten days. To get the space back sooner, use Windows' own cleanup (it needs administrator rights, and going back is no longer possible afterwards).";
                HasWindowsOld = true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Optimize: measuring Windows.old failed.", ex);
        }
    }

    [RelayCommand]
    private void OpenWindowsCleanup() => OpenWindowsCleanupAction();

    // ---- Startup apps: what starts with Windows, each with a switch (the Task Manager way: reversible, nothing deleted) ----------

    /// <summary>The startup entries; null leaves the card out.</summary>
    public IStartupSource? StartupSource { get; init; }

    public bool HasStartup => StartupSource is not null;

    public ObservableCollection<StartupRow> StartupRows { get; } = new();

    [ObservableProperty]
    private string _startupSummary = "";

    [RelayCommand]
    internal void RefreshStartup()
    {
        StartupRows.Clear();
        if (StartupSource is null)
            return;

        var service = new StartupAppsService(StartupSource);
        foreach (var app in service.List())
        {
            var row = new StartupRow(app, service, UpdateStartupSummary);
            StartupRows.Add(row);
            _ = row.LoadAboutAsync(); // what the program says it is: read from the file in the background
        }

        UpdateStartupSummary();
    }

    private void UpdateStartupSummary()
    {
        OnPropertyChanged(nameof(StartupTabText));
        var on = StartupRows.Count(r => r.IsOn);
        StartupSummary = StartupRows.Count == 0
            ? "Nothing is set to start with Windows."
            : $"{on} of {StartupRows.Count} start when you sign in. Switching one off only stops it starting with Windows - it is not uninstalled, and you can switch it back on.";
    }

    // ---- The gaming health check: what is worth changing on this PC, read-only ----------------------------------------------

    /// <summary>What the check reads; null leaves the card out.</summary>
    public IAdvisorFacts? AdvisorFacts { get; init; }

    public bool HasAdvisor => AdvisorFacts is not null;

    public ObservableCollection<AdvisorRow> AdvisorRows { get; } = new();

    /// <summary>Reads the PC again and lists what it found: what can be improved first, then what is already right, then what is only worth knowing.</summary>
    [RelayCommand]
    internal void CheckGamingHealth()
    {
        _ = Tweaks?.RefreshTweakStatesAsync(); // what Windows has for each tweak is read again with the check
        AdvisorRows.Clear();
        if (AdvisorFacts is not null)
        {
            foreach (var finding in GamingAdvisor.Check(AdvisorFacts).OrderBy(f => f.Level switch { AdviceLevel.Advice => 0, AdviceLevel.Good => 1, _ => 2 }))
                AdvisorRows.Add(new AdvisorRow(finding, Tweaks, CheckGamingHealth));
        }

        OnPropertyChanged(nameof(HealthTabText));
    }

    /// <summary>True when the launcher already runs with administrator rights (then a refused file is simply in use, and asking again would change nothing).</summary>
    internal Func<bool> IsAdministrator { get; set; } = ElevatedCleaner.IsAdministrator;

    /// <summary>Clears these categories (by id) with administrator rights after Windows' own permission prompt; null when that was declined.</summary>
    internal Func<IReadOnlyList<string>, IReadOnlyDictionary<string, CleanOutcome>?> RunElevated { get; set; } = ElevatedCleaner.Run;

    /// <param name="protectedFolders">Install folders whose programs memory trimming must leave alone (the running game's).</param>
    /// <param name="confirm">The "are you sure?" question; true to go ahead.</param>
    /// <param name="games">The library's games, for the Space saver list; null leaves the list out.</param>
    /// <param name="uninstall">Starts a game's uninstall (the usual Uninstall command).</param>
    public OptimizeViewModel(IMemoryOptimizer memory, IReadOnlyList<CleanCategory> categories, IReadOnlyList<string> protectedFolders,
        Func<string, bool> confirm,
        Func<IReadOnlyList<GameEntry>>? games = null, Action<GameEntry>? uninstall = null, string? runningGameId = null)
    {
        _games = games;
        _uninstall = uninstall;
        _runningGameId = runningGameId;
        _memory = memory;
        _protectedFolders = protectedFolders;
        _confirm = confirm;
        foreach (var category in categories)
        {
            var item = new CleanItem(category);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CleanItem.IsSelected))
                    OnSelectionChanged();
            };
            Items.Add(item);
        }

        OnSelectionChanged();
        RefreshMemory();
        RefreshSpaceSaver();
    }

    public ObservableCollection<CleanItem> Items { get; } = new();

    /// <summary>"Select all" while some kind is unticked; "Select none" once every kind is ticked.</summary>
    [ObservableProperty]
    private string _selectAllText = "Select all";

    /// <summary>Ticks every kind of clutter, or - when they are all ticked already - unticks them all.</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var tick = !Items.All(i => i.IsSelected);
        foreach (var item in Items)
            item.IsSelected = tick;
    }

    private void OnSelectionChanged()
    {
        SelectAllText = Items.Count > 0 && Items.All(i => i.IsSelected) ? "Select none" : "Select all";

        // What would be cleared follows the ticks, once there is a scan to go by.
        if (!IsBusy && HasStorageBar)
        {
            StorageText = SelectedSummary();
            StorageBarFraction = Items.Any(i => i.IsSelected && i.Bytes > 0) ? 1 : 0;
        }
    }

    /// <summary>Big games not played for a while, biggest first (see <see cref="SpaceSaver"/>).</summary>
    public ObservableCollection<SpaceSaverRow> SpaceSaverRows { get; } = new();

    /// <summary>False when the page was opened without a library (the card is hidden then).</summary>
    public bool HasSpaceSaver => _games is not null;

    [ObservableProperty]
    private string _spaceSaverSummary = "";

    [ObservableProperty]
    private string _memoryText = "";

    [ObservableProperty]
    private double _memoryUsedFraction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMemoryResult))]
    private string _memoryResultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStorageResult))]
    private string _storageResultText = "";

    public bool HasMemoryResult => !string.IsNullOrEmpty(MemoryResultText);

    public bool HasStorageResult => !string.IsNullOrEmpty(StorageResultText);


    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FreeUpMemoryCommand), nameof(ScanCommand), nameof(CleanCommand))]
    private bool _isBusy;

    // ---- storage: one bar and one line, the same shape as memory's ---------------------------------------------------------------

    /// <summary>True once something has been measured, so the bar has something to show.</summary>
    [ObservableProperty]
    private bool _hasStorageBar;

    /// <summary>How much of the clutter that was measured is still there: full after a scan, shrinking as files are cleared, and ending
    /// at what could not be removed (files in use). The memory bar shows how full memory is; this one shows how much clutter is left.</summary>
    [ObservableProperty]
    private double _storageBarFraction;

    /// <summary>The line under the bar (memory's own text sits in the same place): what was found, what it is clearing, what is left.</summary>
    [ObservableProperty]
    private string _storageText = "";

    public void RefreshMemory()
    {
        var status = _memory.GetStatus();
        MemoryText = status.Describe();
        MemoryUsedFraction = status.UsedFraction;
    }

    /// <summary>Works the list out again from the library as it is now: sizes are measured in the background, so it fills in over a minute or two.</summary>
    [RelayCommand]
    private void RefreshSpaceSaver()
    {
        SpaceSaverRows.Clear();
        if (_games is null)
            return;

        var all = _games();
        var suggestions = SpaceSaver.Suggest(all, DateTime.UtcNow, runningGameId: _runningGameId);
        foreach (var suggestion in suggestions)
            SpaceSaverRows.Add(new SpaceSaverRow(suggestion, game => _uninstall?.Invoke(game)));

        var waiting = SpaceSaver.Unmeasured(all);
        var measuring = waiting > 0 ? $" {waiting:N0} {(waiting == 1 ? "game is" : "games are")} still being measured - check again in a moment." : "";
        SpaceSaverSummary = suggestions.Count == 0
            ? $"Nothing found: no big game has gone {SpaceSaver.UnplayedDays} days unplayed.{measuring}"
            : $"Uninstalling all of these would free {ByteFormat.Size(suggestions.Sum(x => x.Bytes))}.{measuring}";
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task FreeUpMemoryAsync()
    {
        IsBusy = true;
        try
        {
            MemoryResultText = "Freeing up memory...";
            var result = await Task.Run(() => _memory.Trim(_protectedFolders));
            Logger.Info($"Optimize: free up memory (by hand) - {result.Describe()}");
            MemoryResultText = result.Describe();
            RefreshMemory();
        }
        catch (Exception ex)
        {
            Logger.Error("Optimize: freeing memory failed.", ex);
            MemoryResultText = $"Couldn't free up memory: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Measures every kind of clutter. Deletes nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            StorageResultText = "";
            StorageText = "Scanning...";
            StorageBarFraction = 0;
            HasStorageBar = true;
            foreach (var item in Items)
            {
                ScanOutcome outcome;
                try
                {
                    outcome = await Task.Run(() => item.Category.Scan(CancellationToken.None));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Optimize: scanning '{item.Name}' failed.", ex);
                    item.Bytes = 0;
                    item.SizeText = "Couldn't be scanned";
                    continue;
                }

                item.Bytes = outcome.Bytes;
                item.SizeText = outcome.Files == 0 ? "Nothing to clear" : $"{ByteFormat.Size(outcome.Bytes)} in {outcome.Files:N0} {(outcome.Files == 1 ? "file" : "files")}";
            }

            StorageText = SelectedSummary(); // under the bar, like memory's line; the result line below the buttons is for what a clear-out did
            StorageBarFraction = Items.Any(i => i.IsSelected && i.Bytes > 0) ? 1 : 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>"3.4 GB selected" - what Clean would free, going by the last scan.</summary>
    public string SelectedSummary()
    {
        var selected = Items.Where(i => i.IsSelected && i.Bytes > 0).ToList();
        return selected.Count == 0 ? "Nothing selected to clear." : $"{ByteFormat.Size(selected.Sum(i => i.Bytes))} selected to clear.";
    }

    /// <summary>Clears the ticked kinds. Asks first, listing exactly what will go, and does nothing on No.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CleanAsync()
    {
        var chosen = Items.Where(i => i.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            StorageResultText = "Tick at least one kind of clutter to clear.";
            return;
        }

        var question = "Delete these files?\n\n" + string.Join("\n", chosen.Select(i => $"  - {i.Name} ({i.SizeText})"))
            + "\n\nFiles that are in use are skipped. This cannot be undone.";
        if (!_confirm(question))
        {
            StorageResultText = "Nothing was deleted.";
            return;
        }

        IsBusy = true;
        HasStorageBar = true;
        StorageBarFraction = 1;
        StorageText = "Measuring what is there...";
        try
        {
            // Before: measured again right now, not taken from the scan on page open, so the bar starts from the truth.
            long before = 0;
            foreach (var item in chosen)
                before += await MeasureAsync(item);

            long freed = 0;
            int skipped = 0;
            long done = 0;
            var step = 0;
            var refused = new List<CleanItem>(); // kinds that had files Windows would not let a normal run delete
            foreach (var item in chosen)
            {
                step++;
                StorageText = $"Clearing {item.Name}... ({step} of {chosen.Count})";
                long reportedByThisKind = 0;
                void Report(long bytes)
                {
                    Interlocked.Add(ref reportedByThisKind, bytes);
                    var total = Interlocked.Add(ref done, bytes);
                    StorageBarFraction = before > 0 ? Math.Clamp(1.0 - (double)total / before, 0, 1) : 0; // the bar drains as files go
                }

                try
                {
                    var category = item.Category;
                    var outcome = await Task.Run(() => category.CleanReporting is { } reporting
                        ? reporting(CancellationToken.None, Report)
                        : category.Clean(CancellationToken.None));
                    freed += outcome.FreedBytes;
                    skipped += outcome.Skipped;

                    // A kind that finishes in one go (the Recycle Bin) reports nothing along the way: credit it now.
                    var unreported = outcome.FreedBytes - Interlocked.Read(ref reportedByThisKind);
                    if (unreported > 0)
                        Report(unreported);

                    item.SizeText = outcome.Deleted == 0 ? "Nothing was cleared" : $"Cleared {ByteFormat.Size(outcome.FreedBytes)}";
                    if (outcome.Skipped > 0)
                        refused.Add(item);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Optimize: cleaning '{item.Name}' failed.", ex);
                    item.SizeText = "Couldn't be cleared";
                }
            }

            // Files a normal run is refused (the NVIDIA and AMD driver folders ask Windows' own "Continue" permission prompt too): with the person's say-so the launcher
            // starts a copy of itself with administrator rights to clear just those kinds. Declined or not, the numbers below are measured, not assumed.
            string? elevatedNote = null;
            if (refused.Count > 0 && !IsAdministrator())
            {
                var names = string.Join("\n", refused.Select(i => $"  - {i.Name}"));
                if (_confirm($"Some files in these were refused - Windows wants administrator permission to remove them:\n\n{names}\n\nClear them with administrator rights? Windows will ask you to allow it."))
                {
                    StorageText = "Clearing with administrator rights...";
                    var elevated = await Task.Run(() => RunElevated(refused.Select(i => i.Category.Id).ToList()));
                    if (elevated is null)
                    {
                        elevatedNote = $" Those files stay: {ElevatedCleaner.LastProblem ?? "administrator permission was not given"}.";
                    }
                    else
                    {
                        foreach (var item in refused)
                        {
                            if (!elevated.TryGetValue(item.Category.Id, out var more))
                                continue;

                            freed += more.FreedBytes;
                            if (more.Deleted > 0)
                                item.SizeText = $"Cleared {ByteFormat.Size(more.FreedBytes)} with administrator rights";
                        }

                        skipped = elevated.Values.Sum(o => o.Skipped);
                    }
                }
            }

            // After: measured again, so the bar ends on what is really left (files in use stay), not on what was meant to go.
            StorageText = "Checking what is left...";
            long after = 0;
            foreach (var item in chosen)
            {
                var left = await MeasureAsync(item);
                item.Bytes = left;
                after += left;
                if (left > 0 && item.SizeText.StartsWith("Cleared", StringComparison.Ordinal))
                    item.SizeText += $" - {ByteFormat.Size(left)} left (in use)";
            }

            StorageBarFraction = before > 0 ? Math.Clamp((double)after / before, 0, 1) : 0;
            StorageText = after == 0 ? $"All {ByteFormat.Size(before)} cleared." : $"{ByteFormat.Size(after)} left of {ByteFormat.Size(before)}.";
            var summary = $"Freed {ByteFormat.Size(freed)}." + (skipped > 0 ? $" {skipped:N0} files were in use or protected and were left alone." : "") + elevatedNote;

            Logger.Info($"Optimize: cleared {ByteFormat.Size(freed)} - {ByteFormat.Size(before)} before, {ByteFormat.Size(after)} after (skipped {skipped} files in use or protected).");
            StorageResultText = summary;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>How many bytes a kind of clutter holds right now; 0 when it cannot be measured.</summary>
    private static async Task<long> MeasureAsync(CleanItem item)
    {
        try
        {
            return (await Task.Run(() => item.Category.Scan(CancellationToken.None))).Bytes;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Optimize: measuring '{item.Name}' failed.", ex);
            return 0;
        }
    }
}

/// <summary>One row of the Space saver list: a big game that has not been played for a while, with a button that starts its uninstall.</summary>
public sealed partial class SpaceSaverRow
{
    private readonly Action<GameEntry> _uninstall;

    public SpaceSaverRow(SpaceSaverSuggestion suggestion, Action<GameEntry> uninstall)
    {
        Game = suggestion.Game;
        SizeText = ByteFormat.Size(suggestion.Bytes);
        LastPlayedText = suggestion.LastPlayedText;
        _uninstall = uninstall;
    }

    public GameEntry Game { get; }

    public string Name => Game.Name;

    public string SizeText { get; }

    public string LastPlayedText { get; }

    [RelayCommand]
    private void Uninstall() => _uninstall(Game);
}

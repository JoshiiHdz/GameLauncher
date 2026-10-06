using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

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
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Optimize: cleaning '{item.Name}' failed.", ex);
                    item.SizeText = "Couldn't be cleared";
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
            var summary = $"Freed {ByteFormat.Size(freed)}." + (skipped > 0 ? $" {skipped:N0} files were in use or protected and were left alone." : "");

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

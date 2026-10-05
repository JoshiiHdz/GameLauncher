using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Converters;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>The Ctrl+K palette (and the global hotkey that opens it from anywhere): games to launch and commands to run.</summary>
public partial class LibraryViewModel
{
    /// <summary>Test seam: receives the palette instead of opening the window, and returns what to "choose" (or null to dismiss).</summary>
    internal Func<CommandPaletteViewModel, PaletteItem?>? CommandPaletteDialogForTest { get; set; }

    private bool _paletteOpen;

    /// <summary>On by default; a switch exists because a global hotkey is a system-wide behaviour someone may not want.</summary>
    [ObservableProperty]
    private bool _globalHotkeyEnabled = true;

    partial void OnGlobalHotkeyEnabledChanged(bool value)
    {
        _settings.GlobalHotkeyEnabled = value;
        _settingsService.Save(_settings);
    }

    private static PaletteItem Command(string title, string keywords, Action run, string subtitle = "Command", bool showWhenEmpty = false) =>
        new(title, subtitle, keywords, PaletteKind.Command, run, ShowWhenEmpty: showWhenEmpty);

    /// <summary>Everything the palette can find: every visible game, and the app's own commands.</summary>
    internal IReadOnlyList<PaletteItem> BuildPaletteItems()
    {
        var now = SessionClock();
        var items = new List<PaletteItem>();

        var recentIds = _allGames.Where(g => !g.Hidden && g.HasPlayTime).OrderByDescending(g => g.LastPlayedUtc).Take(5)
            .Select(g => g.Id).ToHashSet();

        foreach (var game in _allGames.Where(g => !g.Hidden && IsSourceEnabled(g.Source)))
        {
            var recent = game.LastPlayedUtc is { } last && now - last <= TimeSpan.FromDays(7);
            var played = game.HasPlayTime ? $"{PlayTimeFormat.Duration(game.TotalPlaySeconds)} tracked" : "Not played yet";
            var launcher = GameSourceDisplayConverter.Name(game.Source);
            var captured = game;
            items.Add(new PaletteItem(game.Name, $"{launcher} - {played}", string.Join(' ', game.Collections.Append(launcher)), PaletteKind.Game,
                () => Launch(captured), Bonus: (recent ? 40 : 0) + (game.Favorite ? 20 : 0),
                ShowWhenEmpty: recentIds.Contains(game.Id)));
        }

        items.Add(Command("Pick a game for me", "random shuffle suggest surprise", () => PickGameCommand.Execute(null), showWhenEmpty: true));
        items.Add(Command(ControllerModeAvailable ? "Controller mode" : "Controller mode (coming soon)", "big screen controller gamepad couch tv fullscreen full screen", () => ShowBigScreenCommand.Execute(null), showWhenEmpty: true));
        items.Add(Command("Show stats", "statistics playtime play time most played charts", () => ShowStatsCommand.Execute(null), showWhenEmpty: true));
        items.Add(Command("Optimize: free up memory and storage", "ram clean cleanup temp cache speed boost", () => ShowOptimizeCommand.Execute(null), showWhenEmpty: true));
        items.Add(Command("Rescan library", "refresh scan find games", () => RescanCommand.Execute(null)));
        items.Add(Command("Go to all games", "home library view", () => SelectViewCommand.Execute("all")));
        items.Add(Command("Go to favorites", "starred view", () => SelectViewCommand.Execute("favorites")));
        items.Add(Command("Go to recently played", "history recent view", () => SelectViewCommand.Execute("recent")));
        items.Add(Command("Go to not played yet", "unplayed backlog never played view", () => SelectViewCommand.Execute("unplayed")));
        if (HasDuplicates)
            items.Add(Command("Go to games installed twice", "duplicates copies view", () => SelectViewCommand.Execute("duplicates")));
        items.Add(Command("Open settings", "preferences options configuration", () => ShowSettingsCommand.Execute(null)));
        items.Add(Command("Export library data...", "backup save settings favorites notes", () => ExportLibraryDataCommand.Execute(null)));
        items.Add(Command("Import library data...", "restore backup load settings favorites notes", () => ImportLibraryDataCommand.Execute(null)));
        items.Add(Command("Send feedback or report a bug", "bug issue problem report idea", () => ReportBugCommand.Execute(null)));
        items.Add(Command("Shut down this PC", "power off turn off computer", () => ShutdownPcCommand.Execute(null), "Command - asks first"));
        return items;
    }

    [RelayCommand]
    private void ShowCommandPalette()
    {
        if (_paletteOpen)
            return; // a second hotkey press (or Ctrl+K) while it is open must not stack another

        _paletteOpen = true;
        PaletteItem? chosen;
        try
        {
            var palette = new CommandPaletteViewModel(BuildPaletteItems());
            if (CommandPaletteDialogForTest is { } seam)
                chosen = seam(palette);
            else
            {
                AppShell.ShowModal("Command palette", new CommandPaletteDialog(palette));
                chosen = palette.Chosen;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't open the command palette.", ex);
            StatusText = $"Couldn't open the command palette: {ex.Message}";
            return;
        }
        finally
        {
            _paletteOpen = false;
        }

        // Run only now that the palette is gone, so a dialog or a launching game is never left behind it.
        try
        {
            chosen?.Execute();
        }
        catch (Exception ex)
        {
            Logger.Error($"Command palette: '{chosen?.Title}' failed.", ex);
            StatusText = $"Couldn't run \"{chosen?.Title}\": {ex.Message}";
        }
    }
}

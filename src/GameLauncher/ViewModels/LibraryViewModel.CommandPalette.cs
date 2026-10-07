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
        new(title, subtitle, keywords, PaletteKind.Command, run, ShowWhenEmpty: showWhenEmpty, Detail: "Command");

    /// <summary>The settings the palette can find by name: title, words people might type, the Settings section that holds it (its place in the list) and that section's name.</summary>
    private static readonly (string Title, string Keywords, int Section, string SectionName)[] SettingsPaletteEntries =
    [
        ("Start with Windows", "startup boot login sign in autostart", GeneralSettingsCategory, "General"),
        ("Open maximized", "full screen window size start maximize", GeneralSettingsCategory, "General"),
        ("Check for updates on startup", "updates automatic version", GeneralSettingsCategory, "General"),
        ("Hide to tray while gaming", "tray minimize system tray background", GeneralSettingsCategory, "General"),
        ("Track games opened outside the launcher", "play time external track detect", GeneralSettingsCategory, "General"),
        ("Global shortcut (Ctrl+Alt+Space)", "hotkey command palette anywhere shortcut keyboard", GeneralSettingsCategory, "General"),
        ("Watched folders", "add folder scan manual games", LibrarySettingsCategory, "Library"),
        ("Drives to search", "drives disk ignore stop searching", LibrarySettingsCategory, "Library"),
        ("Desktop shortcut and cover art", "desktop shortcut covers artwork scanning", LibrarySettingsCategory, "Library"),
        ("Change theme", "theme look style axis playstation xbox appearance dark colors", AppearanceSettingsCategory, "Appearance"),
        ("Vibrant background", "background accent color glow", AppearanceSettingsCategory, "Appearance"),
        ("Focus play (high performance power plan)", "power plan performance ultimate high priority battery plugged in game boost", PerformanceSettingsCategory, "Performance"),
        ("Free up memory before launching a game", "memory ram optimize trim clean", PerformanceSettingsCategory, "Performance"),
        ("Clear cache and refresh covers", "cache covers artwork reset images", BackupSettingsCategory, "Data and backup"),
        ("Reset all settings and cache", "reset factory defaults clear everything start over", BackupSettingsCategory, "Data and backup"),
        ("Check for updates now", "update version upgrade new release", AboutSettingsCategory, "About and support"),
        ("Open logs folder", "log diagnostics troubleshoot support problem", AboutSettingsCategory, "About and support"),
    ];

    /// <summary>Everything the palette can find: every visible game, and the app's own commands.</summary>
    internal IReadOnlyList<PaletteItem> BuildPaletteItems()
    {
        var now = SessionClock();
        var items = new List<PaletteItem>();

        var recentIds = _allGames.Where(g => !g.Hidden && g.HasPlayTime).OrderByDescending(g => g.LastPlayedUtc).Take(IsControllerMode ? 12 : 5) // no keyboard to type with: offer more
            .Select(g => g.Id).ToHashSet();

        foreach (var game in _allGames.Where(g => !g.Hidden && IsSourceEnabled(g.Source)))
        {
            var recent = game.LastPlayedUtc is { } last && now - last <= TimeSpan.FromDays(7);
            var played = game.HasPlayTime ? $"{PlayTimeFormat.Duration(game.TotalPlaySeconds)} tracked" : "Not played yet";
            var launcher = GameSourceDisplayConverter.Name(game.Source);
            var captured = game;
            items.Add(new PaletteItem(game.Name, $"{launcher} - {played}", string.Join(' ', game.Collections.Append(launcher)), PaletteKind.Game,
                // Axis plays the game straight away; the Xbox style opens its details; the PlayStation style, whose home shows the details, brings the game up there with Play highlighted.
                () =>
                {
                    if (IsAxisTheme)
                        Launch(captured);
                    else if (IsConsoleRibbonTheme && ShowGameOnRibbonHome is { } showOnHome)
                        showOnHome(captured);
                    else
                        ShowGameDetailsCommand.Execute(captured);
                },
                Bonus: (recent ? 40 : 0) + (game.Favorite ? 20 : 0), ShowWhenEmpty: recentIds.Contains(game.Id) || (IsControllerMode && game.Favorite),
                Cover: game.IsCoverArt ? game.Icon : null, Detail: LauncherText.Name(game.Source)));
        }

        // Commands (and the settings you can type to find) are an Axis feature. In the console themes the palette is only a way to find a game, since everything
        // else is a button on screen.
        if (!ThemeState.Instance.ShortcutsEnabled)
            return items;

        items.Add(Command("Pick a game for me", "random shuffle suggest surprise", () => PickGameCommand.Execute(null), showWhenEmpty: true));
        items.Add(Command("Show stats", "statistics playtime play time most played charts", () => ShowStatsCommand.Execute(null), showWhenEmpty: true));
        items.Add(Command("My PC: specs, motherboard, BIOS and Secure Boot", "computer hardware cpu processor gpu graphics ram memory motherboard mobo bios uefi tpm system information", () => ShowMyPcCommand.Execute(null), showWhenEmpty: true));
        items.Add(Command("Optimize: free up memory and storage", "ram clean cleanup temp cache speed boost", () => ShowOptimizeCommand.Execute(null), showWhenEmpty: true));
        items.Add(Command("Rescan library", "refresh scan find games", () => RescanCommand.Execute(null)));
        items.Add(Command("Go to all games", "home library view", () => SelectViewCommand.Execute("all")));
        items.Add(Command("Go to favorites", "starred view", () => SelectViewCommand.Execute("favorites")));
        items.Add(Command("Go to recently played", "history recent view", () => SelectViewCommand.Execute("recent")));
        items.Add(Command("Go to not played yet", "unplayed backlog never played view", () => SelectViewCommand.Execute("unplayed")));
        if (HasDuplicates)
            items.Add(Command("Go to games installed twice", "duplicates copies view", () => SelectViewCommand.Execute("duplicates")));
        items.Add(Command("Open settings", "preferences options configuration", () => ShowSettingsCommand.Execute(null)));
        if (IsAxisTheme)
            items.Add(Command("Controller mode (F11)", "gamepad controller pad xbox playstation big screen couch tv full screen switch", () => EnterControllerModeFromAxisCommand.Execute(null)));

        // Each setting can be found by name and takes you to the section that holds it.
        foreach (var (title, keywords, section, sectionName) in SettingsPaletteEntries)
        {
            var category = section;
            items.Add(new PaletteItem("Settings: " + title, "Opens Settings > " + sectionName, keywords, PaletteKind.Command, () => OpenSettingsAt(category), Detail: "Setting"));
        }

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

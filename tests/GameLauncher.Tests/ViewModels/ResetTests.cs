using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>"Clear cache" and "Reset all settings and cache": what they remove, and - just as important - what they never touch.</summary>
public sealed class ResetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Reset-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_directory, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "file.bin"), "data");
        return path;
    }

    /// <summary>A saved library with one game whose cover was chosen automatically, one whose cover the user picked, and one with
    /// a recorded "asked the catalogs, no match" - then loaded the way the app loads it.</summary>
    private LibraryViewModel Library(AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        settings.Overrides["auto"] = new GameOverride
        {
            Favorite = true,
            Notes = "keep me",
            Artwork = new ArtworkSelection { Provider = ArtworkProvider.Igdb, IsUserSelected = false, ProviderGameId = "1" },
            Identity = new GameIdentityRecord { LastAttempt = new ResolutionAttempt { Outcome = LookupOutcome.NoMatch, At = DateTime.UtcNow } },
        };
        settings.Overrides["mine"] = new GameOverride
        {
            Artwork = new ArtworkSelection { Provider = ArtworkProvider.UserLocalFile, IsUserSelected = true, AssetId = "abc", AssetExtension = ".png" },
        };
        new SettingsService(_directory).Save(settings);
        return new LibraryViewModel(new SettingsService(_directory), new PendingUpdateNotesService(_directory));
    }

    [Fact]
    public void ClearingTheCache_RemovesCoversAndIcons_ButNotTheUsersOwnCovers()
    {
        var covers = Folder("CoverArtCache");
        Directory.CreateDirectory(Path.Combine(covers, "Igdb"));
        File.WriteAllText(Path.Combine(covers, "Igdb", "a.png"), "x");
        var icons = Folder("IconCache");
        var custom = Folder("CustomCovers");
        var logs = Folder("logs");
        var vm = Library();

        var removed = vm.ClearCacheCore();

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(covers));
        Assert.False(Directory.Exists(icons));
        Assert.True(File.Exists(Path.Combine(custom, "file.bin"))); // a cover the user chose lives here and stays
        Assert.True(File.Exists(Path.Combine(logs, "file.bin")));
    }

    [Fact]
    public void ClearingTheCache_ForgetsAutomaticCoversAndOldNoMatchAnswers_ButKeepsEverythingTheUserDecided()
    {
        var vm = Library();

        vm.ClearCacheCore();

        var saved = new SettingsService(_directory).Load();
        Assert.Null(saved.Overrides["auto"].Artwork);                 // chosen automatically: looked up again
        Assert.Null(saved.Overrides["auto"].Identity!.LastAttempt);    // "no match yet" no longer blocks the next lookup
        Assert.True(saved.Overrides["auto"].Favorite);
        Assert.Equal("keep me", saved.Overrides["auto"].Notes);
        Assert.True(saved.Overrides["mine"].Artwork!.IsUserSelected);  // picked by the user: untouched
        Assert.Equal("abc", saved.Overrides["mine"].Artwork!.AssetId);
    }

    [Fact]
    public void ClearingTheCache_WithNothingCached_IsFine()
    {
        var vm = Library();

        Assert.Equal(0, vm.ClearCacheCore());
    }

    [Fact]
    public void ResettingSettings_RestoresEveryDefault_AndKeepsTheLibraryData()
    {
        var settings = new AppSettings
        {
            VibrantBackground = false, MinimizeToTrayWhileGaming = false, OptimizeBeforeLaunch = true, GlobalHotkeyEnabled = false,
            TrackExternalGames = false, CheckForUpdates = false, StartMaximized = false, DetectSteam = false, DetectEa = false,
            DetectManual = false, SidebarExpanded = false, SidebarToolsExpanded = false, SidebarLaunchersExpanded = false,
        };
        settings.WatchedFolders.Add(new WatchedFolder { Path = @"D:\Games" });
        var vm = Library(settings);

        vm.ResetSettingsCore();

        var saved = new SettingsService(_directory).Load();
        Assert.True(saved.VibrantBackground);
        Assert.True(saved.MinimizeToTrayWhileGaming);
        Assert.False(saved.OptimizeBeforeLaunch);
        Assert.True(saved.GlobalHotkeyEnabled);
        Assert.True(saved.TrackExternalGames);
        Assert.True(saved.CheckForUpdates);
        Assert.True(saved.StartMaximized);
        Assert.True(saved.DetectSteam && saved.DetectEa && saved.DetectManual);
        Assert.True(saved.SidebarExpanded && saved.SidebarToolsExpanded && saved.SidebarLaunchersExpanded && saved.SidebarDrivesExpanded);

        // ...and the person's own data is exactly as it was.
        Assert.True(saved.Overrides["auto"].Favorite);
        Assert.Equal("keep me", saved.Overrides["auto"].Notes);
        Assert.True(saved.Overrides["mine"].Artwork!.IsUserSelected);
        Assert.Equal(@"D:\Games", Assert.Single(saved.WatchedFolders).Path);
    }

    [Fact]
    public void TheCommands_AskFirst_AndDoNothingOnNo() => RunAsync(async () =>
    {
        var covers = Folder("CoverArtCache");
        var settings = new AppSettings { StartMaximized = false };
        var vm = Library(settings);
        var asked = new List<string>();
        var rescans = 0;
        vm.ConfirmResetForTest = question => { asked.Add(question); return false; };
        vm.RescanAfterChange = () => { rescans++; return Task.CompletedTask; };

        await vm.ClearCacheCommand.ExecuteAsync(null);
        await vm.ResetEverythingCommand.ExecuteAsync(null);

        Assert.Equal(2, asked.Count);
        Assert.Contains("favorites", asked[0]);
        Assert.Contains("kept", asked[1]);
        Assert.True(Directory.Exists(covers));
        Assert.False(vm.StartMaximized);
        Assert.Equal(0, rescans);
    });

    [Fact]
    public void OnYes_ClearCacheClearsAndRescans_AndResetAlsoRestoresTheSettings() => RunAsync(async () =>
    {
        var covers = Folder("CoverArtCache");
        var vm = Library(new AppSettings { StartMaximized = false });
        var rescans = 0;
        vm.ConfirmResetForTest = _ => true;
        vm.RescanAfterChange = () => { rescans++; return Task.CompletedTask; };

        await vm.ClearCacheCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(covers));
        Assert.False(vm.StartMaximized); // clearing the cache alone leaves the settings
        Assert.Equal(1, rescans);
        Assert.Contains("looking the covers up again", vm.StatusText);

        await vm.ResetEverythingCommand.ExecuteAsync(null);
        Assert.True(vm.StartMaximized);
        Assert.Equal(2, rescans);
        Assert.Contains("Settings reset", vm.StatusText);
    });

    private static void RunAsync(Func<Task> body) => body().GetAwaiter().GetResult();
}

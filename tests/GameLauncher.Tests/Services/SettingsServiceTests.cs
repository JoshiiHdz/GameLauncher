using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>Uses a fresh temp directory per test (via the SettingsService(string dataDir) constructor
/// added for testing) rather than the real %AppData%\GameLauncher, so these never touch a developer's
/// or CI runner's actual settings file.</summary>
public class SettingsServiceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly SettingsService _sut;

    public SettingsServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _sut = new SettingsService(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    [Fact]
    public void Load_NoFileYet_ReturnsDefaults()
    {
        var settings = _sut.Load();

        Assert.Empty(settings.WatchedFolders);
        Assert.Empty(settings.Overrides);
        Assert.True(settings.DetectSteam);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var settings = new AppSettings { SteamGridDbApiKey = "test-key" };
        settings.WatchedFolders.Add(new WatchedFolder { Path = "C:\\Games" });
        settings.Overrides["steam-100"] = new GameOverride { Favorite = true, Hidden = false };

        _sut.Save(settings);
        var loaded = _sut.Load();

        Assert.Equal("test-key", loaded.SteamGridDbApiKey);
        Assert.Equal("C:\\Games", Assert.Single(loaded.WatchedFolders).Path);
        Assert.True(loaded.Overrides["steam-100"].Favorite);
    }

    [Fact]
    public void Save_WritesAtomically_NoLeftoverTempFile()
    {
        _sut.Save(new AppSettings());

        var settingsPath = Path.Combine(_dataDir, "settings.json");
        var tempPath = settingsPath + ".tmp";

        Assert.True(File.Exists(settingsPath));
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public void Save_Twice_WritesBackupOfPreviousVersion()
    {
        _sut.Save(new AppSettings { SteamGridDbApiKey = "first" });
        _sut.Save(new AppSettings { SteamGridDbApiKey = "second" });

        var backupPath = Path.Combine(_dataDir, "settings.json.bak");
        Assert.True(File.Exists(backupPath));

        // The backup should hold the *previous* save, not the latest one.
        var backupJson = File.ReadAllText(backupPath);
        Assert.Contains("first", backupJson);
    }

    [Fact]
    public void Load_CorruptLiveFile_FallsBackToBackup()
    {
        _sut.Save(new AppSettings { SteamGridDbApiKey = "good" });
        _sut.Save(new AppSettings { SteamGridDbApiKey = "good-2" }); // "good" is now in the backup

        var settingsPath = Path.Combine(_dataDir, "settings.json");
        File.WriteAllText(settingsPath, "{ not valid json");

        var loaded = _sut.Load();

        Assert.Equal("good", loaded.SteamGridDbApiKey);
    }

    [Fact]
    public void Load_BothLiveAndBackupCorrupt_FallsBackToDefaults()
    {
        var settingsPath = Path.Combine(_dataDir, "settings.json");
        var backupPath = Path.Combine(_dataDir, "settings.json.bak");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(settingsPath, "{ not valid json");
        File.WriteAllText(backupPath, "{ also not valid json");

        var loaded = _sut.Load();

        Assert.Null(loaded.SteamGridDbApiKey);
        Assert.Empty(loaded.WatchedFolders);
    }

    [Fact]
    public void Load_ExplicitNullCollections_NormalizedToEmpty()
    {
        // "WatchedFolders": null deserializes successfully (System.Text.Json accepts null for a
        // reference-typed property) - callers used to enumerate it with no null-check and crash.
        var settingsPath = Path.Combine(_dataDir, "settings.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(settingsPath, """{"WatchedFolders": null, "Overrides": null}""");

        var loaded = _sut.Load();

        Assert.NotNull(loaded.WatchedFolders);
        Assert.Empty(loaded.WatchedFolders);
        Assert.NotNull(loaded.Overrides);
        Assert.Empty(loaded.Overrides);
    }

    [Fact]
    public void Load_NullEntryInWatchedFoldersArray_IsRemoved()
    {
        // System.Text.Json intercepts a JSON null array element for a reference-typed element type
        // itself - WatchedFolderJsonConverter.Read is never even called for it, so this has to be
        // cleaned up by the caller instead of the converter.
        var settingsPath = Path.Combine(_dataDir, "settings.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(settingsPath, """{"WatchedFolders": [null, {"Path": "C:\\Games"}]}""");

        var loaded = _sut.Load();

        Assert.Equal("C:\\Games", Assert.Single(loaded.WatchedFolders).Path);
    }

    [Fact]
    public void Load_NullValueInOverridesDictionary_IsRemoved()
    {
        // Same idea as the null WatchedFolders entry above, but for a dictionary value: downstream
        // code (GameScannerService's per-game enrichment) does settings.Overrides.TryGetValue(id, out
        // var over) and dereferences over.* directly - a null value here would previously reach that
        // as a NullReferenceException instead of just being treated as "no override for this game".
        var settingsPath = Path.Combine(_dataDir, "settings.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(settingsPath,
            """{"Overrides": {"steam-100": null, "steam-200": {"Favorite": true}}}""");

        var loaded = _sut.Load();

        var id = Assert.Single(loaded.Overrides.Keys);
        Assert.Equal("steam-200", id);
        Assert.True(loaded.Overrides["steam-200"].Favorite);
    }
}

using System.IO;
using System.Linq;
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

    // Save() catches IOException/UnauthorizedAccessException and only ever hands them to the shared
    // Logger, which itself silently swallows the exact same exception types while writing the log file -
    // so on an environment where Save() is genuinely failing for one of those reasons, the log can come
    // up completely empty even though a real exception was thrown and caught. Captured here via the
    // internal onSaveError seam instead, so a failing assertion below can report the real exception
    // (type/message/HResult/stack trace) instead of just "the backup file doesn't exist".
    private readonly List<Exception> _saveExceptions = new();

    public SettingsServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _sut = new SettingsService(_dataDir, _saveExceptions.Add);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    /// <summary>Call after any Save() whose effect the test is about to check, so a swallowed
    /// IOException/UnauthorizedAccessException fails loudly with full diagnostic detail instead of
    /// surfacing only as a confusing downstream assertion failure.</summary>
    private void FailIfSaveThrew()
    {
        if (_saveExceptions.Count == 0)
            return;

        var details = string.Join("\n---\n", _saveExceptions.Select(ex =>
            $"{ex.GetType().FullName}: {ex.Message}\nHResult: 0x{ex.HResult:X8}\n{ex.StackTrace}"));
        Assert.Fail($"SettingsService.Save() threw {_saveExceptions.Count} exception(s) it caught and swallowed:\n{details}");
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
        FailIfSaveThrew();

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
        FailIfSaveThrew();

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

    [Fact]
    public void Save_Succeeds_ReturnsTrue()
    {
        Assert.True(_sut.Save(new AppSettings()));
    }

    [Fact]
    public void Load_NullArtworkConflictsList_NormalizedToEmpty()
    {
        var settingsPath = Path.Combine(_dataDir, "settings.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(settingsPath, """{"ArtworkConflicts": null}""");

        var loaded = _sut.Load();

        Assert.NotNull(loaded.ArtworkConflicts);
        Assert.Empty(loaded.ArtworkConflicts);
    }

    [Fact]
    public void Load_NullEntryInArtworkConflictsArray_IsRemoved()
    {
        var settingsPath = Path.Combine(_dataDir, "settings.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(settingsPath, """{"ArtworkConflicts": [null]}""");

        var loaded = _sut.Load();

        Assert.Empty(loaded.ArtworkConflicts);
    }

    [Fact]
    public void Load_ArtworkConflictWithNullLoserSelection_IsRemoved()
    {
        // An ArtworkConflict without its LoserSelection carries no usable data at all - downstream code
        // (a future GC reference-discovery pass, a conflict-resolution UI) can only crash on it.
        var settingsPath = Path.Combine(_dataDir, "settings.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(settingsPath,
            """{"ArtworkConflicts": [{"WinnerGameId": "a", "LoserGameId": "b", "LoserSelection": null}]}""");

        var loaded = _sut.Load();

        Assert.Empty(loaded.ArtworkConflicts);
    }

    // ---- Load: ArtworkRevision validation ---------------------------------------------------------

    [Fact]
    public void Load_NegativeArtworkRevision_NormalizedToZero()
    {
        // -1 specifically collides with LibraryViewModel.ReconcileArtwork's own internal representation
        // of "no scan result at all" if it were ever allowed to leak in as a real persisted value -
        // that representation is a null (see ArtworkApplyResult?), never a numeric sentinel, precisely
        // so a persisted revision can't collide with it; this defends against the persisted side too.
        var settingsPath = Path.Combine(_dataDir, "settings.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(settingsPath,
            """{"Overrides": {"steam-100": {"ArtworkRevision": -1}}}""");

        var loaded = _sut.Load();

        Assert.Equal(0, loaded.Overrides["steam-100"].ArtworkRevision);
    }

    [Fact]
    public void Load_ArtworkRevisionNearLongMaxValue_NormalizedToZero()
    {
        // Close enough to long.MaxValue that the very next legitimate bump (Change Cover, Reset, or a
        // dedup merge) would silently wrap to a large negative number under unchecked arithmetic.
        var settingsPath = Path.Combine(_dataDir, "settings.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(settingsPath,
            """{"Overrides": {"steam-100": {"ArtworkRevision": """ + long.MaxValue + """ }}}""");

        var loaded = _sut.Load();

        Assert.Equal(0, loaded.Overrides["steam-100"].ArtworkRevision);
    }

    [Fact]
    public void Load_OrdinaryArtworkRevision_LeftUnchanged()
    {
        var settingsPath = Path.Combine(_dataDir, "settings.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(settingsPath,
            """{"Overrides": {"steam-100": {"ArtworkRevision": 42}}}""");

        var loaded = _sut.Load();

        Assert.Equal(42, loaded.Overrides["steam-100"].ArtworkRevision);
    }
}

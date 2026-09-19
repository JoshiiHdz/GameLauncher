using System.IO;
using System.Text;
using System.Text.Json;
using GameLauncher.Models;

namespace GameLauncher.Services;

public sealed class SettingsService
{
    private readonly string _dataDir;
    private readonly string _settingsPath;

    // Written atomically alongside every successful save (see Save) so a live file left corrupt by a
    // crash/power-loss mid-write, or by hand-editing, still has a known-good fallback instead of Load
    // silently dropping straight to blank defaults - which would read as "lost my favorites/hidden
    // games/watched folders" for no reason discoverable from the UI.
    private readonly string _backupPath;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Test-only diagnostic seam: Save()'s catch clause hands the caught exception here in addition to
    // Logger.Warn. Needed because Logger.Write itself silently swallows IOException/UnauthorizedAccessException
    // while writing the log file - on an environment where the *save* is failing for one of those same
    // reasons, the log write can fail right alongside it, leaving no record of either. A test that only
    // has the shared AppData logger to go on can come up completely empty even when Save did fail.
    private readonly Action<Exception>? _onSaveError;

    public SettingsService() : this(AppPaths.DataDir)
    {
    }

    /// <summary>Lets tests point Load/Save at an isolated temp directory instead of the real
    /// %AppData%\GameLauncher - production code always uses the parameterless constructor above.</summary>
    public SettingsService(string dataDir) : this(dataDir, onSaveError: null)
    {
    }

    /// <summary>Test-only: also lets a test capture the exact exception Save() catches (type, message,
    /// HResult, stack trace) without depending on the shared Logger - see _onSaveError above.</summary>
    internal SettingsService(string dataDir, Action<Exception>? onSaveError)
    {
        _dataDir = dataDir;
        _settingsPath = Path.Combine(dataDir, "settings.json");
        _backupPath = _settingsPath + ".bak";
        _onSaveError = onSaveError;
    }

    public AppSettings Load()
    {
        var settings = TryLoad(_settingsPath) ?? TryLoad(_backupPath);
        if (settings is null)
            return new AppSettings();

        // A hand-edited or partially-corrupted file can deserialize successfully while still setting
        // a collection to JSON null (e.g. "WatchedFolders": null) - System.Text.Json happily accepts
        // that for a reference-typed property. Callers (LibraryViewModel's constructor, GameScannerService)
        // enumerate these directly with no null-check of their own, so a null here crashes the app at
        // startup instead of just treating the entry as "none".
        settings.WatchedFolders ??= new();
        settings.Overrides ??= new();

        // "WatchedFolders": [null] deserializes to a list containing one null entry without ever
        // calling WatchedFolderJsonConverter.Read (System.Text.Json intercepts JSON null for a
        // reference-typed element itself) - so no amount of hardening in that converter catches it.
        // Same idea for a null value under "Overrides". Downstream code (ManualFolderScanner,
        // GameScannerService's per-game enrichment) dereferences these without a null-check.
        settings.WatchedFolders.RemoveAll(f => f is null);
        foreach (var key in settings.Overrides.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            settings.Overrides.Remove(key);

        // Same defensive normalization for the new artwork-conflict list: a null list, a null entry
        // within it, or an entry missing the LoserSelection it exists to hold is treated as "not really
        // a recorded conflict" rather than left for downstream code (GC reference-discovery, a future
        // conflict-resolution UI) to crash on.
        settings.ArtworkConflicts ??= new();
        settings.ArtworkConflicts.RemoveAll(c => c is null || c.LoserSelection is null);

        // GameOverride.ArtworkRevision is a plain, unchecked long - a hand-edited or otherwise corrupted
        // settings.json can persist a negative value (colliding with reconciliation's "no scan result at
        // all" case, which is represented as the absence of a value, never a numeric sentinel - see
        // LibraryViewModel.ReconcileArtwork) or a value near long.MaxValue (which the very next +1
        // bump - Change Cover, Reset, or a dedup merge - would silently wrap to a huge negative number).
        // Neither can arise from this app's own normal operation, so resetting to 0 on load is always
        // safe: it only ever discards a value that was already meaningless.
        foreach (var over in settings.Overrides.Values)
        {
            if (over.ArtworkRevision < 0 || over.ArtworkRevision > long.MaxValue - 1_000_000)
                over.ArtworkRevision = 0;
        }

        return settings;
    }

    private static AppSettings? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't read '{path}'.", ex);
            return null;
        }
    }

    /// <summary>Returns whether the write actually succeeded - callers that only ever change simple
    /// fields (Favorite/Hidden/...) can keep ignoring this exactly as before, but Change Cover/Reset
    /// need to know for certain before treating a new selection (or a cleared one) as durably committed;
    /// see LibraryViewModel's artwork commit path.</summary>
    public bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);

            // Write-then-replace rather than a direct File.WriteAllText(_settingsPath, ...): writing in
            // place means a crash, power loss, or antivirus lock mid-write leaves settings.json
            // truncated - the only copy - and the very next Load() falls back to blank defaults.
            // File.Replace swaps the temp file in as a single atomic filesystem operation and, in the
            // same operation, saves whatever was previously at _settingsPath to _backupPath - so Load()
            // always has a last-known-good file to fall back to even if this process dies mid-save.
            var tempPath = _settingsPath + ".tmp";
            var bytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                // Flush(true) pushes through the OS write cache to stable storage, not just the app's
                // own buffer - File.WriteAllText only guarantees the latter, so a power loss between
                // that write and the OS's own lazy flush could still leave the temp file (and thus the
                // eventual replace) corrupt, defeating the point of writing to a temp file at all.
                stream.Flush(true);
            }

            if (File.Exists(_settingsPath))
                File.Replace(tempPath, _settingsPath, _backupPath);
            else
                File.Move(tempPath, _settingsPath);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("Couldn't save settings.json - changes may be lost on restart.", ex);
            _onSaveError?.Invoke(ex);
            return false;
        }
    }
}

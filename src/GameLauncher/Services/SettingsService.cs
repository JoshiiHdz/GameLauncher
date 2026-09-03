using System.IO;
using System.Text;
using System.Text.Json;
using GameLauncher.Models;

namespace GameLauncher.Services;

public sealed class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(AppPaths.DataDir, "settings.json");

    // Written atomically alongside every successful save (see Save) so a live file left corrupt by a
    // crash/power-loss mid-write, or by hand-editing, still has a known-good fallback instead of Load
    // silently dropping straight to blank defaults - which would read as "lost my favorites/hidden
    // games/watched folders" for no reason discoverable from the UI.
    private static readonly string BackupPath = SettingsPath + ".bak";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Load()
    {
        var settings = TryLoad(SettingsPath) ?? TryLoad(BackupPath);
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

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);

            // Write-then-replace rather than a direct File.WriteAllText(SettingsPath, ...): writing in
            // place means a crash, power loss, or antivirus lock mid-write leaves settings.json
            // truncated - the only copy - and the very next Load() falls back to blank defaults.
            // File.Replace swaps the temp file in as a single atomic filesystem operation and, in the
            // same operation, saves whatever was previously at SettingsPath to BackupPath - so Load()
            // always has a last-known-good file to fall back to even if this process dies mid-save.
            var tempPath = SettingsPath + ".tmp";
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

            if (File.Exists(SettingsPath))
                File.Replace(tempPath, SettingsPath, BackupPath);
            else
                File.Move(tempPath, SettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("Couldn't save settings.json - changes may be lost on restart.", ex);
        }
    }
}

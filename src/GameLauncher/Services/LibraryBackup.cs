using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Models;
using GameLauncher.ViewModels;

namespace GameLauncher.Services;

/// <summary>What a backup file holds for one game: only the user's own choices and tracked play time.</summary>
public sealed class BackupGame
{
    public string? CustomName { get; set; }
    public bool Hidden { get; set; }
    public bool Favorite { get; set; }
    public List<string> Collections { get; set; } = new();
    public string? Notes { get; set; }
    public DateTime? DateAdded { get; set; }
    public long TotalPlaySeconds { get; set; }
    public DateTime? LastPlayedUtc { get; set; }

    internal bool HasAnything => !string.IsNullOrWhiteSpace(CustomName) || Hidden || Favorite || Collections.Count > 0
        || !string.IsNullOrWhiteSpace(Notes) || TotalPlaySeconds > 0 || LastPlayedUtc is not null;
}

/// <summary>The few plain preferences a backup carries. Nothing PC-specific (watched folders, start-with-Windows) and no keys or secrets.</summary>
public sealed class BackupSettings
{
    public bool DetectSteam { get; set; } = true;
    public bool DetectEpic { get; set; } = true;
    public bool DetectGog { get; set; } = true;
    public bool DetectXbox { get; set; } = true;
    public bool DetectEa { get; set; } = true;
    public bool DetectUbisoft { get; set; } = true;
    public bool DetectBattleNet { get; set; } = true;
    public bool DetectRockstar { get; set; } = true;
    public bool DetectAmazonGames { get; set; } = true;
    public bool DetectManual { get; set; } = true;
    public bool SidebarExpanded { get; set; } = true;
    public bool VibrantBackground { get; set; } = true;
    public bool MinimizeToTrayWhileGaming { get; set; } = true;
    public bool TrackExternalGames { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public bool OptimizeBeforeLaunch { get; set; }
}

public sealed class BackupFile
{
    public const string Marker = "Axis Game Launcher backup";
    public const int CurrentVersion = 1;

    public string Kind { get; set; } = Marker;
    public int Version { get; set; } = CurrentVersion;
    public string AppVersion { get; set; } = "";
    public DateTime ExportedUtc { get; set; }
    public Dictionary<string, BackupGame> Games { get; set; } = new();
    public BackupSettings Settings { get; set; } = new();
}

public sealed record ImportSummary(int GamesInFile, int GamesInThisLibrary, int GamesAddedAsNew, int GamesUpdated)
{
    public string Describe() =>
        $"Imported data for {GamesInFile:N0} {(GamesInFile == 1 ? "game" : "games")} ({GamesInThisLibrary:N0} in your library right now; the rest will apply if they are installed later) and your settings.";
}

/// <summary>Export and import of the user's own library data: favorites, hidden, collections, notes, custom names, tracked play time and a
/// few preferences. Deliberately not in it: cover art and identity decisions (they point at files and lookups on one PC), anything
/// secret, and anything tied to this PC's folders. Import only ever MERGES - nothing in the current library is removed - and importing
/// the same file twice changes nothing the second time.</summary>
public static class LibraryBackup
{
    internal const long MaxFileBytes = 20L * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static BackupFile Build(AppSettings settings, string appVersion, DateTime nowUtc)
    {
        var file = new BackupFile
        {
            AppVersion = appVersion,
            ExportedUtc = nowUtc,
            Settings = new BackupSettings
            {
                DetectSteam = settings.DetectSteam, DetectEpic = settings.DetectEpic, DetectGog = settings.DetectGog,
                DetectXbox = settings.DetectXbox, DetectEa = settings.DetectEa, DetectUbisoft = settings.DetectUbisoft,
                DetectBattleNet = settings.DetectBattleNet, DetectRockstar = settings.DetectRockstar,
                DetectAmazonGames = settings.DetectAmazonGames, DetectManual = settings.DetectManual, SidebarExpanded = settings.SidebarExpanded,
                VibrantBackground = settings.VibrantBackground, MinimizeToTrayWhileGaming = settings.MinimizeToTrayWhileGaming,
                TrackExternalGames = settings.TrackExternalGames, CheckForUpdates = settings.CheckForUpdates,
                OptimizeBeforeLaunch = settings.OptimizeBeforeLaunch,
            },
        };

        foreach (var (id, over) in settings.Overrides)
        {
            var game = new BackupGame
            {
                CustomName = over.CustomName, Hidden = over.Hidden, Favorite = over.Favorite, Collections = [.. over.Collections ?? []],
                Notes = over.Notes, DateAdded = over.DateAdded, TotalPlaySeconds = over.TotalPlaySeconds, LastPlayedUtc = over.LastPlayedUtc,
            };

            if (game.HasAnything)
                file.Games[id] = game;
        }

        return file;
    }

    public static string ToJson(BackupFile file) => JsonSerializer.Serialize(file, Options);

    /// <summary>Reads a backup, or says why it can't: not our file, a newer format than this version understands, or damaged.</summary>
    public static (BackupFile? File, string? Error) Parse(string json)
    {
        BackupFile? file;
        try
        {
            // The marker is checked on the raw text: a missing one must read as "not ours", not as the class's default value.
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != BackupFile.Marker)
            {
                return (null, "That file isn't an Axis Game Launcher backup.");
            }

            file = document.RootElement.Deserialize<BackupFile>(Options);
        }
        catch (JsonException)
        {
            return (null, "That file isn't a valid backup - it may be damaged or not a backup at all.");
        }

        if (file is null)
            return (null, "That file isn't an Axis Game Launcher backup.");

        if (file.Version > BackupFile.CurrentVersion)
            return (null, $"That backup was made by a newer version of Axis Game Launcher (format {file.Version}). Update the app and try again.");

        file.Games ??= new();
        file.Settings ??= new();
        return (file, null);
    }

    /// <summary>Merges the backup's games into <paramref name="settings"/>. Every rule keeps the result stable under repeats:
    /// favorites and collections are unions, hidden stays hidden once either side hid it, notes are joined only when they differ, a
    /// custom name or date is only filled in where there is none (dates keep the earlier), and play time takes the larger figure
    /// rather than adding the two - so a second import adds nothing.</summary>
    public static ImportSummary Merge(AppSettings settings, BackupFile file, IReadOnlySet<string> idsInLibrary)
    {
        int added = 0, updated = 0;
        foreach (var (id, incoming) in file.Games)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var existed = settings.Overrides.TryGetValue(id, out var over);
            if (!existed)
            {
                over = new GameOverride();
                settings.Overrides[id] = over;
            }

            var before = (over!.Favorite, over.Hidden, over.CustomName, over.Notes, over.TotalPlaySeconds, over.DateAdded, over.LastPlayedUtc,
                Collections: string.Join('\u0001', over.Collections));

            over.Favorite |= incoming.Favorite;
            over.Hidden |= incoming.Hidden;
            if (string.IsNullOrWhiteSpace(over.CustomName) && !string.IsNullOrWhiteSpace(incoming.CustomName))
                over.CustomName = incoming.CustomName.Trim();
            over.Collections = LibraryViewModel.MergeCollectionNames(over.Collections, incoming.Collections);
            over.Notes = Cap(LibraryViewModel.MergeNotes(over.Notes, incoming.Notes));
            over.TotalPlaySeconds = Math.Max(over.TotalPlaySeconds, Math.Max(0, incoming.TotalPlaySeconds));
            if (incoming.DateAdded is { } date && (over.DateAdded is not { } mine || date < mine))
                over.DateAdded = date;
            if (incoming.LastPlayedUtc is { } last && (over.LastPlayedUtc is not { } had || last > had))
                over.LastPlayedUtc = last;

            var after = (over.Favorite, over.Hidden, over.CustomName, over.Notes, over.TotalPlaySeconds, over.DateAdded, over.LastPlayedUtc,
                Collections: string.Join('\u0001', over.Collections));
            if (!existed)
                added++;
            else if (before != after)
                updated++;
        }

        return new ImportSummary(file.Games.Count, file.Games.Keys.Count(idsInLibrary.Contains), added, updated);
    }

    private static string? Cap(string? notes) =>
        notes is { Length: > GameDetailsViewModel.MaxNotesLength } ? notes[..GameDetailsViewModel.MaxNotesLength] : notes;
}

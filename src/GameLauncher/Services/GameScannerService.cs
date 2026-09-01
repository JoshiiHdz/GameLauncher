using System.IO;
using GameLauncher.Models;

namespace GameLauncher.Services;

public sealed class GameScannerService
{
    public Task<List<GameEntry>> ScanAllAsync(AppSettings settings)
    {
        return Task.Run(() =>
        {
            Logger.Info("Scan started.");
            var results = new List<GameEntry>();

            if (settings.DetectSteam)
                results.AddRange(SafeScan("Steam", SteamScanner.Scan));

            if (settings.DetectEpic)
                results.AddRange(SafeScan("Epic", EpicScanner.Scan));

            if (settings.DetectGog)
                results.AddRange(SafeScan("GOG", GogScanner.Scan));

            results.AddRange(SafeScan("Manual folders", () => ManualFolderScanner.Scan(settings.WatchedFolders)));

            // De-duplicate by install dir (a manually-watched folder may overlap a launcher's library,
            // e.g. someone points a watched folder at their Steam "common" dir). Prefer the launcher-detected
            // entry over the generic manual one since it carries the correct launch URI/exe.
            var deduped = results
                .OrderBy(g => g.Source == GameSource.Manual ? 1 : 0)
                .GroupBy(g => g.InstallDir, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (results.Count != deduped.Count)
                Logger.Info($"De-duplicated {results.Count - deduped.Count} overlapping entr(y/ies).");

            foreach (var game in deduped)
            {
                if (!settings.Overrides.TryGetValue(game.ExecutablePath, out var over))
                {
                    over = new GameOverride { DateAdded = DateTime.UtcNow };
                    settings.Overrides[game.ExecutablePath] = over;
                }
                over.DateAdded ??= DateTime.UtcNow;

                if (!string.IsNullOrWhiteSpace(over.CustomName))
                    game.Name = over.CustomName;
                game.Hidden = over.Hidden;
                game.Favorite = over.Favorite;
                game.DateAdded = over.DateAdded.Value;

                game.Icon = CoverArtService.GetCoverArt(game, settings);
            }

            Logger.Info($"Scan finished: {deduped.Count} game(s) total.");

            // Final ordering doesn't matter here - LibraryViewModel re-sorts per the user's chosen SortOption.
            return deduped;
        });
    }

    // Isolates one source's failure (e.g. a Steam/manual library on a now-unplugged external drive)
    // so it can't wipe out games already found from every other source.
    private static List<GameEntry> SafeScan(string sourceName, Func<List<GameEntry>> scan)
    {
        try
        {
            var found = scan();
            Logger.Info($"{sourceName}: found {found.Count} game(s).");
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"{sourceName}: scan failed, skipping this source for now.", ex);
            return [];
        }
    }
}

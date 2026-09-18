using System.IO;

namespace GameLauncher.Services;

/// <summary>
/// Picks the real game executable out of an install folder when a launcher's manifest only gives us
/// the folder, not the exe itself - the largest non-installer/non-tool exe within a shallow walk.
/// Shared by every scanner that needs this (Xbox, EA, Ubisoft, and the publisher-filtered launchers),
/// so the "which .exe is actually the game" heuristic only has to be tuned in one place.
/// </summary>
public static class GameExeFinder
{
    // Generic installer/tool/crash-reporter noise that is never the real game and never something
    // worth tracking as a running process either - safe to exclude both when picking which exe to
    // launch (below) and when GameSessionWatcher decides which exe names are worth polling for.
    // Deliberately NOT here: "trial"/"anticheat". Both can be a legitimate part of a game's actual
    // running process tree (EAAntiCheat.GameServiceLauncher is the confirmed real handoff target for
    // EA SPORTS FC 26's anti-cheat init), so excluding them everywhere would make the session watcher
    // blind to a handoff it needs to see - they're excluded only from the exe-picking pool below,
    // where the concern is different ("don't launch the trial build," not "don't ever watch for it").
    internal static readonly string[] NoiseExcludePatterns =
        { "unins", "redist", "crash", "touchup", "activation", "vcredist", "directx", "dxsetup", "setup",
          "dotnetfx", "installer", "prereq", "cleanup" };

    // "trial"/"anticheat" found from a real log: EA SPORTS FC 26 launched "FC26_Trial.exe" instead
    // of the real "FC26.exe" sitting right next to it, because the trial build happened to be the
    // largest unexcluded exe in the folder - a paying owner should never get routed into trial mode.
    private static readonly string[] DefaultExcludePatterns =
        NoiseExcludePatterns.Concat(new[] { "trial", "anticheat" }).ToArray();

    // EA's "Friend's Pass" system bundles a second, free-trial-for-a-friend build right next to the
    // real, owned game's exe - confirmed via A Way Out: AWayOut_friend.exe sits alongside AWayOut.exe,
    // and without excluding it, an owner whose friend-pass build happens to be the larger file would
    // get launched into the free trial client instead of their own game. Deliberately NOT in
    // DefaultExcludePatterns above: a bare "friend" substring match would also reject a legitimate
    // game's own exe that merely contains that word anywhere in its name (FriendlyGame.exe,
    // MyFriendsList.exe, ...) across EVERY scanner that uses this class, not just EA's. "_friend" (with
    // the underscore) is the actual, confirmed naming convention and is passed only from EaScanner's own
    // extraExcludePatterns - the same narrowly-scoped mechanism "showcase" already uses there - so this
    // never applies to Xbox/Ubisoft/Manual/PublisherUninstallScanner at all.
    internal const string EaFriendsPassExcludePattern = "_friend";

    // A Way Out's real executable sits at <root>\Haze1\Binaries\Win64\AWayOut.exe - three subfolder
    // levels below the install root passed in here, one deeper than the old default of 2 ever reached.
    // 5 matches ManualFolderScanner's own MaxWalkDepth precedent (a scan scoped to ONE game's already-
    // known install folder, not a whole drive, so the extra headroom over the previous default costs
    // comparatively little) rather than picking a number that happens to fit only this one confirmed
    // case.
    public static string? FindLargestExe(string installDir, int maxDepth = 5, IEnumerable<string>? extraExcludePatterns = null)
    {
        var exclude = extraExcludePatterns is null
            ? DefaultExcludePatterns
            : DefaultExcludePatterns.Concat(extraExcludePatterns).ToArray();

        FileInfo? best = null;
        Collect(installDir, 0);
        return best?.FullName;

        void Collect(string dir, int depth)
        {
            if (depth > maxDepth)
                return;

            try
            {
                foreach (var exe in Directory.EnumerateFiles(dir, "*.exe"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                    if (exclude.Any(fileName.Contains))
                        continue;

                    var info = new FileInfo(exe);
                    if (best is null || info.Length > best.Length)
                        best = info;
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                    Collect(sub, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

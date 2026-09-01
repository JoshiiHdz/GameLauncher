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
    private static readonly string[] DefaultExcludePatterns =
        { "unins", "redist", "crash", "touchup", "activation", "vcredist", "directx", "dxsetup", "setup" };

    public static string? FindLargestExe(string installDir, int maxDepth = 2, IEnumerable<string>? extraExcludePatterns = null)
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

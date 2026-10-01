using System.IO;

namespace GameLauncher.Services;

public enum XboxExeSearchOutcome
{
    /// <summary>The ENTIRE reachable tree was searched (within budget) and a plausible exe was found -
    /// a confident result: nothing larger/better was left unexamined.</summary>
    Found,

    /// <summary>The ENTIRE reachable tree was searched (within budget) and nothing plausible was there -
    /// a real, confident negative.</summary>
    NotFound,

    /// <summary>The directory/time budget ran out before the search could finish. NOT the same as
    /// NotFound: the game may well have an exe somewhere still unvisited - a caller must never treat this
    /// as proof the game is absent. ProvisionalExePath carries whatever was the largest candidate found
    /// BEFORE the budget ran out, if any - "found something, but couldn't confirm it's actually the best
    /// (or only) candidate in the whole tree" is a different, weaker fact than Found, and a caller that
    /// silently treated the two the same would hide that a better candidate might still be sitting in an
    /// unvisited directory.</summary>
    Incomplete,
}

public readonly record struct XboxExeSearchResult(XboxExeSearchOutcome Outcome, string? ExePath, string? ProvisionalExePath = null)
{
    public static XboxExeSearchResult Found(string path) => new(XboxExeSearchOutcome.Found, path);
    public static readonly XboxExeSearchResult NotFound = new(XboxExeSearchOutcome.NotFound, null);
    public static XboxExeSearchResult Incomplete(string? provisionalExePath) => new(XboxExeSearchOutcome.Incomplete, null, provisionalExePath);
}

/// <summary>
/// The fallback exe search XboxScanner uses only when a package's own metadata (MicrosoftGame.config/
/// AppxManifest.xml) named no usable executable - layout-independent (no maxDepth at all; the old
/// hardcoded "1 level, never deeper" was the exact bug that made detection depend on how a specific game
/// happened to be laid out) but still genuinely BOUNDED, so it can never turn into an unlimited
/// whole-drive walk:
///   - a directory-visit budget (deterministic and testable, unlike a wall-clock timeout)
///   - a wall-clock ceiling on top of that, for the real, non-pathological-but-still-huge case
///   - cancellable, checked every directory AND periodically while enumerating one - a single directory
///     with an enormous number of entries (a huge, flat asset folder) could otherwise run for a long time
///     between the per-directory checks alone
///   - one inaccessible directory is skipped, not fatal to the whole search
///   - reparse points (junctions/symlinks) are never entered - the classic source of an infinite loop,
///     and also a way the search could otherwise wander outside the install root it was scoped to
/// </summary>
public static class XboxExeSearch
{
    // How often (in enumerated entries) the loop re-checks cancellation/budget WHILE inside a single
    // directory's own file/subdirectory listing - not just once per directory. Small enough that a
    // directory holding tens of thousands of entries still responds promptly; large enough that the
    // check itself is not a meaningful cost against ordinary directories.
    private const int EnumerationCheckInterval = 256;

    /// <summary>Test seam: called once per directory, right before it's processed. Lets a test cancel a
    /// linked token at an exact, deterministic point in the walk instead of racing a wall-clock delay
    /// against however fast the walk happens to run on whatever machine executes it.</summary>
    internal static Action<string>? OnDirectoryVisitedForTest { get; set; }

    /// <summary>Test seam: called each time the periodic in-enumeration check fires (every
    /// EnumerationCheckInterval entries), before that check evaluates cancellation/budget. Lets a test
    /// prove cancellation is actually observed WHILE enumerating one large directory - not just once per
    /// directory - without needing thousands of files and a timing race to make that observable.</summary>
    internal static Action? OnEnumerationCheckForTest { get; set; }

    public static XboxExeSearchResult FindLargestExe(string root, CancellationToken ct,
        int maxDirectoriesToVisit = 20_000, TimeSpan? timeBudget = null)
    {
        var deadline = DateTime.UtcNow + (timeBudget ?? TimeSpan.FromSeconds(5));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        var normalizedRoot = NormalizeForVisited(root);
        if (normalizedRoot is null)
            return XboxExeSearchResult.NotFound; // root itself doesn't even resolve to a real path

        queue.Enqueue(root);
        visited.Add(normalizedRoot);

        FileInfo? best = null;
        var visitedCount = 0;
        var incomplete = false;

        bool BudgetExhausted() => visitedCount >= maxDirectoriesToVisit || DateTime.UtcNow >= deadline;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            if (BudgetExhausted())
            {
                incomplete = true;
                break;
            }

            var dir = queue.Dequeue();
            visitedCount++;
            OnDirectoryVisitedForTest?.Invoke(dir);

            try
            {
                var entriesSinceCheck = 0;
                foreach (var exe in Directory.EnumerateFiles(dir, "*.exe"))
                {
                    if (++entriesSinceCheck >= EnumerationCheckInterval)
                    {
                        entriesSinceCheck = 0;
                        OnEnumerationCheckForTest?.Invoke();
                        ct.ThrowIfCancellationRequested();
                        if (BudgetExhausted())
                        {
                            incomplete = true;
                            break;
                        }
                    }

                    if (GameExeFinder.IsExcludedExeName(exe))
                        continue;

                    var info = new FileInfo(exe);
                    if (best is null || info.Length > best.Length)
                        best = info;
                }

                if (incomplete)
                    break;

                entriesSinceCheck = 0;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (++entriesSinceCheck >= EnumerationCheckInterval)
                    {
                        entriesSinceCheck = 0;
                        OnEnumerationCheckForTest?.Invoke();
                        ct.ThrowIfCancellationRequested();
                        if (BudgetExhausted())
                        {
                            incomplete = true;
                            break;
                        }
                    }

                    FileAttributes attrs;
                    try
                    {
                        attrs = File.GetAttributes(sub);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue; // can't even stat it - treat the same as any other inaccessible entry
                    }

                    if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        continue; // never followed: the loop-prevention guarantee doesn't depend on the visited-set alone

                    var normalized = NormalizeForVisited(sub);
                    if (normalized is not null && visited.Add(normalized))
                        queue.Enqueue(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Xbox: couldn't read '{dir}' while searching for the game's executable - skipping it, not the whole search.", ex);
            }

            if (incomplete)
                break;
        }

        if (incomplete)
            return XboxExeSearchResult.Incomplete(best?.FullName);

        return best is not null ? XboxExeSearchResult.Found(best.FullName) : XboxExeSearchResult.NotFound;
    }

    private static string? NormalizeForVisited(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }
}

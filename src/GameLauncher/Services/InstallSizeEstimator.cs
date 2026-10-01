using System.IO;

namespace GameLauncher.Services;

public enum InstallSizeOutcome
{
    /// <summary>The ENTIRE reachable tree was walked (within budget) - Bytes is the real total.</summary>
    Complete,

    /// <summary>The directory/time budget ran out before the walk could finish. Bytes still carries
    /// whatever was summed before that happened - a real, honest lower bound, not a number to discard -
    /// since "roughly how much space" never promised exactness in the first place.</summary>
    Incomplete,
}

public readonly record struct InstallSizeResult(InstallSizeOutcome Outcome, long Bytes)
{
    public static InstallSizeResult Complete(long bytes) => new(InstallSizeOutcome.Complete, bytes);
    public static InstallSizeResult Incomplete(long bytesSoFar) => new(InstallSizeOutcome.Incomplete, bytesSoFar);
}

/// <summary>
/// Estimates a game's install folder size for the "≈ N GB" figure a card shows once its drive is
/// selected (see LibraryViewModel.EstimateSizesForSelectedDriveAsync). Deliberately an ESTIMATE, not a
/// figure meant to match Explorer's own Properties dialog exactly - cluster-size rounding and files
/// that change mid-walk mean two honest walks of the same folder can disagree slightly too.
///
/// Bounded the same way XboxExeSearch is bounded, for the same reason - see that type's own remarks
/// for the full reasoning this mirrors: a directory-visit budget (deterministic, testable) plus a
/// wall-clock ceiling on top, cancellable and checked periodically WHILE enumerating one huge
/// directory (not just once per directory), one inaccessible directory/file skipped rather than fatal
/// to the whole walk, and reparse points never entered (loop prevention, and staying inside the
/// install root it was scoped to).
/// </summary>
public static class InstallSizeEstimator
{
    private const int EnumerationCheckInterval = 256;

    /// <summary>Test seam: called once per directory, right before it's processed - lets a test cancel
    /// a linked token at an exact, deterministic point in the walk.</summary>
    internal static Action<string>? OnDirectoryVisitedForTest { get; set; }

    public static InstallSizeResult Estimate(string root, CancellationToken ct,
        int maxDirectoriesToVisit = 20_000, TimeSpan? timeBudget = null)
    {
        var deadline = DateTime.UtcNow + (timeBudget ?? TimeSpan.FromSeconds(5));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        var normalizedRoot = NormalizeForVisited(root);
        if (normalizedRoot is null)
            return InstallSizeResult.Complete(0); // root itself doesn't even resolve to a real path

        queue.Enqueue(root);
        visited.Add(normalizedRoot);

        var totalBytes = 0L;
        var visitedCount = 0;

        bool BudgetExhausted() => visitedCount >= maxDirectoriesToVisit || DateTime.UtcNow >= deadline;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            if (BudgetExhausted())
                return InstallSizeResult.Incomplete(totalBytes);

            var dir = queue.Dequeue();
            visitedCount++;
            OnDirectoryVisitedForTest?.Invoke(dir);

            try
            {
                var entriesSinceCheck = 0;
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (++entriesSinceCheck >= EnumerationCheckInterval)
                    {
                        entriesSinceCheck = 0;
                        ct.ThrowIfCancellationRequested();
                        if (BudgetExhausted())
                            return InstallSizeResult.Incomplete(totalBytes);
                    }

                    try
                    {
                        totalBytes += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // One unreadable file (deleted mid-walk, permissions) doesn't invalidate the estimate.
                    }
                }

                entriesSinceCheck = 0;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (++entriesSinceCheck >= EnumerationCheckInterval)
                    {
                        entriesSinceCheck = 0;
                        ct.ThrowIfCancellationRequested();
                        if (BudgetExhausted())
                            return InstallSizeResult.Incomplete(totalBytes);
                    }

                    FileAttributes attrs;
                    try
                    {
                        attrs = File.GetAttributes(sub);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    var normalized = NormalizeForVisited(sub);
                    if (normalized is not null && visited.Add(normalized))
                        queue.Enqueue(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Couldn't read '{dir}' while estimating install size - skipping it, not the whole estimate.", ex);
            }
        }

        return InstallSizeResult.Complete(totalBytes);
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

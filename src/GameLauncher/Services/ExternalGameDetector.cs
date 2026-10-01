using System.IO;
using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Services;

internal sealed record ExternalGameCandidate(string GameId, string ExecutablePath);

/// <summary>Passive detection only: never launches, waits on, or terminates a process. Requires an
/// exact, uniquely-owned executable path. Inaccessible images and URI-only installs are not guessed.</summary>
internal sealed class ExternalGameDetector(IProcessProvider processes)
{
    public ExternalGameDetector() : this(new SnapshotProcessProvider()) { }

    internal static string? NormalizePath(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    public IReadOnlyList<ExternalGameCandidate> Detect(IReadOnlyList<ExternalGameCandidate> games, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var byPath = games.Select(g => (Game: g, Path: NormalizePath(g.ExecutablePath)))
            .Where(p => p.Path is not null).GroupBy(p => p.Path!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(p => p.Game.GameId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Game, StringComparer.OrdinalIgnoreCase);
        if (byPath.Count == 0) return [];
        var names = byPath.Keys.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase);
        var found = processes.FindProcessesByName(names!);
        var matches = new Dictionary<string, ExternalGameCandidate>();
        try
        {
            foreach (var process in found)
            {
                ct.ThrowIfCancellationRequested();
                var path = NormalizePath(process.GetPath());
                if (path is not null && byPath.TryGetValue(path, out var game)) matches[game.GameId] = game;
            }
        }
        finally
        {
            foreach (var process in found) process.Dispose();
        }
        return matches.Values.ToArray();
    }
}

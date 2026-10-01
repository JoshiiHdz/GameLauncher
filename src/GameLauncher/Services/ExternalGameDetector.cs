using System.IO;
using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Services;

internal sealed record ExternalGameCandidate(string GameId, string ExecutablePath);

/// <summary>What a poll needs to know about the library, worked out once per library rather than once per poll: each
/// uniquely-owned, normalised executable path with its game, and the distinct process names to look for.</summary>
internal sealed class ExternalGameIndex
{
    internal Dictionary<string, ExternalGameCandidate> ByPath { get; }
    internal string[] Names { get; }

    internal ExternalGameIndex(Dictionary<string, ExternalGameCandidate> byPath)
    {
        ByPath = byPath;
        Names = byPath.Keys.Select(p => Path.GetFileNameWithoutExtension(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public int PathCount => ByPath.Count;
    public int NameCount => Names.Length;
}

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

    public ExternalGameIndex BuildIndex(IReadOnlyList<ExternalGameCandidate> games) => new(
        games.Select(g => (Game: g, Path: NormalizePath(g.ExecutablePath)))
            .Where(p => p.Path is not null).GroupBy(p => p.Path!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(p => p.Game.GameId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Game, StringComparer.OrdinalIgnoreCase));

    public IReadOnlyList<ExternalGameCandidate> Detect(IReadOnlyList<ExternalGameCandidate> games, CancellationToken ct) =>
        Detect(BuildIndex(games), ct);

    public IReadOnlyList<ExternalGameCandidate> Detect(ExternalGameIndex index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (index.PathCount == 0) return [];
        var found = processes.FindProcessesByName(index.Names);
        var matches = new Dictionary<string, ExternalGameCandidate>();
        try
        {
            foreach (var process in found)
            {
                ct.ThrowIfCancellationRequested();
                var path = NormalizePath(process.GetPath());
                if (path is not null && index.ByPath.TryGetValue(path, out var game)) matches[game.GameId] = game;
            }
        }
        finally
        {
            foreach (var process in found) process.Dispose();
        }
        return matches.Values.ToArray();
    }
}

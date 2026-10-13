using System.IO;
using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Services;

/// <summary>A library game as the external-session check sees it: its id, the exe it launches (empty for a game started through a launcher's address), and the folder
/// it is installed in (which is how a game with no exe of its own on record - Xbox, EA, anything started by address - is told apart from other programs).</summary>
internal sealed record ExternalGameCandidate(string GameId, string ExecutablePath, string? InstallDir = null);

/// <summary>A game's program found inside its install folder: the folder, and whether this is one of its main programs (its own exe, an Unreal "...-Shipping" exe, or the
/// only exe it has) - the ones a protected process may be recognised by name alone.</summary>
internal sealed record ExternalInstall(ExternalGameCandidate Game, string Dir, bool IsMainName);

/// <summary>What a poll needs to know about the library, worked out once per library rather than once per poll: each
/// uniquely-owned, normalised executable path with its game, the programs found in each install folder, and the distinct process names to look for.</summary>
internal sealed class ExternalGameIndex
{
    internal Dictionary<string, ExternalGameCandidate> ByPath { get; }

    /// <summary>Process name -> the games with a program of that name inside their install folder.</summary>
    internal Dictionary<string, List<ExternalInstall>> ByName { get; }

    internal string[] Names { get; }

    internal ExternalGameIndex(Dictionary<string, ExternalGameCandidate> byPath, Dictionary<string, List<ExternalInstall>>? byName = null)
    {
        ByPath = byPath;
        ByName = byName ?? new Dictionary<string, List<ExternalInstall>>(StringComparer.OrdinalIgnoreCase);
        Names = byPath.Keys.Select(p => Path.GetFileNameWithoutExtension(p)!).Concat(ByName.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public int PathCount => ByPath.Count;
    public int NameCount => Names.Length;
    public bool IsEmpty => ByPath.Count == 0 && ByName.Count == 0;
}

/// <summary>Passive detection only: never launches, waits on, or terminates a process. A process is a game when it runs from a game's recorded exe path, or - for a game
/// with no exe on record (an Xbox or EA game, anything started by address) - when it runs from inside that game's install folder. A process whose path Windows will not
/// reveal (an anti-cheat protects it) is recognised by name only when the name is one of exactly one game's main programs; nothing else is guessed.</summary>
internal sealed class ExternalGameDetector(IProcessProvider processes, IExecutableNameDiscovery? names = null)
{
    public ExternalGameDetector() : this(new SnapshotProcessProvider(), new FileSystemExecutableNameDiscovery()) { }

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

    private static string? NormalizeDir(string? dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir))
                return null;

            var full = Path.GetFullPath(dir);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
            // A drive root (or nothing deeper than it) is not a game's folder: everything on the drive would be "inside" it.
            return string.Equals(Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), trimmed, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public ExternalGameIndex BuildIndex(IReadOnlyList<ExternalGameCandidate> games)
    {
        var byPath = games.Select(g => (Game: g, Path: NormalizePath(g.ExecutablePath)))
            .Where(p => p.Path is not null).GroupBy(p => p.Path!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(p => p.Game.GameId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Game, StringComparer.OrdinalIgnoreCase);

        var byName = new Dictionary<string, List<ExternalInstall>>(StringComparer.OrdinalIgnoreCase);
        if (names is not null)
        {
            foreach (var game in games)
            {
                if (NormalizeDir(game.InstallDir) is not { } dir || !Directory.Exists(dir))
                    continue;

                var found = names.GetCandidateNames(dir);
                var ownExe = Path.GetFileNameWithoutExtension(NormalizePath(game.ExecutablePath) ?? "");
                foreach (var name in found)
                {
                    var main = found.Count == 1 || name.Equals(ownExe, StringComparison.OrdinalIgnoreCase) || name.EndsWith("Shipping", StringComparison.OrdinalIgnoreCase);
                    if (!byName.TryGetValue(name, out var list))
                        byName[name] = list = [];

                    list.Add(new ExternalInstall(game, dir, main));
                }
            }
        }

        return new ExternalGameIndex(byPath, byName);
    }

    public IReadOnlyList<ExternalGameCandidate> Detect(IReadOnlyList<ExternalGameCandidate> games, CancellationToken ct) =>
        Detect(BuildIndex(games), ct);

    public IReadOnlyList<ExternalGameCandidate> Detect(ExternalGameIndex index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (index.IsEmpty) return [];
        var found = processes.FindProcessesByName(index.Names);
        var matches = new Dictionary<string, ExternalGameCandidate>();
        try
        {
            foreach (var process in found)
            {
                ct.ThrowIfCancellationRequested();
                var path = NormalizePath(process.GetPath());
                if (path is not null && index.ByPath.TryGetValue(path, out var game))
                {
                    matches[game.GameId] = game;
                    continue;
                }

                if (!index.ByName.TryGetValue(process.ProcessName, out var installs))
                    continue;

                if (path is not null)
                {
                    // The path is known and is not a recorded exe: it counts only when it runs from inside one game's install folder (the deepest folder wins).
                    var inside = installs.Where(i => IsUnder(path, i.Dir)).ToList();
                    if (inside.Count == 0)
                        continue;

                    var deepest = inside.Max(i => i.Dir.Length);
                    var owners = inside.Where(i => i.Dir.Length == deepest).Select(i => i.Game.GameId).Distinct().ToList();
                    if (owners.Count == 1)
                        matches[owners[0]] = inside.First(i => i.Game.GameId == owners[0]).Game;
                }
                else
                {
                    // Protected: the name has to belong to exactly one game, and be one of its main programs (not a helper or a shared component).
                    var owners = installs.Select(i => i.Game.GameId).Distinct().ToList();
                    if (owners.Count == 1 && installs.Any(i => i.IsMainName))
                        matches[owners[0]] = installs[0].Game;
                }
            }
        }
        finally
        {
            foreach (var process in found) process.Dispose();
        }
        return matches.Values.ToArray();
    }
}

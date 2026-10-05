using System.Text;
using GameLauncher.Converters;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>Games found installed through more than one launcher - the same title in Steam and Epic, say - which usually means the same
/// gigabytes twice. Matching is deliberately strict: the scanner's own title (never a name the user chose), compared with punctuation,
/// spacing, case and the trademark marks ignored. A near-miss like "Game" and "Game: Deluxe Edition" is NOT reported - a missed
/// duplicate costs nothing, a wrong one sends someone to uninstall the wrong game.</summary>
public static class DuplicateDetector
{
    /// <summary>"The Witcher® 3: Wild Hunt" and "witcher 3 wild hunt" both become "witcher3wildhunt". Null for a title with no letters
    /// or digits at all, which can never be matched.</summary>
    public static string? KeyFor(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        // The marks go first: compatibility normalisation would otherwise spell the trademark sign out as the letters "TM".
        var builder = new StringBuilder(title.Length);
        foreach (var ch in title.Replace("™", "").Replace("®", "").Replace("©", "").Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }

        var key = builder.ToString();
        if (key.StartsWith("the", StringComparison.Ordinal) && key.Length > 3)
            key = key[3..];

        return key.Length == 0 ? null : key;
    }

    /// <summary>Every group of two or more games with the same key that came from at least two different launchers. Hidden games are left
    /// out - hiding one is how someone says they have already dealt with it.</summary>
    public static IReadOnlyList<IReadOnlyList<GameEntry>> Find(IEnumerable<GameEntry> games) =>
        games.Where(g => !g.Hidden)
            .Select(g => (Game: g, Key: KeyFor(g.DetectedTitle)))
            .Where(p => p.Key is not null)
            .GroupBy(p => p.Key!)
            .Select(group => (IReadOnlyList<GameEntry>)group.Select(p => p.Game).ToList())
            .Where(group => group.Select(g => g.Source).Distinct().Count() > 1)
            .ToList();

    /// <summary>"Also installed through Epic Games and GOG" - the launchers of the OTHER copies.</summary>
    public static string NoteFor(GameEntry game, IReadOnlyList<GameEntry> group)
    {
        var others = group.Where(g => g.Source != game.Source).Select(g => GameSourceDisplayConverter.Name(g.Source))
            .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        return others.Count switch
        {
            0 => "",
            1 => $"Also installed through {others[0]}",
            2 => $"Also installed through {others[0]} and {others[1]}",
            _ => "Also installed through " + string.Join(", ", others[..^1]) + " and " + others[^1],
        };
    }
}

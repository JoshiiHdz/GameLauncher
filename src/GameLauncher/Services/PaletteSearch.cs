namespace GameLauncher.Services;

public enum PaletteKind
{
    Game,
    Command,
}

/// <param name="Title">What is searched first and shown large.</param>
/// <param name="Subtitle">The quiet second line ("Steam - 12 h tracked").</param>
/// <param name="Keywords">Extra words that also match ("settings preferences options"), never shown.</param>
/// <param name="Execute">Run after the palette has closed.</param>
/// <param name="Bonus">Nudges a likely choice (a game played yesterday) above an equally good match.</param>
/// <param name="ShowWhenEmpty">Offered before anything is typed.</param>
/// <param name="Cover">A game's cover, shown as a small thumbnail (null for commands and games without art).</param>
/// <param name="Detail">The short label at the right of the row: the launcher's name for a game, "Command" for a command.</param>
public sealed record PaletteItem(string Title, string Subtitle, string Keywords, PaletteKind Kind, Action Execute, int Bonus = 0, bool ShowWhenEmpty = false,
    object? Cover = null, string Detail = "")
{
    /// <summary>"GAMES" or "COMMANDS" on the first row of each kind; null on the others. Set when the list is built.</summary>
    public string? Header { get; set; }
}

/// <summary>Ranks palette entries for a typed query. Every word typed must appear (in the title or the hidden keywords), in any order -
/// "ring elden" finds "Elden Ring". Better matches score higher: the exact title, then a title that starts with the text, then one with a
/// word that does, then one that merely contains it, then a keyword match.</summary>
public static class PaletteSearch
{
    /// <summary>0 means no match.</summary>
    public static int Score(string query, PaletteItem item)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return 0;

        var title = item.Title;
        var inTitle = words.All(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));
        var inKeywords = words.All(w => title.Contains(w, StringComparison.OrdinalIgnoreCase)
            || item.Keywords.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (!inKeywords)
            return 0;

        var trimmed = string.Join(' ', words);
        int score;
        if (!inTitle)
            score = 200;
        else if (string.Equals(title, trimmed, StringComparison.OrdinalIgnoreCase))
            score = 1000;
        else if (title.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            score = 800;
        else if (StartsAWord(title, words[0]))
            score = 600;
        else
            score = 400;

        return score + item.Bonus;
    }

    private static bool StartsAWord(string title, string word)
    {
        for (var i = title.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = title.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (i == 0 || !char.IsLetterOrDigit(title[i - 1]))
                return true;
        }

        return false;
    }

    /// <summary>The best <paramref name="max"/> matches, best first; ties go to the shorter title, then alphabetical. With nothing typed,
    /// the entries marked ShowWhenEmpty, best bonus first.</summary>
    public static IReadOnlyList<PaletteItem> Search(IEnumerable<PaletteItem> items, string? query, int max = 8)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return items.Where(i => i.ShowWhenEmpty).OrderByDescending(i => i.Bonus)
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        }

        return items.Select(i => (Item: i, Score: Score(query, i))).Where(p => p.Score > 0)
            .OrderByDescending(p => p.Score).ThenBy(p => p.Item.Title.Length).ThenBy(p => p.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(max).Select(p => p.Item).ToList();
    }
}

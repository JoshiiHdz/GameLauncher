using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Models;

namespace GameLauncher.ViewModels;

/// <summary>Which letter of the A to Z index a game's name files under: its first letter ("The Witcher" is under T), accents ignored ("Élan" is under E),
/// and everything that does not start with a letter - digits, symbols, other alphabets - under #.</summary>
public static class LibraryLetters
{
    public const string Other = "#";

    /// <summary>The whole index, in order: # first, then A to Z.</summary>
    public static IReadOnlyList<string> All { get; } = [Other, .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString())];

    public static string LetterOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Other;

        foreach (var ch in name.Trim())
        {
            if (!char.IsLetterOrDigit(ch))
                continue; // skip leading punctuation: "  - Hades" and "'Fall Guys'" are filed by the first letter or digit

            if (char.IsDigit(ch))
                return Other;

            var plain = ch.ToString().Normalize(NormalizationForm.FormD).FirstOrDefault(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
            var upper = char.ToUpperInvariant(plain);
            return upper is >= 'A' and <= 'Z' ? upper.ToString() : Other;
        }

        return Other;
    }
}

/// <summary>One heading of the Library grid and the games under it. When the grid is not sorted by name there is a single group with no heading.</summary>
public sealed class LetterGroup(string letter, IReadOnlyList<GameEntry> games, bool showHeader)
{
    public string Letter { get; } = letter;

    public IReadOnlyList<GameEntry> Games { get; } = games;

    public bool ShowHeader { get; } = showHeader;
}

/// <summary>One letter on the index rail beside the Library grid: dimmed when no game starts with it, lit while the grid is scrolled to it.</summary>
public sealed partial class LetterItem(string letter, bool hasGames) : ObservableObject
{
    public string Letter { get; } = letter;

    public bool HasGames { get; } = hasGames;

    [ObservableProperty]
    private bool _isCurrent;
}

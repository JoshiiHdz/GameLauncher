using GameLauncher.ViewModels;

namespace GameLauncher.Services;

/// <summary>What a key on the on-screen keyboard does.</summary>
public enum KeyKind { Char, Backspace, Shift, Symbols, Space, CaretLeft, CaretRight, Clear, Enter, Done }

/// <summary>One key: what it shows, what it does, the text it types (for a character), and how wide it is compared with a letter key (1).</summary>
public sealed record KeySpec(string Label, KeyKind Kind, string Text = "", double Width = 1);

public enum KeyboardLayer { Lower, Upper, Symbols }

/// <summary>The keys of the on-screen keyboard that the pad types with (a search, a note, a collection name, feedback), as plain data so the layout can be tested without a window.
/// Five rows: digits and Backspace; three rows of letters (or symbols); and a bottom row with the layer keys, Space, the cursor keys, Clear and Done.</summary>
public static class KeyboardLayout
{
    private static KeySpec[] Chars(string characters, KeyboardLayer layer) =>
        characters.Select(c => new KeySpec(layer == KeyboardLayer.Upper ? char.ToUpperInvariant(c).ToString() : c.ToString(), KeyKind.Char,
            layer == KeyboardLayer.Upper ? char.ToUpperInvariant(c).ToString() : c.ToString())).ToArray();

    /// <summary>The rows for a layer. A text box that accepts several lines gets an Enter key beside Done; one that does not has only Done.</summary>
    public static IReadOnlyList<IReadOnlyList<KeySpec>> Rows(KeyboardLayer layer, bool acceptsReturn)
    {
        var rows = new List<IReadOnlyList<KeySpec>>();

        // Row 1: the digits, and Backspace.
        rows.Add([.. Chars("1234567890", KeyboardLayer.Lower), new KeySpec("⌫", KeyKind.Backspace, Width: 1.6)]);

        if (layer == KeyboardLayer.Symbols)
        {
            rows.Add(Chars("!@#$%^&*()", KeyboardLayer.Lower));
            rows.Add(Chars("-_=+[]{}:;", KeyboardLayer.Lower));
            rows.Add([new KeySpec("ABC", KeyKind.Symbols, Width: 1.6), .. Chars("/\\|<>\"~`", KeyboardLayer.Lower), new KeySpec("'", KeyKind.Char, "'")]);
        }
        else
        {
            rows.Add(Chars("qwertyuiop", layer));
            rows.Add(Chars("asdfghjkl'", layer));
            rows.Add([new KeySpec("⇧", KeyKind.Shift, Width: 1.6), .. Chars("zxcvbnm,.?", layer)]);
        }

        var bottom = new List<KeySpec>();
        if (layer != KeyboardLayer.Symbols)
            bottom.Add(new KeySpec("123", KeyKind.Symbols, Width: 1.6));
        else
            bottom.Add(new KeySpec("abc", KeyKind.Symbols, Width: 1.6));

        bottom.Add(new KeySpec("Space", KeyKind.Space, " ", 4.2));
        bottom.Add(new KeySpec("◀", KeyKind.CaretLeft, Width: 1.1));
        bottom.Add(new KeySpec("▶", KeyKind.CaretRight, Width: 1.1));
        bottom.Add(new KeySpec("Clear", KeyKind.Clear, Width: 1.6));
        if (acceptsReturn)
            bottom.Add(new KeySpec("Enter", KeyKind.Enter, Width: 1.6));

        bottom.Add(new KeySpec("Done", KeyKind.Done, Width: 1.6));
        rows.Add(bottom);

        return rows;
    }

    /// <summary>The line of help under the keys: what the pad's own buttons do while the keyboard is up.</summary>
    public static IReadOnlyList<PadHintItem> HelpItems { get; } =
    [
        PadHintItem.Of(PadGlyph.A, "Type"), PadHintItem.Of(PadGlyph.X, "Backspace"), PadHintItem.Of(PadGlyph.Y, "Space"), PadHintItem.Of(PadGlyph.LB, PadGlyph.RB, "Move cursor"),
        PadHintItem.Of(PadGlyph.LT, "Shift"), PadHintItem.Of(PadGlyph.RT, "123"), PadHintItem.Of(PadGlyph.Start, "Done"), PadHintItem.Of(PadGlyph.B, "Close"),
    ];

    /// <summary>The same help as one line of text.</summary>
    public static string Help => PadHintItem.Join(HelpItems);
}

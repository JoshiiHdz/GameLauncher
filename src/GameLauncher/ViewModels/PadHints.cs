using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>A button on the pad as it is drawn in the hints: the four coloured face buttons, Start (a circle with three lines), the bumpers and triggers, and the right stick.</summary>
public enum PadGlyph { A, B, X, Y, Start, LB, RB, LT, RT, RightStick }

/// <summary>One thing the hints bar says: which button (or two, for a pair like LB RB) and what it does. Items with no button are plain sentences (the controller has gone to sleep).</summary>
public sealed record PadHintItem(IReadOnlyList<PadGlyph> Glyphs, string Label)
{
    public static PadHintItem Of(PadGlyph glyph, string label) => new([glyph], label);

    public static PadHintItem Of(PadGlyph first, PadGlyph second, string label) => new([first, second], label);

    public static PadHintItem Sentence(string text) => new([], text);

    /// <summary>The words for a button, as the text version of the hints names it.</summary>
    public static string NameOf(PadGlyph glyph) => glyph switch
    {
        PadGlyph.Start => "Start",
        PadGlyph.RightStick => "Right stick",
        _ => glyph.ToString(),
    };

    /// <summary>"A  Select", "LB RB  Move cursor", or just the sentence: the hint as plain text (tests, accessibility).</summary>
    public string Text => Glyphs.Count == 0 ? Label : $"{string.Join(" ", Glyphs.Select(NameOf))}  {Label}";

    /// <summary>How the hint appears when the items are joined into one line of text.</summary>
    public const string Gap = "      ";

    public static string Join(IEnumerable<PadHintItem> items) => string.Join(Gap, items.Select(i => i.Text));

    /// <summary>The glyph for a controller button that has one (A and B are fixed; the rest can be remapped but are still named for the button).</summary>
    public static PadGlyph? GlyphOf(PadButton button) => button switch
    {
        PadButton.Accept => PadGlyph.A,
        PadButton.Back => PadGlyph.B,
        PadButton.X => PadGlyph.X,
        PadButton.Y => PadGlyph.Y,
        PadButton.Start => PadGlyph.Start,
        PadButton.LB => PadGlyph.LB,
        PadButton.RB => PadGlyph.RB,
        PadButton.LT => PadGlyph.LT,
        PadButton.RT => PadGlyph.RT,
        _ => null,
    };
}

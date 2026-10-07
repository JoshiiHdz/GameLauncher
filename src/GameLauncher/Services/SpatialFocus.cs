using System.Windows;
using GameLauncher.ViewModels;

namespace GameLauncher.Services;

/// <summary>Which control the pad's D-pad or stick should go to next: pure geometry, so it is the same everywhere and can be tested without a window.
///
/// From the focused control's box it looks only at controls that lie further in the pressed direction.
/// - Left and Right stay in their own row: only a control whose box overlaps the focused one's vertically ("in the beam") is a candidate, so Right at the end of a row does
///   nothing instead of jumping to some other row. Of those the nearest wins.
/// - Up and Down prefer a control straight ahead (in the beam), but not blindly: a control off to the side that is clearly closer wins over one straight ahead but far
///   away (so Up from a tile at the end of a row lands on the card just above it, not on a button two sections up). With nothing straight ahead, the nearest wins, with
///   the sideways offset counted heavily so it stays near where the thumb pointed.</summary>
public static class SpatialFocus
{
    /// <summary>The index of the best candidate in <paramref name="candidates"/>, or -1 when nothing lies that way.</summary>
    public static int Pick(Rect from, IReadOnlyList<Rect> candidates, ShellDirection direction)
    {
        var horizontal = direction is ShellDirection.Left or ShellDirection.Right;
        var best = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            var box = candidates[i];
            var beside = !horizontal && Major(from, box, direction) <= 0 && !InBeam(from, box, direction); // level with the focused control, not above or below it
            if (!Lies(from, box, direction) || beside || (horizontal && !InBeam(from, box, direction)))
                continue;

            if (best < 0 || Better(from, box, candidates[best], direction))
                best = i;
        }

        return best;
    }

    private static bool Better(Rect from, Rect a, Rect b, ShellDirection direction)
    {
        if (BeamBeats(from, a, b, direction))
            return true;

        if (BeamBeats(from, b, a, direction))
            return false;

        return Score(from, a, direction) < Score(from, b, direction);
    }

    /// <summary>Straight ahead matters far more than sideways.</summary>
    private static double Score(Rect from, Rect box, ShellDirection direction)
    {
        var major = Major(from, box, direction);
        var minor = Minor(from, box, direction);
        return 13 * major * major + minor * minor;
    }

    /// <summary><paramref name="a"/> is in the beam and <paramref name="b"/> is not, and <paramref name="b"/> is not so much closer that going past it would be absurd.</summary>
    private static bool BeamBeats(Rect from, Rect a, Rect b, ShellDirection direction)
    {
        if (InBeam(from, b, direction) || !InBeam(from, a, direction))
            return false;

        if (!Lies(from, b, direction))
            return true;

        if (direction is ShellDirection.Left or ShellDirection.Right)
            return true;

        // Up and Down: b can only take the place of a control straight ahead if it is alongside too - a control well off to the side (a list of categories at the left of a
        // page of options) is not "the next one down", however little lower it is.
        if (SideGap(from, b, direction) > 4 * Major(from, b, direction) + 60)
            return true;

        // Alongside, b beats a only if it is completely closer: its far edge is nearer than a's near edge.
        return Major(from, a, direction) < Math.Max(1, MajorToFarEdge(from, b, direction));
    }

    /// <summary>The empty space between the boxes across the pressed direction (0 when they overlap that way).</summary>
    private static double SideGap(Rect from, Rect box, ShellDirection direction) =>
        direction is ShellDirection.Down or ShellDirection.Up
            ? Math.Max(0, Math.Max(box.Left - from.Right, from.Left - box.Right))
            : Math.Max(0, Math.Max(box.Top - from.Bottom, from.Top - box.Bottom));

    /// <summary>The box is on the pressed side: past the focused one's far edge, and not wholly behind its near edge.</summary>
    internal static bool Lies(Rect from, Rect box, ShellDirection direction) => direction switch
    {
        ShellDirection.Down => (from.Top < box.Top || from.Bottom <= box.Top) && from.Bottom < box.Bottom,
        ShellDirection.Up => (from.Bottom > box.Bottom || from.Top >= box.Bottom) && from.Top > box.Top,
        ShellDirection.Right => (from.Left < box.Left || from.Right <= box.Left) && from.Right < box.Right,
        _ => (from.Right > box.Right || from.Left >= box.Right) && from.Left > box.Left,
    };

    /// <summary>The gap between the boxes along the pressed direction (0 when they overlap that way).</summary>
    internal static double Major(Rect from, Rect box, ShellDirection direction) => Math.Max(0, direction switch
    {
        ShellDirection.Down => box.Top - from.Bottom,
        ShellDirection.Up => from.Top - box.Bottom,
        ShellDirection.Right => box.Left - from.Right,
        _ => from.Left - box.Right,
    });

    /// <summary>The distance from the focused control's edge to the box's far edge, along the pressed direction.</summary>
    private static double MajorToFarEdge(Rect from, Rect box, ShellDirection direction) => direction switch
    {
        ShellDirection.Down => box.Bottom - from.Bottom,
        ShellDirection.Up => from.Top - box.Top,
        ShellDirection.Right => box.Right - from.Right,
        _ => from.Left - box.Left,
    };

    /// <summary>How far off the box is, across the pressed direction. Going up or down, a box is as near as its centre or its left edge is to the focused one's: the pages are
    /// left-aligned lists, so a row is entered at its first control rather than wherever the centres happen to line up.</summary>
    internal static double Minor(Rect from, Rect box, ShellDirection direction) =>
        direction is ShellDirection.Down or ShellDirection.Up
            ? Math.Min(Math.Abs(Center(from.Left, from.Right) - Center(box.Left, box.Right)), Math.Abs(from.Left - box.Left))
            : Math.Abs(Center(from.Top, from.Bottom) - Center(box.Top, box.Bottom));

    /// <summary>Straight ahead: the boxes overlap across the pressed direction.</summary>
    public static bool InBeam(Rect from, Rect box, ShellDirection direction) =>
        direction is ShellDirection.Down or ShellDirection.Up
            ? box.Left < from.Right && box.Right > from.Left
            : box.Top < from.Bottom && box.Bottom > from.Top;

    private static double Center(double a, double b) => (a + b) / 2;
}

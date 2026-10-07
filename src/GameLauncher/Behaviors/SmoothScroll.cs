using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GameLauncher.Behaviors;

/// <summary>Scrolls a scroll area to a place by gliding there instead of jumping: each frame it covers a third of what is left, so it starts fast and settles gently, in about a
/// fifth of a second. With Windows' animations switched off (<see cref="Motion.AnimationsEnabled"/>) it jumps, as before. A new target while it is still gliding simply
/// changes where it is heading, and anything that scrolls the area directly (the right stick) calls <see cref="Stop"/> first so the two do not fight.</summary>
internal static class SmoothScroll
{
    private sealed class Glide
    {
        public double Current;
        public double Target;
        public DispatcherTimer? Timer;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, Glide> Glides = new();

    private const double Fraction = 0.34;
    private const double Settled = 0.5;

    /// <summary>Scrolls the area to a vertical offset (clamped to what it can scroll), gliding when animations are on.</summary>
    internal static void ToVertical(ScrollViewer scroller, double offset)
    {
        offset = Math.Clamp(offset, 0, scroller.ScrollableHeight);
        if (!Motion.AnimationsEnabled())
        {
            Stop(scroller);
            scroller.ScrollToVerticalOffset(offset);
            return;
        }

        if (Math.Abs(offset - scroller.VerticalOffset) < Settled && !Glides.TryGetValue(scroller, out _))
            return;

        var glide = Glides.GetOrCreateValue(scroller);
        if (glide.Timer is null)
            glide.Current = scroller.VerticalOffset; // starting from where it is; while gliding it carries on from where the glide has got to

        glide.Target = offset;
        if (glide.Timer is not null)
            return;

        glide.Timer = new DispatcherTimer(DispatcherPriority.Render, scroller.Dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
        glide.Timer.Tick += (_, _) =>
        {
            var left = glide.Target - glide.Current;
            if (Math.Abs(left) < Settled)
            {
                glide.Current = glide.Target;
                scroller.ScrollToVerticalOffset(Math.Clamp(glide.Target, 0, scroller.ScrollableHeight));
                Stop(scroller);
                return;
            }

            glide.Current += left * Fraction;
            scroller.ScrollToVerticalOffset(Math.Clamp(glide.Current, 0, scroller.ScrollableHeight));
        };
        glide.Timer.Start();
    }

    /// <summary>The offset a glide in progress is heading for, or the current offset when there is none (what a caller works out "how far to go" from).</summary>
    internal static double TargetOf(ScrollViewer scroller) =>
        Glides.TryGetValue(scroller, out var glide) && glide.Timer is not null ? glide.Target : scroller.VerticalOffset;

    /// <summary>Ends any glide in progress where it is.</summary>
    internal static void Stop(ScrollViewer scroller)
    {
        if (!Glides.TryGetValue(scroller, out var glide))
            return;

        glide.Timer?.Stop();
        glide.Timer = null;
    }
}

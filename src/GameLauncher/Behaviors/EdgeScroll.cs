using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace GameLauncher.Behaviors;

/// <summary>Hover-to-scroll for a horizontally scrolling row (Console-tile's shelves): resting the pointer near the left or right edge of the row
/// scrolls it toward that end, faster the closer the pointer is to the edge, and stops as soon as the pointer leaves the zone, leaves the row or
/// the row reaches its end. Set <c>beh:EdgeScroll.Enabled="True"</c> on the ScrollViewer.</summary>
public static class EdgeScroll
{
    private const double MaxSpeed = 1500; // pixels per second at the very edge

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(EdgeScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static readonly DependencyProperty ScrollerProperty = DependencyProperty.RegisterAttached(
        "Scroller", typeof(Scroller), typeof(EdgeScroll));

    /// <summary>True while a row is being scrolled by the pointer, so hover focus can hold still instead of chasing every tile that slides past.</summary>
    public static bool IsScrolling { get; private set; }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
            return;

        if ((bool)e.NewValue)
        {
            var scroller = new Scroller(viewer);
            viewer.SetValue(ScrollerProperty, scroller);
            viewer.PreviewMouseMove += scroller.OnMove;
            viewer.MouseLeave += scroller.OnLeave;
            viewer.Unloaded += scroller.OnLeave;
        }
        else if (viewer.GetValue(ScrollerProperty) is Scroller scroller)
        {
            viewer.PreviewMouseMove -= scroller.OnMove;
            viewer.MouseLeave -= scroller.OnLeave;
            viewer.Unloaded -= scroller.OnLeave;
            scroller.Stop();
            viewer.ClearValue(ScrollerProperty);
        }
    }

    private sealed class Scroller
    {
        private readonly ScrollViewer _viewer;
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly Stopwatch _clock = new();
        private TimeSpan _last;

        public Scroller(ScrollViewer viewer)
        {
            _viewer = viewer;
            _timer.Tick += (_, _) => Step();
        }

        public void OnMove(object sender, MouseEventArgs e)
        {
            if (!_timer.IsEnabled && Direction() != 0)
            {
                _clock.Restart();
                _last = TimeSpan.Zero;
                _timer.Start();
                IsScrolling = true;
            }
        }

        public void OnLeave(object sender, EventArgs e) => Stop();

        public void Stop()
        {
            _timer.Stop();
            _clock.Stop();
            IsScrolling = false;
        }

        /// <summary>-1..1: how far into the left or right edge zone the pointer is (0 outside the zones or when the row has nowhere to go).</summary>
        private double Direction()
        {
            if (!_viewer.IsMouseOver || _viewer.ScrollableWidth <= 0 || _viewer.ViewportWidth <= 0)
                return 0;

            var x = Mouse.GetPosition(_viewer).X;
            var zone = Math.Clamp(_viewer.ViewportWidth * 0.14, 64, 140);
            if (x < zone && _viewer.HorizontalOffset > 0)
                return -Math.Pow((zone - Math.Max(0, x)) / zone, 2);

            if (x > _viewer.ViewportWidth - zone && _viewer.HorizontalOffset < _viewer.ScrollableWidth)
                return Math.Pow((Math.Min(_viewer.ViewportWidth, x) - (_viewer.ViewportWidth - zone)) / zone, 2);

            return 0;
        }

        private void Step()
        {
            var direction = Direction();
            var now = _clock.Elapsed;
            var seconds = Math.Min(0.05, (now - _last).TotalSeconds);
            _last = now;
            if (direction == 0)
            {
                Stop();
                return;
            }

            _viewer.ScrollToHorizontalOffset(Math.Clamp(_viewer.HorizontalOffset + direction * MaxSpeed * seconds, 0, _viewer.ScrollableWidth));
        }
    }
}

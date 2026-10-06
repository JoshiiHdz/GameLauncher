using System.Windows;
using System.Windows.Media.Animation;

namespace GameLauncher.Behaviors;

/// <summary>CSS-style cubic-bezier(x1, y1, x2, y2) easing for WPF. Leave EasingMode at EaseIn: the curve already encodes the shape.
/// Y values may exceed 1 for overshoot (e.g. Console-tile focus: 0.2, 0.9, 0.3, 1.25).</summary>
public sealed class BezierEase : EasingFunctionBase
{
    public double X1 { get; set; } = 0.25;
    public double Y1 { get; set; } = 0.1;
    public double X2 { get; set; } = 0.25;
    public double Y2 { get; set; } = 1.0;

    public BezierEase() => EasingMode = EasingMode.EaseIn;

    protected override double EaseInCore(double normalizedTime)
    {
        var x = Math.Clamp(normalizedTime, 0, 1);
        if (x is 0 or 1) return x;
        var t = SolveT(x);
        return Bezier(t, Y1, Y2);
    }

    private static double Bezier(double t, double p1, double p2)
    {
        var u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }

    private static double BezierDerivative(double t, double p1, double p2)
    {
        var u = 1 - t;
        return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
    }

    private double SolveT(double x)
    {
        var t = x;
        for (var i = 0; i < 8; i++)
        {
            var err = Bezier(t, X1, X2) - x;
            if (Math.Abs(err) < 1e-5) return t;
            var d = BezierDerivative(t, X1, X2);
            if (Math.Abs(d) < 1e-6) break;
            t -= err / d;
        }
        // Bisection fallback
        double lo = 0, hi = 1; t = x;
        for (var i = 0; i < 20; i++)
        {
            var v = Bezier(t, X1, X2);
            if (Math.Abs(v - x) < 1e-5) break;
            if (v < x) lo = t; else hi = t;
            t = (lo + hi) / 2;
        }
        return t;
    }

    protected override Freezable CreateInstanceCore() => new BezierEase();
}

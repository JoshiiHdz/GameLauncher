namespace GameLauncher.Services;

/// <summary>How much bigger the PlayStation theme is drawn in controller mode: 20 percent. The whole window is scaled, so text, tiles, buttons and menus grow
/// together and the layout reflows to what is left. It is the same on every screen (a bigger factor looked huge on a TV). The one exception is a screen so small
/// that, once scaled, what is left would fall under the layout's own minimum (900 x 560, the point where it switches to its small layout): that screen keeps
/// the normal size. Sizes are in the window's device-independent units, so Windows' own display scaling is already taken into account.</summary>
public static class ControllerScale
{
    public const double Factor = 1.2;

    public static double For(double screenWidth, double screenHeight) =>
        screenWidth / Factor >= 900 && screenHeight / Factor >= 560 ? Factor : 1.0;
}

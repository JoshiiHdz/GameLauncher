using System.Windows;

namespace GameLauncher.Services;

/// <summary>Chooses between a theme's normal and "Small" layout values. A window narrower than 900 px or shorter than 560 px (the spec's
/// 760 x 480 case) is small. For every layout token the theme defines both of (<c>Name</c> and <c>NameSmall</c>), this publishes the one
/// that applies as an application resource called <c>ActiveName</c>, which XAML reads with DynamicResource - so a resize or a theme switch
/// needs no per-element code.</summary>
public static class ThemeLayout
{
    private static readonly string[] Keys =
    [
        "PageGutter", "CardWidth", "CardHeight", "HeroHeight", "FontSizeDisplay", "ToolTileWidth", "ToolTileHeight", "GuideWidth",
        "DialogWidth", "PaletteWidth", "SearchWidth", "SettingsTileWidth", "SettingsTileHeight", "SettingsNavWidth", "RibbonItemSize",
        "RibbonItemFocusedSize", "RibbonTop", "HeroTop", "FilterColumnWidth", "PaletteTop", "PageSideGutter",
    ];

    /// <summary>Raised after the published values changed (a resize into or out of the small size, or a theme switch).</summary>
    public static event EventHandler? Changed;

    public static bool IsSmall(double width, double height) => width < 900 || height < 560;

    /// <summary>Whether the small values are the ones currently published.</summary>
    public static bool CurrentSmall { get; private set; }

    public static void Apply(bool small)
    {
        CurrentSmall = small;
        ThemeState.Instance.UpdateSmall(small);
        var app = Application.Current;
        if (app is null)
            return;

        var changed = false;
        foreach (var key in Keys)
        {
            var value = ThemeManager.Token(small ? key + "Small" : key) ?? ThemeManager.Token(key);
            if (value is not null && !Equals(app.Resources["Active" + key], value))
            {
                app.Resources["Active" + key] = value;
                changed = true;
            }
        }

        app.Resources["ActivePaletteListHeight"] = small ? 300d : 420d;

        // Page header and body margins (the page layer's own padding: top, sides, and 32 below).
        if (app.Resources["ActivePageSideGutter"] is double side)
        {
            var top = ThemeManager.Token("PageTop") is double pt ? pt : 18;
            app.Resources["ActivePageHeaderMargin"] = new Thickness(side, top, side, 4);
            app.Resources["ActivePageBodyMargin"] = new Thickness(side, 0, side, 32);
        }

        // Thickness values derived from the gutter, for margins XAML cannot compute: the top bar's sides, and a scrolling body's sides and bottom.
        if (app.Resources["ActivePageGutter"] is double gutter)
        {
            app.Resources["ActiveGutterMargin"] = new Thickness(gutter, 0, gutter, 0);
            app.Resources["ActiveGutterBodyMargin"] = new Thickness(gutter, 0, gutter, 36);
            app.Resources["ActiveToastMargin"] = new Thickness(0, 0, gutter, 20);

            // Console-ribbon, positioned from the window top in the spec; its home starts 84 px down (caption 32 + top bar 52).
            var ribbonTop = app.Resources["ActiveRibbonTop"] is double rt ? rt : 96;
            var heroTop = app.Resources["ActiveHeroTop"] is double ht ? ht : 244;
            var focused = app.Resources["ActiveRibbonItemFocusedSize"] is double f ? f : 180;
            app.Resources["ActiveRibbonMargin"] = new Thickness(gutter, Math.Max(0, ribbonTop - 84), gutter, 0);
            app.Resources["ActiveRibbonHeight"] = focused + 56 + 16; // the focused icon, its title underneath, and the strip's scroll bar
            app.Resources["ActiveHeroLineHeight"] = (app.Resources["ActiveFontSizeDisplay"] is double fd ? fd : 44) * 1.12;
            app.Resources["ActiveHeroMargin"] = new Thickness(gutter, Math.Max(0, heroTop - 84), 0, 30); // 30 below: the key hints sit there
            app.Resources["ActiveActivityMargin"] = new Thickness(gutter, 0, gutter - 14, 28);
            app.Resources["ActiveCoverMargin"] = new Thickness(0, Math.Max(0, heroTop - 84), gutter, 0);
            app.Resources["ActiveFilterMargin"] = new Thickness(gutter, 10, 0, 20);
            app.Resources["ActivePosterMargin"] = new Thickness(28, 14, gutter, 36);
            app.Resources["ActiveSettingsNavColumn"] = new GridLength(app.Resources["ActiveSettingsNavWidth"] is double nw && nw > 0 ? nw + (ThemeManager.Current == ThemeId.ConsoleRibbon ? 28 : 24) : 0);
            app.Resources["ActiveFilterColumn"] = new GridLength(app.Resources["ActiveFilterColumnWidth"] is double fw ? fw : 240);
        }

        if (changed)
            Changed?.Invoke(null, EventArgs.Empty);
    }
}

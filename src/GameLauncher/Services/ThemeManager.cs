using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace GameLauncher.Services;

public enum ThemeId { Axis, ConsoleTile, ConsoleRibbon }

/// <summary>Swaps the theme's token and template dictionaries at runtime. Only presentation resources change: the library, its view
/// models and the running services are never touched, so a theme switch can happen live (Settings > Appearance > Theme).
/// The three dictionaries it owns are merged into the application resources: <c>Shared.Styles</c> (the control templates, which read
/// the tokens), then the active theme's <c>Tokens</c> and <c>Templates</c> (the theme's own structural resources). All three are loaded
/// again on every switch: the styles hold animations that read the theme's timings when they are built (see <see cref="Behaviors.ThemeTiming"/>),
/// and everything else reads tokens with DynamicResource, so replacing the set re-styles open windows.</summary>
public static class ThemeManager
{
    // WPF-UI draws its accent-coloured controls (primary Button, ToggleSwitch, CheckBox, ProgressBar, selection) from these keys.
    // ApplicationAccentColorManager.Apply writes its own values straight into the application resources, which win over any merged
    // dictionary, so after it runs the theme's values are copied over them again (see Apply).
    private static readonly string[] AccentKeys =
    [
        "SystemAccentColor", "SystemAccentColorPrimary", "SystemAccentColorSecondary", "SystemAccentColorTertiary",
        "SystemAccentColorPrimaryBrush", "SystemAccentColorSecondaryBrush", "SystemAccentColorTertiaryBrush",
        "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
        "TextOnAccentFillColorPrimaryBrush", "AccentTextFillColorPrimaryBrush",
    ];

    private static ResourceDictionary? _shared;
    private static ResourceDictionary? _tokens;
    private static ResourceDictionary? _templates;

    public static ThemeId Current { get; private set; } = ThemeId.Axis;

    /// <summary>One value from the active theme's token dictionary (a duration, easing, size...); null when there is none.</summary>
    public static object? Token(string key) => _tokens is not null && _tokens.Contains(key) ? _tokens[key] : null;

    /// <summary>Raised on the UI thread after the new dictionaries are in place.</summary>
    public static event EventHandler<ThemeId>? ThemeChanged;

    /// <summary>The Xbox style (Console-tile) is built and kept in the code but is not offered yet: it is left out of the theme picker and a saved choice of it opens as
    /// Axis. Set this to true to bring it back (nothing else changes).</summary>
    public const bool XboxStyleShips = false;

    /// <summary>Whether a theme can be chosen in this build.</summary>
    public static bool IsAvailable(ThemeId id) => id != ThemeId.ConsoleTile || XboxStyleShips;

    /// <summary>The theme a saved name stands for; anything unknown, empty or not available in this build is Axis, the default.</summary>
    public static ThemeId Parse(string? name) =>
        Enum.TryParse<ThemeId>(name, ignoreCase: true, out var id) && Enum.IsDefined(id) && IsAvailable(id) ? id : ThemeId.Axis;

    public static string DisplayName(ThemeId id) => id switch
    {
        ThemeId.ConsoleTile => "Xbox style",
        ThemeId.ConsoleRibbon => "PlayStation style",
        _ => "Axis",
    };

    private static ResourceDictionary Load(string name) =>
        new() { Source = new Uri($"pack://application:,,,/GameLauncher;component/Themes/{name}.xaml", UriKind.Absolute) };

    /// <summary>Installs the theme if none is installed yet (a window being built, a test fixture), without changing a loaded one.</summary>
    public static void EnsureLoaded(ThemeId id)
    {
        if (_tokens is null || Application.Current is { } app && !app.Resources.MergedDictionaries.Contains(_tokens))
            Apply(id);
    }

    /// <summary>Makes <paramref name="id"/> the active theme. Both of its dictionaries are loaded before anything is replaced, so a
    /// resource that fails to load leaves the previous theme fully in place.</summary>
    public static void Apply(ThemeId id)
    {
        var app = Application.Current ?? throw new InvalidOperationException("There is no running application to theme.");
        app.Dispatcher.VerifyAccess();
        if (!Enum.IsDefined(id))
            id = ThemeId.Axis;

        // The tokens are current before the styles load: those read their timings from them (see ThemeTiming).
        var previousTokens = _tokens;
        var tokens = Load($"{id}.Tokens");
        _tokens = tokens;
        ResourceDictionary shared, templates;
        try
        {
            shared = Load("Shared.Styles");
            templates = Load($"{id}.Templates");
        }
        catch
        {
            _tokens = previousTokens;
            throw;
        }

        var merged = app.Resources.MergedDictionaries;
        if (_shared is not null) merged.Remove(_shared);
        if (previousTokens is not null) merged.Remove(previousTokens);
        if (_templates is not null) merged.Remove(_templates);
        merged.Add(shared);
        merged.Add(tokens);
        merged.Add(templates);
        _shared = shared;
        _templates = templates;

        if (tokens["AccentColor"] is Color accent)
            ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark, systemGlassColor: false);

        foreach (var key in AccentKeys)
        {
            if (tokens.Contains(key))
                app.Resources[key] = tokens[key];
        }

        Current = id;
        ThemeState.Instance.Update(id);
        ThemeLayout.Apply(ThemeLayout.CurrentSmall); // the new theme's values for the size the window already is
        ThemeChanged?.Invoke(null, id);
    }
}

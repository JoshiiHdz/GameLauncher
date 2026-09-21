using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services.Identity;

/// <summary>Launcher-derived art: Steam's own CDN, keyed by the launcher's app id. It never guesses by name, so it is
/// unaffected by catalog ambiguity - and it is authorized only by the predicate (a live launcher id, and after a user
/// confirmation only when proven consistent with it).</summary>
public static class SteamLauncherArt
{
    public static (BitmapImage? Image, bool FromCache) Fetch(IdentityQuery query, CancellationToken ct) =>
        Fetch(query, cacheDirOverride: null, ct);

    public static (BitmapImage? Image, bool FromCache) Fetch(IdentityQuery query, string? cacheDirOverride, CancellationToken ct)
    {
        var steamId = query.LauncherIds.FirstOrDefault(l => l.Namespace == IdentifierNamespace.SteamApp);
        if (steamId is null)
            return (null, false);

        var scratch = new GameEntry
        {
            Id = $"steam-{steamId.Id}", Name = query.DetectedTitle, ExecutablePath = "", InstallDir = "", Source = GameSource.Steam,
        };
        var image = new SteamCoverArtProvider().GetCoverArt(scratch, out var fromCache, cacheDirOverride, ct);
        return (image, fromCache);
    }
}

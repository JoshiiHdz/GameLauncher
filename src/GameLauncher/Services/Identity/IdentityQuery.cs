using System.Security.Cryptography;
using System.Text;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services.Identity;

/// <summary>Everything a provider is allowed to know about a game when it looks for its catalog identity: the title the
/// scanner DETECTED and the launcher's own metadata. Immutable, and it has NO display-name member - a user's custom
/// name cannot reach a provider because there is nowhere in this type to put it (design I7, 2.1).
///
/// Built once per scan (and per resolution unit) from GameEntry.DetectedTitle, never from GameEntry.Name.</summary>
public sealed class IdentityQuery
{
    public string DetectedTitle { get; }
    public GameSource Source { get; }
    public IReadOnlyList<LauncherIdentifier> LauncherIds { get; }
    public string? InstallFolderName { get; }

    /// <summary>Today's GameEntry.CatalogName: an explicit, hand-verified, provenance-tagged expansion of an abbreviated
    /// title (EaScanner's "Apex" -> "Apex Legends"). Transitional (design 4.6): evidence a general rule may not extend.</summary>
    public string? CuratedHint { get; }

    private readonly Lazy<string> _fingerprint;

    public IdentityQuery(string detectedTitle, GameSource source, IEnumerable<LauncherIdentifier>? launcherIds = null,
        string? installFolderName = null, string? curatedHint = null)
    {
        DetectedTitle = detectedTitle;
        Source = source;
        LauncherIds = (launcherIds ?? Enumerable.Empty<LauncherIdentifier>()).ToList();
        InstallFolderName = installFolderName;
        CuratedHint = string.IsNullOrWhiteSpace(curatedHint) ? null : curatedHint;
        _fingerprint = new Lazy<string>(ComputeFingerprint);
    }

    /// <summary>The text every title search uses: the curated hint when there is one, else the detected title with any trademark notice
    /// ("(R)", "(TM)", a registered sign) removed - the DETECTED title itself stays untouched, evidence exactly as the launcher reported it.
    /// This is also what a cached lookup's recorded SearchedName is compared with (design 9.1, R8) and the picker's starting text.</summary>
    public string SearchTitle => CuratedHint ?? SteamGridDbCoverArtProvider.StripTrademarks(DetectedTitle);

    /// <summary>The "same inputs?" token for stale detection: a hash of the normalized title, the sorted launcher ids,
    /// the source and the hint. It contains no display name, so a rename never changes it (S1).</summary>
    public string Fingerprint => _fingerprint.Value;

    public bool HasLauncherId(LauncherIdentifier id) => LauncherIds.Contains(id);

    public bool HasLauncherId(IdentityKey key) => LauncherIds.Any(l => l.Namespace == key.Namespace && l.Id == key.Id);

    private string ComputeFingerprint()
    {
        var parts = new List<string>
        {
            "t=" + SteamGridDbCoverArtProvider.CollapsedTitle(DetectedTitle),
            "s=" + Source,
            "h=" + (CuratedHint is null ? "" : SteamGridDbCoverArtProvider.CollapsedTitle(CuratedHint)),
        };
        parts.AddRange(LauncherIds.Select(l => $"l={l.Namespace}:{l.Id}").OrderBy(p => p, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
    }

    /// <summary>Builds the query for a scanned game. Reads DetectedTitle, never Name.</summary>
    public static IdentityQuery From(GameEntry game) =>
        new(game.DetectedTitle, game.Source, ExtractLauncherIds(game), FolderName(game.InstallDir), game.CatalogName);

    /// <summary>The launcher ids every scanner already encodes in GameEntry.Id ("steam-{appid}", "gog-{id}",
    /// "epic-{appName}", "ubisoft-{id}"). Evidence for this scan only - never persisted as a resolved identity (3.2).</summary>
    internal static IReadOnlyList<LauncherIdentifier> ExtractLauncherIds(GameEntry game)
    {
        static string? After(string id, string prefix) =>
            id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length ? id[prefix.Length..] : null;

        var ids = new List<LauncherIdentifier>();
        switch (game.Source)
        {
            case GameSource.Steam when After(game.Id, "steam-") is { } steam:
                ids.Add(new LauncherIdentifier(IdentifierNamespace.SteamApp, steam));
                break;
            case GameSource.Gog when After(game.Id, "gog-") is { } gog:
                ids.Add(new LauncherIdentifier(IdentifierNamespace.GogProduct, gog));
                break;
            case GameSource.Epic when After(game.Id, "epic-") is { } epic:
                ids.Add(new LauncherIdentifier(IdentifierNamespace.EpicApp, epic));
                break;
            case GameSource.Ubisoft when After(game.Id, "ubisoft-") is { } ubisoft:
                ids.Add(new LauncherIdentifier(IdentifierNamespace.UbisoftGame, ubisoft));
                break;
        }

        return ids;
    }

    private static string? FolderName(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return null;

        var name = System.IO.Path.GetFileName(installDir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

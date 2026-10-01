using System.IO;
using System.Text.Json;

namespace GameLauncher.Services.CoverArt;

/// <summary>The matched-game evidence sidecar written next to a name-keyed cached cover (IGDB and SteamGridDB share it, and its
/// on-disk JSON, exactly). Reading is all-or-nothing: anything short of FULLY verified evidence is null, and the caller then deletes
/// both files and fetches again rather than keep serving something that can no longer be verified.
///
/// The record below is the evidence, field by field:
///  - Id / Title: what the lookup matched. A bare "{}" deserializes to the defaults (0 / null), and default values are not evidence.
///  - SearchedName: the resolved search identity (CatalogName ?? Name) the entry was fetched under, compared with the CURRENT one on
///    every read. A CacheVersion bump only clears mistakes once, at release time; an identity correction that lands afterwards
///    (a CatalogName the first scan never had) would otherwise leave a stale-identity entry being served indefinitely.
///  - ImageSha256: binds the sidecar to the SPECIFIC image bytes it was written for. The image and its sidecar are two files written
///    one after the other, so an old sidecar surviving a failed rewrite of the image must never be attributed to whatever image is
///    sitting at the cache path now.</summary>
internal static class MatchEvidenceSidecar
{
    private readonly record struct Evidence(int Id, string Title, string SearchedName, string ImageSha256);

    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    internal static (int Id, string Title)? TryRead(string metaPath, byte[] cachedImageBytes, string currentSearchName, string label)
    {
        try
        {
            if (!File.Exists(metaPath))
                return null;

            // Bounded (ProviderHttp.MaxSidecarBytes): an oversized sidecar is unverifiable evidence, exactly like a corrupt one.
            var text = ProviderImageIo.ReadBoundedSidecarText(metaPath, label);
            if (text is null)
                return null;

            var e = JsonSerializer.Deserialize<Evidence>(text);
            return e.Id > 0 && !string.IsNullOrWhiteSpace(e.Title) && !string.IsNullOrWhiteSpace(e.ImageSha256)
                   && string.Equals(e.SearchedName, currentSearchName, StringComparison.Ordinal)
                   && string.Equals(e.ImageSha256, Hash(cachedImageBytes), StringComparison.Ordinal)
                ? (e.Id, e.Title)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void Write(string metaPath, int id, string title, string searchName, byte[] imageBytes, string label)
    {
        try
        {
            File.WriteAllText(metaPath, JsonSerializer.Serialize(new Evidence(id, title, searchName, Hash(imageBytes))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"{label}: couldn't write match evidence for cached cover '{metaPath}'.", ex);
        }
    }

    internal static void TryDelete(string metaPath, string label)
    {
        try
        {
            if (File.Exists(metaPath))
                File.Delete(metaPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"{label}: couldn't delete stale match evidence file '{metaPath}'.", ex);
        }
    }
}

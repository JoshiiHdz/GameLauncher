using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace GameLauncher.Services.CoverArt;

/// <summary>The identity-bound cover cache (design 9): an image is cached under the RESOLVED catalog id, never under a
/// game id or a name - `{dir}\id-{id}-v{N}.png` + sidecar `{Id, Title, ImageSha256}`. An identity change therefore
/// changes the key: an old entry can never be served for a new identity and nothing ever needs deleting (I8).
///
/// Reads are bounded and go through the shared validator; a corrupt, oversized, or unverifiable entry is deleted and
/// treated as a miss. Whether an entry may be SERVED for a given identity is the caller's decision (the artwork
/// predicate, I11) - this class only guarantees that what it returns really is this id's image, intact.</summary>
internal static class IdKeyedCoverCache
{
    private sealed record Sidecar(string Id, string Title, string ImageSha256);

    internal static string PathFor(string dir, string id, int version) => Path.Combine(dir, $"id-{id}-v{version}.png");

    /// <summary>The validated image for `id`, or null (a miss). A hit needs: the file within its cap and a valid image,
    /// a sidecar within ITS cap whose Id equals `id` and whose hash matches these exact bytes.</summary>
    internal static BitmapImage? TryRead(string path, string id, string label)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var bytes = ProviderImageIo.ReadBoundedCacheFile(path, label);
            var image = bytes is null ? null : ArtworkImageValidator.ValidateProviderBytes(bytes, path);
            if (image is not null && bytes is not null && SidecarMatches(path + ".meta.json", id, bytes, label))
                return image;

            Logger.Warn($"{label}: cached cover '{Path.GetFileName(path)}' is corrupt or unverifiable - discarding it.");
            TryDelete(path);
            TryDelete(path + ".meta.json");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void Write(string path, string id, string title, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            var sidecar = new Sidecar(id, title, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
            File.WriteAllText(path + ".meta.json", JsonSerializer.Serialize(sidecar));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't cache the cover for id {id}.", ex);
        }
    }

    private static bool SidecarMatches(string metaPath, string id, byte[] imageBytes, string label)
    {
        try
        {
            if (!File.Exists(metaPath))
                return false;

            var text = ProviderImageIo.ReadBoundedSidecarText(metaPath, label);
            if (text is null)
                return false;

            var sidecar = JsonSerializer.Deserialize<Sidecar>(text);
            return sidecar is not null
                && !string.IsNullOrWhiteSpace(sidecar.Id) && string.Equals(sidecar.Id, id, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(sidecar.ImageSha256)
                && string.Equals(sidecar.ImageSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageBytes)), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

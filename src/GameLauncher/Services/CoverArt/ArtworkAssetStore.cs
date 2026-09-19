using System.IO;

namespace GameLauncher.Services.CoverArt;

/// <summary>Durable local storage for every user-selected cover image - deliberately separate from the
/// automatic-match CoverArtCache, which is versioned and forgotten on logic changes (see
/// SteamGridDbCoverArtProvider.CacheVersion) and so is not safe to depend on for something meant to
/// persist indefinitely. Keyed by a generated AssetId (a GUID), not GameEntry.Id - a dedup-ID migration
/// just copies the ArtworkSelection object (a string reference) onto the surviving override; no file I/O,
/// no renaming.
///
/// No delete/cleanup method exists here yet - automatic garbage collection of orphaned assets is
/// deliberately deferred (see the design record for why: an unreadable/corrupt settings file must never
/// be treated as "no references," and getting that wrong risks deleting recoverable artwork). Orphaned
/// files accumulate under CustomCovers for now - a bounded-in-practice but not technically bounded cost,
/// not a claim this is fine forever.</summary>
public static class ArtworkAssetStore
{
    private static readonly string DefaultStoreDir = Path.Combine(AppPaths.DataDir, "CustomCovers");

    // Matches exactly what CoverArtDecoder (a plain WPF/WIC BitmapImage decode - confirmed by reading it
    // directly) actually supports. WebP is deliberately excluded: WIC has no built-in WebP codec and
    // .NET ships none, so advertising it here would silently fail for real users.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "png", "jpg", "jpeg", "bmp", "gif", "tiff" };

    /// <summary>Resolves `assetId`/`extension` to a path strictly inside the store directory, or fails -
    /// used by every read/write here, and meant to be used by any future caller (repair, a later GC
    /// pass) too, so this validation can never be bypassed by constructing a path a different way.
    ///
    /// A bare StartsWith(storeDir) check is NOT sufficient on its own - a sibling directory like
    /// "CustomCovers-other" shares that exact string prefix. Path.GetRelativePath is what actually
    /// proves containment: anything genuinely outside storeDir produces a result starting with ".." or
    /// an absolute/rooted path (e.g. a different drive).
    ///
    /// `storeDirOverride` is test-only (production never passes it, always resolving under
    /// AppPaths.DataDir as the design requires) - a per-call parameter rather than a shared mutable
    /// static field specifically so parallel test execution (xUnit's default) can't race two tests
    /// against each other's override.</summary>
    public static bool TryResolvePath(string assetId, string extension, out string path, string? storeDirOverride = null)
    {
        path = "";

        if (!Guid.TryParseExact(assetId, "D", out _))
            return false;

        var normalizedExtension = extension.TrimStart('.').ToLowerInvariant();
        if (!AllowedExtensions.Contains(normalizedExtension))
            return false;

        var storeRoot = Path.GetFullPath(storeDirOverride ?? DefaultStoreDir);
        var candidate = Path.GetFullPath(Path.Combine(storeRoot, $"{assetId}.{normalizedExtension}"));

        var relative = Path.GetRelativePath(storeRoot, candidate);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;

        path = candidate;
        return true;
    }

    /// <summary>Writes and flushes `bytes` under a brand-new AssetId, returning it - never overwrites an
    /// existing asset (every Change Cover stages a NEW id; the previous one, if any, is simply left in
    /// place - see LibraryViewModel's commit ordering for why deletion never happens on this hot path).
    /// Caller must already have validated `bytes`/image content (see ArtworkImageValidator) - this only
    /// validates identity/path safety, not that the bytes are a real, well-formed image.
    ///
    /// Fully written and flushed to stable storage BEFORE returning, so a caller that goes on to record
    /// this AssetId into settings.json can never reference a write that hasn't actually landed on disk
    /// yet. `storeDirOverride`: see TryResolvePath.</summary>
    public static string Write(byte[] bytes, string extension, string? storeDirOverride = null)
    {
        var normalizedExtension = extension.TrimStart('.').ToLowerInvariant();
        if (!AllowedExtensions.Contains(normalizedExtension))
            throw new ArgumentException($"Unsupported artwork extension '{extension}'.", nameof(extension));

        var storeDir = storeDirOverride ?? DefaultStoreDir;
        Directory.CreateDirectory(storeDir);

        var assetId = Guid.NewGuid().ToString("D");
        if (!TryResolvePath(assetId, normalizedExtension, out var path, storeDirOverride))
            throw new InvalidOperationException("A freshly generated asset id failed its own path validation - this should never happen.");

        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true); // pushed to stable storage, not just the app's own buffer - see SettingsService.Save's identical reasoning
        }

        return assetId;
    }

    /// <summary>Null for anything short of a fully successful read: a failed identity/path validation,
    /// a missing file, or an I/O error. Callers (CoverArtService.ApplyStored) treat null as "the
    /// selection is retained, but its image can't currently be shown - fall back to the exe icon,"
    /// never as a reason to silently pick different artwork. `storeDirOverride`: see
    /// TryResolvePath.</summary>
    public static byte[]? TryRead(string assetId, string extension, string? storeDirOverride = null)
    {
        if (!TryResolvePath(assetId, extension, out var path, storeDirOverride))
            return null;

        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't read custom cover asset '{assetId}'.", ex);
            return null;
        }
    }
}

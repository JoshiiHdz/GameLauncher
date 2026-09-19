using System.IO;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>Fully isolated via ArtworkAssetStore's storeDirOverride parameter - a fresh temp directory
/// per test, never the real %AppData%\GameLauncher\CustomCovers, and safe under xUnit's default
/// parallel test execution since the override is passed per-call, not held in shared static state.</summary>
public class ArtworkAssetStoreTests : IDisposable
{
    private readonly string _storeDir;

    public ArtworkAssetStoreTests()
    {
        _storeDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Assets-" + Guid.NewGuid());
    }

    public void Dispose()
    {
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
    }

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var assetId = ArtworkAssetStore.Write(bytes, "png", _storeDir);
        var read = ArtworkAssetStore.TryRead(assetId, "png", _storeDir);

        Assert.NotNull(read);
        Assert.Equal(bytes, read);
    }

    [Fact]
    public void Write_GeneratesAValidGuidAssetId()
    {
        var assetId = ArtworkAssetStore.Write(new byte[] { 1 }, "png", _storeDir);
        Assert.True(Guid.TryParseExact(assetId, "D", out _));
    }

    [Fact]
    public void Write_RejectsUnsupportedExtension()
    {
        Assert.Throws<ArgumentException>(() => ArtworkAssetStore.Write(new byte[] { 1 }, "webp", _storeDir));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("12345678-1234-1234-1234-12345678901")] // one hex digit short
    [InlineData("../../../etc/passwd")]
    [InlineData("12345678-1234-1234-1234-123456789012/../../evil")]
    public void TryResolvePath_RejectsNonStrictGuidAssetId(string assetId)
    {
        Assert.False(ArtworkAssetStore.TryResolvePath(assetId, "png", out _, _storeDir));
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("exe")]
    [InlineData("")]
    [InlineData("png; DROP TABLE")]
    public void TryResolvePath_RejectsExtensionOutsideAllowlist(string extension)
    {
        var validId = Guid.NewGuid().ToString("D");
        Assert.False(ArtworkAssetStore.TryResolvePath(validId, extension, out _, _storeDir));
    }

    [Fact]
    public void TryResolvePath_AcceptsEveryAllowedExtension()
    {
        var validId = Guid.NewGuid().ToString("D");
        foreach (var ext in new[] { "png", "jpg", "jpeg", "bmp", "gif", "tiff" })
            Assert.True(ArtworkAssetStore.TryResolvePath(validId, ext, out _, _storeDir));
    }

    [Fact]
    public void TryResolvePath_ResolvedPath_IsStrictlyInsideTheStoreDirectory()
    {
        var validId = Guid.NewGuid().ToString("D");
        Assert.True(ArtworkAssetStore.TryResolvePath(validId, "png", out var path, _storeDir));

        var storeRoot = Path.GetFullPath(_storeDir);
        var relative = Path.GetRelativePath(storeRoot, Path.GetFullPath(path));
        Assert.False(relative.StartsWith("..", StringComparison.Ordinal));
        Assert.False(Path.IsPathRooted(relative));
    }

    [Fact]
    public void TryResolvePath_ResolvedPath_NeverFallsUnderASiblingDirectorySharingOnlyAStringPrefix()
    {
        // The specific bug a bare StartsWith(storeDir) check would miss: "CustomCovers-other" shares the
        // string "CustomCovers" as a prefix but is NOT inside it. With strict GUID parsing and an
        // extension allowlist already in place, an assetId/extension pair can never actually construct a
        // path escaping the store directory - this asserts that invariant directly (GetRelativePath-based
        // containment), rather than relying only on the fact that the other two checks happen to make it
        // unreachable in practice.
        var siblingRoot = Path.GetFullPath(_storeDir + "-other");
        var validId = Guid.NewGuid().ToString("D");

        Assert.True(ArtworkAssetStore.TryResolvePath(validId, "png", out var resolvedPath, _storeDir));

        var relativeToSibling = Path.GetRelativePath(siblingRoot, Path.GetFullPath(resolvedPath));
        Assert.StartsWith("..", relativeToSibling); // genuinely outside the sibling
    }

    [Fact]
    public void TryRead_MissingAsset_ReturnsNullWithoutThrowing()
    {
        var neverWrittenId = Guid.NewGuid().ToString("D");
        Assert.Null(ArtworkAssetStore.TryRead(neverWrittenId, "png", _storeDir));
    }

    [Fact]
    public void TryRead_InvalidAssetId_ReturnsNullWithoutThrowing()
    {
        Assert.Null(ArtworkAssetStore.TryRead("not-a-guid", "png", _storeDir));
    }

    [Fact]
    public void Write_NeverOverwritesAnExistingAsset_EachCallGetsItsOwnId()
    {
        var firstId = ArtworkAssetStore.Write(new byte[] { 1 }, "png", _storeDir);
        var secondId = ArtworkAssetStore.Write(new byte[] { 2 }, "png", _storeDir);

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(new byte[] { 1 }, ArtworkAssetStore.TryRead(firstId, "png", _storeDir));
        Assert.Equal(new byte[] { 2 }, ArtworkAssetStore.TryRead(secondId, "png", _storeDir));
    }
}

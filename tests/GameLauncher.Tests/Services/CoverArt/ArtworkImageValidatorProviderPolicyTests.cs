using System.Windows.Media.Imaging;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>B1: the two validation POLICIES on ArtworkImageValidator. The default (strict, asset-store
/// formats only) is what the user's Change Cover path has always used and must not change; the provider
/// policy relaxes ONLY the container-format allowlist and never the size/dimension/decode bounds.</summary>
public class ArtworkImageValidatorProviderPolicyTests
{
    [Fact]
    public void DefaultPolicy_StillRejectsAContainerOutsideTheAssetStoreFormats()
    {
        // Unchanged behaviour for every existing caller: ArtworkAssetStore can only stage five extensions.
        Assert.Null(ArtworkImageValidator.ValidateBytes(TestImages.Icon(), "ico"));
    }

    [Fact]
    public void ProviderPolicy_AcceptsThatSameContainer_ReturningTheFrozenBitmap()
    {
        var decoded = ArtworkImageValidator.ValidateProviderBytes(TestImages.Icon(), "ico");

        Assert.NotNull(decoded);
        Assert.True(decoded!.IsFrozen);
    }

    [Fact]
    public void ProviderPolicy_ReportsAnEmptyExtension_ForAnUnstageableContainer()
    {
        var validated = ArtworkImageValidator.ValidateBytes(TestImages.Icon(), "ico", requireAssetStoreFormat: false);

        Assert.NotNull(validated);
        Assert.Equal("", validated!.Extension);
    }

    [Fact]
    public void ProviderPolicy_StillReportsTheRealExtension_ForAStageableFormat()
    {
        var validated = ArtworkImageValidator.ValidateBytes(TestImages.Png(), "png", requireAssetStoreFormat: false);

        Assert.Equal("png", validated!.Extension);
    }

    [Theory]
    [InlineData(9000, 100)]
    [InlineData(100, 9000)]
    public void ProviderPolicy_StillEnforcesTheDimensionBounds(int width, int height)
    {
        // The relaxation is about the container FORMAT only; the bounds are what actually protect memory.
        Assert.Null(ArtworkImageValidator.ValidateProviderBytes(TestImages.Png(width, height), "oversized"));
    }

    [Fact]
    public void ProviderPolicy_StillEnforcesTheByteCap_AndTheEmptyAndCorruptChecks()
    {
        Assert.Null(ArtworkImageValidator.ValidateProviderBytes(new byte[ArtworkImageValidator.MaxFileBytes + 1], "big"));
        Assert.Null(ArtworkImageValidator.ValidateProviderBytes(Array.Empty<byte>(), "empty"));
        Assert.Null(ArtworkImageValidator.ValidateProviderBytes(new byte[] { 1, 2, 3, 4 }, "garbage"));
    }

    [Fact]
    public void ProviderPolicy_AcceptsAnOrdinaryInBoundsCover()
    {
        Assert.NotNull(ArtworkImageValidator.ValidateProviderBytes(TestImages.Png(600, 900), "cover"));
    }
}

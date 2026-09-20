using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>
/// EaScanner itself reads the real Windows registry, which isn't practical to fake in a unit test - so
/// this covers ResolveCatalogName directly, the one pure function AddIfPlayable delegates to for
/// resolving a known-abbreviated EA/Origin folder name to its real catalog title. This is the actual,
/// confirmed "Apex" -> "Apex Legends" identity gap from live v1.18.0 logs: EA installs Apex Legends
/// under a folder literally named "Apex", and no amount of loosening SteamGridDbCoverArtProvider's own
/// match comparison could safely recover from a search query that's an incomplete abbreviation rather
/// than a formatting variant of the real title.
/// </summary>
public class EaScannerTests
{
    [Fact]
    public void ResolveCatalogName_KnownAbbreviatedName_ReturnsTheRealCatalogTitle_ApexCase()
    {
        Assert.Equal("Apex Legends", EaScanner.ResolveCatalogName("Apex"));
    }

    [Fact]
    public void ResolveCatalogName_IsCaseInsensitive()
    {
        Assert.Equal("Apex Legends", EaScanner.ResolveCatalogName("apex"));
        Assert.Equal("Apex Legends", EaScanner.ResolveCatalogName("APEX"));
    }

    [Theory]
    [InlineData("Apex Legends")] // already the real title - must not be "corrected" again
    [InlineData("A Way Out")]
    [InlineData("AWayOut")]
    [InlineData("FC27")]
    [InlineData("Some Unrelated Game")]
    public void ResolveCatalogName_NoKnownMapping_ReturnsNull(string rawDetectedName)
    {
        Assert.Null(EaScanner.ResolveCatalogName(rawDetectedName));
    }
}

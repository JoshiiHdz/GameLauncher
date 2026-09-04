using System.Windows;
using GameLauncher.Converters;
using GameLauncher.Models;

namespace GameLauncher.Tests.Converters;

public class PlatformBadgeVisibilityConverterTests
{
    private static readonly object PlaceholderIcon = new();

    [Theory]
    [InlineData(GameSource.Xbox, null, Visibility.Visible)] // Xbox, no icon (the only real case) -> logo shows
    [InlineData(GameSource.Steam, null, Visibility.Collapsed)] // non-Xbox, no icon -> fallback symbol's job, not the logo's
    public void ForXboxLogo_ReturnsExpectedVisibility(GameSource source, object? platformIcon, Visibility expected)
    {
        var result = PlatformBadgeVisibilityConverter.ForXboxLogo.Convert(
            [source, platformIcon!], typeof(Visibility), null, null!);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ForXboxLogo_XboxWithAnIconAnyway_StaysCollapsed()
    {
        // Never happens in practice (Xbox's PlatformIcon is always null - see GameScannerService), but
        // the logo must still defer to a real extracted icon if one were ever present.
        var result = PlatformBadgeVisibilityConverter.ForXboxLogo.Convert(
            [GameSource.Xbox, PlaceholderIcon], typeof(Visibility), null, null!);

        Assert.Equal(Visibility.Collapsed, result);
    }

    [Theory]
    [InlineData(GameSource.Steam, null, Visibility.Visible)] // non-Xbox, no icon -> generic fallback glyph shows
    [InlineData(GameSource.Manual, null, Visibility.Visible)]
    [InlineData(GameSource.Xbox, null, Visibility.Collapsed)] // Xbox is always the logo's job, never this one's
    public void ForFallbackSymbol_ReturnsExpectedVisibility(GameSource source, object? platformIcon, Visibility expected)
    {
        var result = PlatformBadgeVisibilityConverter.ForFallbackSymbol.Convert(
            [source, platformIcon!], typeof(Visibility), null, null!);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BothVariants_AnySourceWithARealIcon_AreCollapsed()
    {
        // The extracted PlatformIcon Image itself is what should show - neither fallback layer should.
        var xboxLogo = PlatformBadgeVisibilityConverter.ForXboxLogo.Convert(
            [GameSource.Steam, PlaceholderIcon], typeof(Visibility), null, null!);
        var fallbackSymbol = PlatformBadgeVisibilityConverter.ForFallbackSymbol.Convert(
            [GameSource.Steam, PlaceholderIcon], typeof(Visibility), null, null!);

        Assert.Equal(Visibility.Collapsed, xboxLogo);
        Assert.Equal(Visibility.Collapsed, fallbackSymbol);
    }
}

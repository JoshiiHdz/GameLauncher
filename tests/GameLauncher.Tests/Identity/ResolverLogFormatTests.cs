using GameLauncher.Services.Identity;

namespace GameLauncher.Tests.Identity;

/// <summary>The cooldown log line is the only place a skipped game explains itself, so its time text is pinned.</summary>
public class ResolverLogFormatTests
{
    [Theory]
    [InlineData(4 * 60 + 12, "4h 12m")]
    [InlineData(60, "1h 0m")]
    [InlineData(59, "59m")]
    [InlineData(1, "1m")]
    [InlineData(0, "0m")]
    [InlineData(-5, "0m")]
    public void RemainingCooldown_IsReadableAndNeverNegative(int minutes, string expected) =>
        Assert.Equal(expected, AutomaticResolver.FormatRemaining(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void RemainingCooldown_ASecondsFractionRoundsUpToAMinute_NotToZero() =>
        Assert.Equal("1m", AutomaticResolver.FormatRemaining(TimeSpan.FromSeconds(20)));
}

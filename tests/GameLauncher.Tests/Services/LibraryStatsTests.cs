using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>The pure figures behind the Stats page, the low-space warning and the "What's using this drive?" page.</summary>
public class LibraryStatsTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static GameEntry Game(string name, long playSeconds = 0, double? lastPlayedDaysAgo = null, bool hidden = false, long? sizeBytes = null) => new()
    {
        Id = name, Name = name, ExecutablePath = @"C:\Games\" + name + @"\g.exe", InstallDir = @"C:\Games\" + name, Source = GameSource.Manual,
        Hidden = hidden,
        TotalPlaySeconds = playSeconds,
        LastPlayedUtc = lastPlayedDaysAgo is { } days ? Now.AddDays(-days) : null,
        InstallSizeBytes = sizeBytes,
    };

    // ---- stats -------------------------------------------------------------------------------------

    [Fact]
    public void AnEmptyLibrary_HasNothingToReport()
    {
        var stats = LibraryStats.Compute([], Now);

        Assert.Equal(0, stats.GameCount);
        Assert.Equal("None yet", stats.TotalPlayDisplay);
        Assert.False(stats.HasMostPlayed);
        Assert.True(stats.HasNoPlay);
        Assert.False(stats.HasPlayedThisWeek);
        Assert.False(stats.HasNotPlayed);
    }

    [Fact]
    public void TotalsAndCounts_CoverVisibleGamesOnly()
    {
        var stats = LibraryStats.Compute(
        [
            Game("A", 7200, 1),
            Game("B", 1800, 20),
            Game("C"),
            Game("Hidden", 99999, 1, hidden: true),
        ], Now);

        Assert.Equal(3, stats.GameCount);
        Assert.Equal(9000, stats.TotalPlaySeconds);
        Assert.Equal("2.5 h", stats.TotalPlayDisplay);
        Assert.Equal(2, stats.PlayedGameCount);
        Assert.Equal(1, stats.NotPlayedCount);
        Assert.True(stats.HasNotPlayed);
    }

    [Fact]
    public void MostPlayed_IsRankedLongestFirst_CappedAtFive_WithBarsRelativeToTheTop()
    {
        var games = Enumerable.Range(1, 7).Select(i => Game("G" + i, i * 600)).ToList();

        var stats = LibraryStats.Compute(games, Now);

        Assert.Equal(["G7", "G6", "G5", "G4", "G3"], stats.MostPlayed.Select(m => m.Name));
        Assert.Equal(1.0, stats.MostPlayed[0].Fraction);
        Assert.Equal(3.0 / 7, stats.MostPlayed[4].Fraction, precision: 6);
        Assert.Equal("1.2 h", stats.MostPlayed[0].Detail);
    }

    [Fact]
    public void ShortSessionsReadInMinutes_LongOnesInHours()
    {
        Assert.Equal("12 min", PlayTimeFormat.Duration(12 * 60));
        Assert.Equal("1 min", PlayTimeFormat.Duration(5));
        Assert.Equal("38.2 h", PlayTimeFormat.Duration((long)(38.2 * 3600)));
        Assert.Equal("", PlayTimeFormat.Duration(0));
    }

    [Fact]
    public void PlayedThisWeek_IsLastSevenDays_NewestFirst()
    {
        var stats = LibraryStats.Compute(
        [
            Game("Old", 600, 8),
            Game("Yesterday", 600, 1),
            Game("JustNow", 600, 0.01),
            Game("SixDays", 600, 6),
            Game("Never"),
        ], Now);

        Assert.Equal(["JustNow", "Yesterday", "SixDays"], stats.PlayedThisWeek.Select(p => p.Name));
        Assert.Equal("Played this week (3)", stats.PlayedThisWeekHeading);
    }

    // ---- low-space warning -------------------------------------------------------------------------

    private static DriveSpaceInfo Drive(long totalGb, double freeGb) => new()
    {
        Letter = "C:", Label = "Local Disk", TotalBytes = totalGb << 30, FreeBytes = (long)(freeGb * (1L << 30)),
    };

    [Theory]
    [InlineData(1000, 400, DriveSpaceLevel.Ok)]
    [InlineData(1000, 150, DriveSpaceLevel.Ok)]      // under 15% but over 100 GB free: plenty for a game
    [InlineData(1000, 30, DriveSpaceLevel.Low)]      // 3% of the drive and under 100 GB
    [InlineData(1000, 90, DriveSpaceLevel.Low)]      // under 10% of a big drive and under 100 GB
    [InlineData(4000, 300, DriveSpaceLevel.Ok)]      // 7.5% of 4 TB is still a lot of space - no nagging
    [InlineData(256, 24, DriveSpaceLevel.Low)]       // under 25 GB, whatever the percentage
    [InlineData(256, 9, DriveSpaceLevel.Critical)]
    [InlineData(1000, 0, DriveSpaceLevel.Critical)]
    public void TheLevel_FollowsFreeSpace_NotJustTheTotal(long totalGb, double freeGb, DriveSpaceLevel expected)
    {
        var drive = Drive(totalGb, freeGb);

        Assert.Equal(expected, drive.Level);
        Assert.Equal(expected != DriveSpaceLevel.Ok, drive.HasWarning);
        Assert.Equal(expected == DriveSpaceLevel.Low, drive.IsLow);
        Assert.Equal(expected == DriveSpaceLevel.Critical, drive.IsCritical);
    }

    [Fact]
    public void TheWarningText_NamesHowMuchIsFree_AndIsEmptyWhenAllIsWell()
    {
        Assert.Equal("Running low - 20 GB free", Drive(500, 20).WarningText);
        Assert.Equal("Almost full - only 4 GB free", Drive(500, 4).WarningText);
        Assert.Equal("", Drive(500, 300).WarningText);
    }

    // ---- what's using this drive -------------------------------------------------------------------

    private const long Gb = 1L << 30;

    [Fact]
    public void TheBreakdown_RanksMeasuredGames_BiggestFirst_WithAShareOfTheDrive()
    {
        var snapshot = StorageBreakdown.Build(
        [
            Game("Small", sizeBytes: 5 * Gb),
            Game("Huge", sizeBytes: 100 * Gb),
            Game("Tiny", sizeBytes: Gb / 10),
        ], usedBytes: 300 * Gb, totalBytes: 500 * Gb);

        Assert.Equal(["Huge", "Small", "Tiny"], snapshot.Rows.Select(r => r.Name));
        Assert.Equal(1.0, snapshot.Rows[0].Fraction);
        Assert.Equal(0.05, snapshot.Rows[1].Fraction, precision: 6);
        Assert.Equal("20% of drive", snapshot.Rows[0].ShareText);
        Assert.Equal("under 1% of drive", snapshot.Rows[2].ShareText);
    }

    [Fact]
    public void EverythingElse_IsUsedSpaceMinusTheGames_OnceAllAreMeasured()
    {
        var snapshot = StorageBreakdown.Build([Game("A", sizeBytes: 50 * Gb), Game("B", sizeBytes: 30 * Gb)], 300 * Gb, 500 * Gb);

        Assert.False(snapshot.HasUnmeasured);
        Assert.Equal(220 * Gb, snapshot.OtherBytes);
        Assert.Equal("Everything else on the drive: 220 GB", snapshot.OtherText);
        Assert.Equal("Games: 80 GB", snapshot.GamesTotalText);
    }

    [Fact]
    public void AGameNotMeasuredYet_IsCountedSeparately_AndHoldsBackTheEverythingElseFigure()
    {
        var snapshot = StorageBreakdown.Build([Game("A", sizeBytes: 50 * Gb), Game("B"), Game("C")], 300 * Gb, 500 * Gb);

        Assert.Single(snapshot.Rows);
        Assert.Equal(2, snapshot.UnmeasuredCount);
        Assert.Equal("2 games are still being measured...", snapshot.UnmeasuredText);
        Assert.Null(snapshot.OtherBytes);
        Assert.False(snapshot.HasOther);
    }

    [Fact]
    public void WhenTheDriveCannotBeRead_NoShareOrEverythingElseIsClaimed()
    {
        var snapshot = StorageBreakdown.Build([Game("A", sizeBytes: 50 * Gb)], usedBytes: null, totalBytes: 0);

        Assert.Equal("", snapshot.Rows[0].ShareText);
        Assert.Null(snapshot.OtherBytes);
    }
}

using GameLauncher;
using GameLauncher.Services;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The My PC page deals its sections into even columns instead of wrapping fixed-width cards.</summary>
public sealed class MyPcLayoutTests
{
    private static SystemInfoSection Section(string title, int lines) =>
        new(title, "Desktop24", Enumerable.Range(0, lines).Select(i => new SystemInfoItem("L" + i, "V" + i)).ToList());

    [Theory]
    [InlineData(1280, 3)]
    [InlineData(880, 3)]
    [InlineData(879, 2)]
    [InlineData(560, 2)]
    [InlineData(400, 1)]
    public void TheColumnCount_FollowsTheWidth(double width, int expected) => Assert.Equal(expected, MyPcPage.ColumnsFor(width));

    [Fact]
    public void Sections_AreDealtIntoEvenColumns_KeepingTheirOrder_AndNoneIsLost()
    {
        var sections = new[]
        {
            Section("System", 4), Section("Processor", 6), Section("Motherboard", 7), Section("Memory", 4), Section("Graphics", 7), Section("Storage", 2), Section("Gaming", 3),
        };

        var columns = MyPcPage.Columns(sections, 3);

        Assert.Equal(3, columns.Count);
        Assert.Equal(sections.Length, columns.Sum(c => c.Count));                          // every section is somewhere
        Assert.Equal(sections.OrderBy(s => s.Title), columns.SelectMany(c => c).OrderBy(s => s.Title)); // and only once
        var heights = columns.Select(c => c.Sum(s => s.Items.Count + 2)).ToList();
        Assert.True(heights.Max() - heights.Min() <= 8, $"the columns should be about as tall as each other ({string.Join(", ", heights)})");
        Assert.Equal("System", columns[0][0].Title);                                       // the first sections head the columns, in order
        Assert.Equal("Processor", columns[1][0].Title);
        Assert.Equal("Motherboard", columns[2][0].Title);
    }

    [Fact]
    public void AnEmptySnapshot_OrOneColumn_IsFine()
    {
        Assert.All(MyPcPage.Columns([], 3), c => Assert.Empty(c));
        var one = MyPcPage.Columns([Section("A", 2), Section("B", 2)], 1);
        Assert.Single(one);
        Assert.Equal(2, one[0].Count);
    }
}

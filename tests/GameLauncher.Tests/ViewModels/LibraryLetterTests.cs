using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

public sealed class LibraryLettersTests
{
    [Theory]
    [InlineData("Apex Legends", "A")]
    [InlineData("apex", "A")]
    [InlineData("  Baldur's Gate 3", "B")]
    [InlineData("The Witcher 3", "T")]
    [InlineData("Élan Vital", "E")]
    [InlineData("Ünravel", "U")]
    [InlineData("'Fall Guys'", "F")]
    [InlineData("- Hades", "H")]
    [InlineData("2064: Read Only Memories", "#")]
    [InlineData("7 Days to Die", "#")]
    [InlineData("!!! Weird", "W")]
    [InlineData("!!!", "#")]
    [InlineData("原神", "#")]
    [InlineData("", "#")]
    [InlineData(null, "#")]
    public void AGameIsFiledUnderItsFirstLetter_AccentsIgnored_AndEverythingElseUnderHash(string? name, string expected) =>
        Assert.Equal(expected, LibraryLetters.LetterOf(name));

    [Fact]
    public void TheIndexIsHashThenAToZ() =>
        Assert.Equal(27, LibraryLetters.All.Count);
}

[Collection(WpfStaCollection.Name)]
public sealed class LibraryGroupsAndRibbonTests(WpfStaFixture sta)
{
    private static (LibraryViewModel Vm, ShellState Shell) Create(params string[] names)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Letters-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        var games = names.Select((n, i) => new GameEntry
        {
            Id = "g" + i, Name = n, Source = GameSource.Manual, ExecutablePath = @"C:\G\" + n + ".exe", InstallDir = @"C:\G\" + n, DateAdded = new DateTime(2026, 1, 1).AddDays(i),
        }).ToList();
        vm.SimulateRefreshResult(games);
        var shell = new ShellState(vm);
        vm.SearchText = "x";
        vm.SearchText = ""; // runs the filter, as a scan would
        return (vm, shell);
    }

    private static List<string> Letters(ShellState shell) => shell.LibraryGroups.Select(g => g.Letter).ToList();

    // ---- the A to Z grouping ----

    [Fact]
    public void SortedByName_TheGamesAreFiledUnderLetters_InOrder_WithTheIndexOn() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (_, shell) = Create("Zelda", "Apex", "Élan", "Baldur", "7 Days", "Aces", "Hades");

            Assert.Equal(["#", "A", "B", "E", "H", "Z"], Letters(shell));
            Assert.Equal(["Aces", "Apex"], shell.LibraryGroups.Single(g => g.Letter == "A").Games.Select(g => g.Name));
            Assert.All(shell.LibraryGroups, g => Assert.True(g.ShowHeader));
            Assert.True(shell.ShowLetterIndex);
            Assert.Equal(LibraryLetters.All, shell.LetterIndex.Select(l => l.Letter));
            Assert.Equal(["#", "A", "B", "E", "H", "Z"], shell.LetterIndex.Where(l => l.HasGames).Select(l => l.Letter));
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void ZToA_ReversesTheLetters_AndAnyOtherSortHasNoHeadingsAndNoIndex() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (vm, shell) = Create("Apex", "Baldur", "Zelda");

            vm.SortOption = GameSortOption.NameDesc;
            Assert.Equal(["Z", "B", "A"], Letters(shell));
            Assert.True(shell.ShowLetterIndex);

            vm.SortOption = GameSortOption.MostPlayed;
            var group = Assert.Single(shell.LibraryGroups);
            Assert.False(group.ShowHeader);
            Assert.Equal(3, group.Games.Count);
            Assert.False(shell.ShowLetterIndex);

            vm.SortOption = GameSortOption.NameAsc;
            Assert.Equal(["A", "B", "Z"], Letters(shell));
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void ANarrowedLibrary_FilesOnlyWhatIsShown_AndAnEmptyOneHasNoIndex() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (vm, shell) = Create("Apex", "Baldur", "Zelda");

            vm.SearchText = "zel";
            Assert.Equal(["Z"], Letters(shell));

            vm.SearchText = "nothing matches this";
            Assert.Empty(shell.LibraryGroups);
            Assert.False(shell.ShowLetterIndex);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void TheCurrentLetterLightsOne_AndOnlyOne() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (_, shell) = Create("Apex", "Baldur");
            Assert.Equal(["A"], shell.LetterIndex.Where(l => l.IsCurrent).Select(l => l.Letter)); // starts on the first heading

            shell.SetCurrentLetter("B");

            Assert.Equal(["B"], shell.LetterIndex.Where(l => l.IsCurrent).Select(l => l.Letter));
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    // ---- keyboard movement follows the headings ----

    [Fact]
    public void ArrowKeysInTheLibraryGrid_FollowTheRows_AndANewLetterStartsANewRow() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            // Two columns: A has three games (a row of two, then a row of one), B has two.
            var (_, shell) = Create("Aa", "Ab", "Ac", "Ba", "Bb");
            shell.GridColumns = 2;
            shell.ShowLibraryCommand.Execute(null);
            var games = shell.LibraryGroups.SelectMany(g => g.Games).ToDictionary(g => g.Name);
            shell.FocusedGame = games["Ab"];

            Assert.True(shell.MoveFocus(ShellDirection.Down));                // from the second column of row 1 to the only game of row 2: it clamps to Ac
            Assert.Same(games["Ac"], shell.FocusedGame);

            Assert.True(shell.MoveFocus(ShellDirection.Right));               // past the end of A's last row: B starts a new row
            Assert.Same(games["Ba"], shell.FocusedGame);

            Assert.True(shell.MoveFocus(ShellDirection.Up));                  // back up to A's last row
            Assert.Same(games["Ac"], shell.FocusedGame);

            Assert.True(shell.MoveFocus(ShellDirection.Left));
            Assert.Same(games["Ab"], shell.FocusedGame);

            shell.FocusedGame = games["Bb"];
            Assert.False(shell.MoveFocus(ShellDirection.Down));               // nothing below the last row
            Assert.False(shell.MoveFocus(ShellDirection.Right));              // nothing after the last game
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    // ---- the ribbon on a big library ----

    private static string[] Names(int count) => Enumerable.Range(0, count).Select(i => "Game " + (char)('A' + i % 26) + i).ToArray();

    [Fact]
    public void ASmallLibrary_ShowsEveryGameOnTheStrip_WithNoAllGamesTile() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (_, shell) = Create(Names(ShellState.MaxRibbonGames));

            Assert.Equal(1 + ShellState.MaxRibbonGames, shell.RibbonItems.Count);          // the Library tile and every game
            Assert.DoesNotContain(RibbonMoreItem.Instance, shell.RibbonItems);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void ABigLibrary_KeepsTheStripShort_RecentlyPlayedFirst_ThenFavorites_ThenNewest_AndEndsWithAnAllGamesTile() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (vm, shell) = Create(Names(100));
            var all = vm.Games.ToList();
            var played = all.Take(12).ToList();
            for (var i = 0; i < played.Count; i++)
            {
                played[i].TotalPlaySeconds = 3600;
                played[i].LastPlayedUtc = new DateTime(2026, 10, 1).AddDays(i);
            }

            var favorite = all[40];
            favorite.Favorite = true;
            vm.SearchText = "x";
            vm.SearchText = "";

            var order = shell.RibbonOrder();
            var games = order.Where(g => g is not null).Select(g => g!).ToList();

            Assert.Null(order[0]);                                                          // the Library tile
            Assert.Equal(ShellState.MaxRibbonGames, games.Count);
            Assert.Equal(played.OrderByDescending(g => g.LastPlayedUtc).Take(10).Select(g => g.Id), games.Take(10).Select(g => g.Id)); // recently played, newest first, at most 10
            Assert.Contains(favorite, games);                                               // then the favorites
            Assert.Equal(games.Count, games.Distinct().Count());
            Assert.Same(RibbonMoreItem.Instance, shell.RibbonItems[^1]);                    // an "All N games" tile ends the strip
            Assert.Equal(1 + games.Count + 1, shell.RibbonItems.Count);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void AGameThatIsNotOnTheStrip_IsAddedWhenItIsFocused() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (vm, shell) = Create(Names(60));
            var outsider = vm.Games.First(g => !shell.RibbonItems.Contains(g));

            shell.FocusRibbonItem(outsider);

            Assert.Contains(outsider, shell.RibbonItems);
            Assert.Same(outsider, shell.FocusedGame);
            Assert.Same(RibbonMoreItem.Instance, shell.RibbonItems[^1]);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });
}

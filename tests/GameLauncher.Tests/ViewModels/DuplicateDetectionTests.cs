using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The same game installed through two launchers: how titles are matched (strictly), what the cards say, and the Duplicates view.</summary>
public class DuplicateDetectionTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static GameEntry Game(string id, string name, GameSource source) => new()
    {
        Id = id, Name = name, ExecutablePath = @"C:\G\" + id + ".exe", InstallDir = @"C:\G\" + id, Source = source,
    };

    // ---- matching ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("The Witcher® 3: Wild Hunt", "witcher3wildhunt")]
    [InlineData("witcher 3 wild hunt", "witcher3wildhunt")]
    [InlineData("THE WITCHER 3 - WILD HUNT", "witcher3wildhunt")]
    [InlineData("Half-Life 2™", "halflife2")]
    [InlineData("Ｃｕｐｈｅａｄ", "cuphead")]   // full-width letters read as plain ones
    [InlineData("The", "the")]                  // a title that is only the article keeps it
    public void TitlesAreComparedWithoutPunctuationCaseOrMarks(string title, string expected) =>
        Assert.Equal(expected, DuplicateDetector.KeyFor(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("™ ®")]
    public void ATitleWithNothingToCompare_NeverMatches(string? title) => Assert.Null(DuplicateDetector.KeyFor(title));

    [Fact]
    public void TheSameTitleThroughTwoLaunchers_IsADuplicate_WithANoteNamingTheOther()
    {
        var steam = Game("steam-1", "The Witcher 3: Wild Hunt", GameSource.Steam);
        var gog = Game("gog-1", "Witcher 3 Wild Hunt", GameSource.Gog);
        var other = Game("epic-1", "Fortnite", GameSource.Epic);

        var group = Assert.Single(DuplicateDetector.Find([steam, gog, other]));

        Assert.Equal(2, group.Count);
        Assert.Equal("Also installed through GOG", DuplicateDetector.NoteFor(steam, group));
        Assert.Equal("Also installed through Steam", DuplicateDetector.NoteFor(gog, group));
    }

    [Fact]
    public void ThreeCopies_NameBothOthers_InAReadableList()
    {
        var a = Game("steam-1", "Doom", GameSource.Steam);
        var b = Game("epic-1", "DOOM", GameSource.Epic);
        var c = Game("gog-1", "Doom", GameSource.Gog);
        var group = Assert.Single(DuplicateDetector.Find([a, b, c]));

        Assert.Equal("Also installed through Epic and GOG", DuplicateDetector.NoteFor(a, group));
    }

    [Fact]
    public void TwoCopiesFromTheSameLauncher_AreNotReported()
    {
        var a = Game("steam-1", "Doom", GameSource.Steam);
        var b = Game("steam-2", "Doom", GameSource.Steam);

        Assert.Empty(DuplicateDetector.Find([a, b]));
    }

    [Fact]
    public void AHiddenCopy_IsLeftOut_BecauseHidingItIsHowTheUserDealtWithIt()
    {
        var a = Game("steam-1", "Doom", GameSource.Steam);
        var b = Game("gog-1", "Doom", GameSource.Gog);
        b.Hidden = true;

        Assert.Empty(DuplicateDetector.Find([a, b]));
    }

    [Fact]
    public void ANearMiss_IsNotADuplicate()
    {
        var a = Game("steam-1", "Doom", GameSource.Steam);
        var b = Game("gog-1", "Doom Eternal", GameSource.Gog);
        var c = Game("epic-1", "Doom 2016", GameSource.Epic);

        Assert.Empty(DuplicateDetector.Find([a, b, c]));
    }

    [Fact]
    public void ACustomName_NeverCreatesOrHidesADuplicate_BecauseTheScannersTitleIsWhatCounts()
    {
        var a = Game("steam-1", "Doom", GameSource.Steam);
        var b = Game("gog-1", "Doom", GameSource.Gog);
        b.Name = "My favourite shooter"; // a rename: DetectedTitle is unchanged
        var c = Game("epic-1", "Fortnite", GameSource.Epic);
        c.Name = "Doom";

        var group = Assert.Single(DuplicateDetector.Find([a, b, c]));

        Assert.Equal([a, b], group);
    }

    // ---- in the library ----------------------------------------------------------------------------

    private LibraryViewModel Library(params GameEntry[] games)
    {
        var vm = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir))
        {
            InstallSizeEstimatorForTest = (_, _) => null,
        };
        vm.SimulateRefreshResult([.. games]);
        vm.SelectViewCommand.Execute("all");
        return vm;
    }

    [Fact]
    public void TheCards_CarryTheNote_AndTheSidebarOffersTheDuplicatesView()
    {
        var steam = Game("steam-1", "Doom", GameSource.Steam);
        var gog = Game("gog-1", "Doom", GameSource.Gog);
        var other = Game("epic-1", "Fortnite", GameSource.Epic);
        var vm = Library(steam, gog, other);

        Assert.True(vm.HasDuplicates);
        Assert.Equal(2, vm.DuplicateGameCount);
        Assert.Equal("Also installed through GOG", steam.DuplicateNote);
        Assert.True(steam.HasDuplicate);
        Assert.Equal("", other.DuplicateNote);
        Assert.False(other.HasDuplicate);
    }

    [Fact]
    public void TheDuplicatesView_ListsOnlyTheCopies_SideBySide_AndRetitlesTheGrid()
    {
        var vm = Library(
            Game("steam-1", "Doom", GameSource.Steam), Game("epic-9", "Apex", GameSource.Epic),
            Game("gog-1", "Doom", GameSource.Gog), Game("steam-2", "Portal", GameSource.Steam), Game("gog-2", "Portal", GameSource.Gog));

        vm.SelectViewCommand.Execute("duplicates");

        Assert.Equal(["Doom", "Doom", "Portal", "Portal"], vm.Games.Select(g => g.Name));
        Assert.Equal("Installed twice", vm.LibraryHeaderText);
        Assert.Equal("4 games installed through more than one launcher", vm.LibrarySubheaderText);
        Assert.True(vm.IsDuplicatesViewSelected);
        Assert.False(vm.IsAllViewSelected);
        Assert.False(vm.HasFeaturedGame);
    }

    [Fact]
    public void WhenTheLastDuplicateIsHidden_TheViewIsLeftAndTheEntryGoesAway()
    {
        var steam = Game("steam-1", "Doom", GameSource.Steam);
        var gog = Game("gog-1", "Doom", GameSource.Gog);
        var vm = Library(steam, gog);
        vm.SelectViewCommand.Execute("duplicates");

        vm.ToggleHiddenCommand.Execute(gog);

        Assert.False(vm.HasDuplicates);
        Assert.True(vm.IsAllViewSelected);
        Assert.False(vm.IsDuplicatesViewSelected);
        Assert.Equal("", steam.DuplicateNote);
        Assert.Contains(steam, vm.Games);
    }

    [Fact]
    public void ALibraryWithoutDuplicates_HasNoDuplicatesEntry()
    {
        var vm = Library(Game("steam-1", "Doom", GameSource.Steam), Game("gog-1", "Portal", GameSource.Gog));

        Assert.False(vm.HasDuplicates);
        Assert.Equal(0, vm.DuplicateGameCount);
    }

    [Fact]
    public void TheDuplicatesView_StillHonoursSearch()
    {
        var vm = Library(
            Game("steam-1", "Doom", GameSource.Steam), Game("gog-1", "Doom", GameSource.Gog),
            Game("steam-2", "Portal", GameSource.Steam), Game("gog-2", "Portal", GameSource.Gog));
        vm.SelectViewCommand.Execute("duplicates");

        vm.SearchText = "port";

        Assert.Equal(["Portal", "Portal"], vm.Games.Select(g => g.Name));
    }
}

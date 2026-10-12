using System.IO;
using GameLauncher.Converters;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The details page and the home cards: a long install path is not cut off, a size is measured for the game being looked at (not "not measured"), and
/// there are no notes to add.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class DetailsFitTests(WpfStaFixture sta)
{
    [Fact]
    public void ALongPath_GetsPlacesToBreak_AndIsOtherwiseUnchanged()
    {
        const string path = @"C:\Program Files (x86)\Steam\steamapps\common\A Game With A Very Long Folder Name Indeed";

        var shown = PathWrapConverter.Wrappable(path);

        Assert.Equal(path, shown.Replace("\u200B", ""));                       // nothing else changed
        Assert.Contains("\\\u200B", shown);                                    // a line may break after every separator
        Assert.Equal(path.Count(c => c == '\\'), shown.Count(c => c == '\u200B'));
        Assert.Equal("", PathWrapConverter.Wrappable(null));
    }

    [Fact]
    public void TheSizeText_SaysWhatIsHappening_NotJustNotMeasured()
    {
        var folder = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Size-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var game = new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = Path.Combine(folder, "a.exe"), InstallDir = folder };
            using var details = new GameDetailsViewModel(game);

            Assert.Equal("Measuring...", details.SizeText);                    // the folder is there, the walk is under way

            game.InstallSizeBytes = 3L << 30;
            Assert.Equal("3 GB", details.SizeText);

            var missing = new GameEntry { Id = "2", Name = "Gone", Source = GameSource.Steam, ExecutablePath = @"C:\x\g.exe", InstallDir = Path.Combine(folder, "nope") };
            using var gone = new GameDetailsViewModel(missing);
            Assert.Equal("Install folder not found", gone.SizeText);           // nothing to wait for

            missing.InstallSizeBytes = 0;
            Assert.Equal("Install folder not found", gone.SizeText);           // and a measured nothing is not "0 B"
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void TheGameBeingLookedAt_IsMeasuredFirst_NotAfterEveryOtherUnmeasuredGame() => sta.RunAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-SizeFirst-" + Guid.NewGuid());
        var order = new List<string>();
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory))
        {
            InstallSizeEstimatorForTest = (dir, _) =>
            {
                order.Add(dir);
                return 1024;
            },
        };
        vm.SimulateRefreshResult(
        [
            new GameEntry { Id = "a", Name = "A", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" },
            new GameEntry { Id = "b", Name = "B", Source = GameSource.Steam, ExecutablePath = @"C:\G\b.exe", InstallDir = @"C:\G\b" },
            new GameEntry { Id = "c", Name = "C", Source = GameSource.Steam, ExecutablePath = @"C:\G\c.exe", InstallDir = @"C:\G\c" },
        ]);
        vm.SearchText = "x"; // runs the production filter, so the games are in Games
        vm.SearchText = "";
        order.Clear();
        foreach (var g in vm.Games)
            g.InstallSizeBytes = null;

        vm.EnsureInstallSizeMeasured(vm.Games.Single(g => g.Id == "c"));

        Assert.Equal(@"C:\G\c", order[0]);                                     // the one on screen, then the rest
        Assert.Equal(3, order.Count);
        Assert.All(vm.Games, g => Assert.True(g.HasInstallSize));
        await Task.CompletedTask;
    });

    [Fact]
    public void TheHomeCards_AreSixEvenCards_WithPriorityFirst_AndNoNotes() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Cards-" + Guid.NewGuid());
            var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => 5L << 30 };
            vm.SimulateRefreshResult([new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\Program Files (x86)\Steam\steamapps\common\Apex" }]);
            vm.SearchText = "x";
            vm.SearchText = "";
            var shell = new ShellState(vm);

            shell.FocusRibbonItem(vm.Games.First());

            var cards = shell.ActivityCards.ToList();
            Assert.Equal(6, cards.Count);                                      // even: three rows of two
            Assert.Equal("HIGH PRIORITY", cards[0].Label);
            Assert.DoesNotContain(cards, c => c.Label.Contains("NOTES"));
            Assert.Equal(@"C:\Program Files (x86)\Steam\steamapps\common\Apex", cards.Single(c => c.Label == "INSTALLED IN").Value); // the whole path (it wraps; it is not cut)
            Assert.Equal("5 GB", cards.Single(c => c.Label == "SIZE ON DISK").Value);                                               // measured as the card came up
            await Task.CompletedTask;
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });
}

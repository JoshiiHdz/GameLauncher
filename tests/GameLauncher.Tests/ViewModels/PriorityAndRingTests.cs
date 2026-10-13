using System.IO;
using System.Windows;
using System.Windows.Media;
using GameLauncher.Converters;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The per-game High priority switch in every theme's menu and cards, the drive ring whose arc ran outside its circle, and "last played" saying the same thing
/// everywhere.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class PriorityAndRingTests(WpfStaFixture sta)
{
    private static LibraryViewModel NewLibrary(out GameEntry game)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Priority-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        game = new GameEntry { Id = "g1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G" };
        vm.SimulateRefreshResult([game]);
        return vm;
    }

    [Fact]
    public void HighPriority_IsOneClickFromAGamesMenu_AndTheMenuSaysWhichWayItGoes() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary(out var game);
        var shell = new ShellState(vm);

        shell.OpenCardMenu(game, new Rect(0, 0, 10, 10));
        Assert.Contains(shell.MenuItems, i => i.Header == "Turn on high priority");
        Assert.False(game.HighPriority);

        shell.MenuItems.Single(i => i.Header == "Turn on high priority").Run!();   // the click

        Assert.True(vm.GetRaiseGamePriority(game.Id));
        Assert.True(game.HighPriority);                                            // what the Axis context menu binds to
        Assert.Contains("high priority", vm.StatusText);

        shell.CloseMenu();
        shell.OpenCardMenu(game, new Rect(0, 0, 10, 10));
        Assert.Contains(shell.MenuItems, i => i.Header == "Turn off high priority");

        vm.ToggleHighPriorityCommand.Execute(game);
        Assert.False(vm.GetRaiseGamePriority(game.Id));
        Assert.False(game.HighPriority);
        await Task.CompletedTask;
    });

    [Fact]
    public void ThePriorityChoiceMadeOnTheDetailsPage_ShowsInTheMenuToo() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary(out var game);

        vm.SetRaiseGamePriority(game.Id, true);                                    // what closing the details page does

        Assert.True(game.HighPriority);
        Assert.Equal("Turn off high priority", HighPriorityToMenuTextConverter.Instance.Convert(game.HighPriority, typeof(string), null!, null!));
        await Task.CompletedTask;
    });

    [Fact]
    public void TheHomeCard_IsASwitchWithAnOnOffPill_NotATextFact() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary(out var game);
        var card = new ActivityCard("HIGH PRIORITY", "Runs at normal priority", "Press to switch", () => { }, state: false);

        Assert.True(card.HasState);
        Assert.Equal("OFF", card.StateText);

        var on = new ActivityCard("HIGH PRIORITY", "Runs at high priority while it plays", "Press to switch", () => { }, state: true);
        card.CopyFrom(on);

        Assert.Equal("ON", card.StateText);                                        // a card on screen is updated in place
        Assert.False(new ActivityCard("PLAY TIME", "1 h", "", () => { }).HasState);
        _ = (vm, game);
        await Task.CompletedTask;
    });

    // ---- the drive ring ---------------------------------------------------------------------------------------------------------

    private static (double X, double Y) Centre(Geometry g)
    {
        var bounds = g.Bounds;
        return (bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    }

    [Theory]
    [InlineData("40,36,32,2.5", 20.0, 18.0, 14.75)]   // Axis's rail: a 32 px ring, 2.5 stroke, in a 40 x 36 cell
    [InlineData("40,40,34,4", 20.0, 20.0, 15.0)]      // the PlayStation theme's drive card: 34 px, 4 stroke, in 40 x 40
    public void TheArc_SitsOnTheCentreLineOfItsRing(string ring, double cx, double cy, double radius)
    {
        var (x, y, r) = UsedFractionToArcConverter.Circle(ring);
        Assert.Equal((cx, cy, radius), (x, y, r));

        // A quarter full: from twelve o'clock to three o'clock, both ends on that circle.
        var geometry = (PathGeometry)UsedFractionToArcConverter.Instance.Convert(0.25, typeof(Geometry), ring, null!);
        var figure = geometry.Figures.Single();
        var start = figure.StartPoint;
        var end = ((ArcSegment)figure.Segments.Single()).Point;

        Assert.Equal(cx, start.X, 3);
        Assert.Equal(cy - radius, start.Y, 3);
        Assert.Equal(cx + radius, end.X, 3);
        Assert.Equal(cy, end.Y, 3);
    }

    [Fact]
    public void AFullDrive_IsTheWholeRing_OnTheSameCircle()
    {
        var geometry = (EllipseGeometry)UsedFractionToArcConverter.Instance.Convert(1.0, typeof(Geometry), "40,40,34,4", null!);

        Assert.Equal(new Point(20, 20), geometry.Center);
        Assert.Equal(15, geometry.RadiusX);
        Assert.Equal(15, geometry.RadiusY);
    }

    [Fact]
    public void WithNoRing_TheOriginalNumbersStillApply()
    {
        Assert.Equal((18.0, 18.0, 14.75), UsedFractionToArcConverter.Circle(null));
        Assert.Equal((18.0, 18.0, 14.75), UsedFractionToArcConverter.Circle("nonsense"));
    }

    // ---- "last played" ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void LastPlayed_SaysToday_ForASessionEarlierTheSameLocalDay_InAnyEvening()
    {
        // 10 pm local on the 11th; the game was played at 10 am local the same day. Today's date in UTC can already be the 12th in a zone behind Greenwich, which is what
        // made one screen say "Yesterday" while another said "Today".
        var nowLocal = new DateTime(2026, 10, 11, 22, 0, 0, DateTimeKind.Local);
        var earlierToday = new DateTime(2026, 10, 11, 10, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var yesterday = new DateTime(2026, 10, 10, 20, 0, 0, DateTimeKind.Local).ToUniversalTime();

        Assert.Equal("Today", GameEntry.FormatRelative(earlierToday, nowLocal));
        Assert.Equal("Yesterday", GameEntry.FormatRelative(yesterday, nowLocal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void TheGameCard_AndThePlayTimePage_NameTheSameDay(int daysAgo)
    {
        var nowLocal = new DateTime(2026, 10, 11, 23, 30, 0, DateTimeKind.Local);
        var then = nowLocal.AddDays(-daysAgo).Date.AddHours(9).ToUniversalTime();

        var card = GameEntry.FormatRelative(then, nowLocal);
        var page = PlayHistory.When(then, nowLocal.ToUniversalTime(), TimeZoneInfo.Local);

        Assert.Equal(page, card);
    }

    [Fact]
    public void TheGamesOwnProcess_StartingAfterTheLauncherStub_IsRaisedToo() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary(out var game);
        vm.SetRaiseGamePriority(game.Id, true);
        var raised = new List<int>();
        var before = GamePriority.RaiseForTest;
        GamePriority.RaiseForTest = id =>
        {
            lock (raised)
                raised.Add(id);

            return true;
        };

        try
        {
            var calls = 0;
            IReadOnlyCollection<int> Find()
            {
                // The first look finds only the stub (an anti-cheat or launcher helper); the game's own process comes a few looks later.
                return Interlocked.Increment(ref calls) < 3 ? [100] : [100, 200];
            }

            using var cts = new CancellationTokenSource();
            var keeper = vm.KeepGamePriorityAsync(game, Find, cts.Token, quick: TimeSpan.FromMilliseconds(10), slow: TimeSpan.FromMilliseconds(10));
            for (var i = 0; i < 300; i++)
            {
                lock (raised)
                {
                    if (raised.Contains(200))
                        break;
                }

                await Task.Delay(10);
            }

            lock (raised)
            {
                Assert.Contains(200, raised);                                       // the game itself, found late
                Assert.Equal(1, raised.Count(id => id == 100));                      // and the stub was only tried once, not every look
            }

            Assert.Contains("running at high priority", vm.StatusText);
            cts.Cancel();
            await keeper;                                                           // ends with the session
        }
        finally
        {
            GamePriority.RaiseForTest = before;
        }
    });

    [Fact]
    public void WithTheSwitchOff_TheKeeperRaisesNothing() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary(out var game);                                          // High priority is off for this game
        var raised = 0;
        var before = GamePriority.RaiseForTest;
        GamePriority.RaiseForTest = _ => { Interlocked.Increment(ref raised); return true; };
        try
        {
            using var cts = new CancellationTokenSource();
            var keeper = vm.KeepGamePriorityAsync(game, () => [100], cts.Token, quick: TimeSpan.FromMilliseconds(5), slow: TimeSpan.FromMilliseconds(5));
            await Task.Delay(120);
            cts.Cancel();
            await keeper;

            Assert.Equal(0, raised);
        }
        finally
        {
            GamePriority.RaiseForTest = before;
        }
    });
}

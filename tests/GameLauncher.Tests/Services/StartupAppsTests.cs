using System.IO;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Services;

/// <summary>The startup-apps manager: the Task Manager way, so it is reversible and never deletes or edits an entry.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class StartupAppsTests(WpfStaFixture sta)
{
    private sealed class FakeStartup : IStartupSource
    {
        public List<(string Name, string Command)> Runs { get; } = new();
        public List<string> Folder { get; } = new();
        public Dictionary<(string Area, string Name), bool> Approved { get; } = new();
        public List<(string Area, string Name, bool Enabled)> Writes { get; } = new();

        public IReadOnlyList<(string Name, string Command)> RunEntries() => Runs;
        public IReadOnlyList<string> FolderEntries() => Folder;
        public bool? ReadApproved(string area, string name) => Approved.TryGetValue((area, name), out var v) ? v : null;

        public void WriteApproved(string area, string name, bool enabled)
        {
            Writes.Add((area, name, enabled));
            Approved[(area, name)] = enabled;
        }
    }

    private static FakeStartup Source()
    {
        var source = new FakeStartup();
        source.Runs.Add(("Discord", @"C:\Users\x\AppData\Local\Discord\Update.exe --processStart Discord.exe"));
        source.Runs.Add(("SecurityHealth", @"C:\Windows\system32\SecurityHealthSystray.exe"));
        source.Runs.Add(("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent"));
        source.Folder.Add("Spotify.lnk");
        source.Approved[("Run", "Steam")] = false; // already switched off in Task Manager
        return source;
    }

    [Fact]
    public void TheList_ShowsEveryEntry_WithItsState_AndMarksSecurityComponentsProtected()
    {
        var list = new StartupAppsService(Source()).List();

        Assert.Equal(["Discord", "SecurityHealth", "Spotify", "Steam"], list.Select(a => a.Name));
        Assert.True(list.Single(a => a.Name == "Discord").Enabled);               // never changed: Windows starts it
        Assert.False(list.Single(a => a.Name == "Steam").Enabled);                // switched off already
        Assert.True(list.Single(a => a.Name == "SecurityHealth").Protected);
        Assert.False(list.Single(a => a.Name == "Discord").Protected);
    }

    [Fact]
    public void SwitchingAnEntry_WritesOnlyTheApprovedState_AndRefusesProtectedOrUnknownOnes()
    {
        var source = Source();
        var service = new StartupAppsService(source);

        Assert.True(service.SetEnabled("run|Discord", false));
        Assert.Equal([("Run", "Discord", false)], source.Writes);
        Assert.False(service.List().Single(a => a.Name == "Discord").Enabled);

        Assert.True(service.SetEnabled("folder|Spotify.lnk", false));              // a shortcut in the Startup folder: its own area
        Assert.Contains(("StartupFolder", "Spotify.lnk", false), source.Writes);

        Assert.True(service.SetEnabled("run|Discord", true));                      // and back
        Assert.True(service.List().Single(a => a.Name == "Discord").Enabled);

        var before = source.Writes.Count;
        Assert.False(service.SetEnabled("run|SecurityHealth", false));             // a security component
        Assert.False(service.SetEnabled("run|NotThere", false));
        Assert.False(service.SetEnabled(@"run|..\..\evil", false));
        Assert.Equal(before, source.Writes.Count);
    }

    [Fact]
    public void TheOptimizePage_ListsThem_AndASwitchWritesTheState() => sta.RunAsync(async () =>
    {
        var source = Source();
        var previous = LibraryViewModel.StartupSourceFactory;
        LibraryViewModel.StartupSourceFactory = () => source;
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Startup-" + Guid.NewGuid());
            var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
            OptimizeViewModel? page = null;
            vm.OptimizeDialogForTest = p => page = p;

            vm.ShowOptimizeCommand.Execute(null);

            Assert.NotNull(page);
            Assert.True(page!.HasStartup);
            Assert.Equal(4, page.StartupRows.Count);
            Assert.Contains("3 of 4 start", page.StartupSummary);
            var discord = page.StartupRows.Single(r => r.Name == "Discord");
            Assert.True(discord.CanSwitch);
            Assert.False(page.StartupRows.Single(r => r.Name == "SecurityHealth").CanSwitch);

            discord.IsOn = false;

            Assert.Contains(("Run", "Discord", false), source.Writes);
            Assert.Contains("2 of 4 start", page.StartupSummary);

            // A refused switch goes back to where it was and writes nothing.
            var writes = source.Writes.Count;
            var health = page.StartupRows.Single(r => r.Name == "SecurityHealth");
            health.IsOn = false;
            Assert.True(health.IsOn);
            Assert.Equal(writes, source.Writes.Count);
        }
        finally { LibraryViewModel.StartupSourceFactory = previous; }

        await Task.CompletedTask;
    });
}

using System.IO;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using Velopack;

namespace GameLauncher.Tests.ViewModels;

/// <summary>
/// Regression coverage for the "What's New" dialog's lifecycle (LibraryViewModel's constructor +
/// AcknowledgeWhatsNew, backed by PendingUpdateNotesService): the marker must only ever be shown for
/// the version that's actually running (never a version an apply failure left behind), and must
/// survive a crash between being read and being acknowledged, showing again on a later launch rather
/// than being lost.
///
/// Uses the internal SettingsService/PendingUpdateNotesService-injecting constructor to point both at
/// an isolated temp directory rather than the real %AppData%\GameLauncher - same idea as
/// SettingsServiceTests.
/// </summary>
public class LibraryViewModelWhatsNewTests : IDisposable
{
    private readonly string _dataDir;
    private readonly PendingUpdateNotesService _pendingNotesService;

    public LibraryViewModelWhatsNewTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _pendingNotesService = new PendingUpdateNotesService(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private LibraryViewModel CreateViewModel() =>
        new(new SettingsService(_dataDir), _pendingNotesService);

    private static VelopackAsset MakeRelease(string version, string? notes) => new()
    {
        Version = SemanticVersion.Parse(version),
        NotesMarkdown = notes,
    };

    [Fact]
    public void Constructor_NoPendingNotes_DoesNotShowWhatsNew()
    {
        var vm = CreateViewModel();

        Assert.False(vm.ShowWhatsNew);
    }

    [Fact]
    public void Constructor_PendingNotesMatchRunningVersion_ShowsWhatsNew()
    {
        _pendingNotesService.Save(MakeRelease(AppInfo.Version, "- Added the update banner"));

        var vm = CreateViewModel();

        Assert.True(vm.ShowWhatsNew);
        Assert.Equal(AppInfo.Version, vm.WhatsNewVersion);
        Assert.Equal("- Added the update banner", vm.WhatsNewNotes);
    }

    /// <summary>
    /// The bug: a marker version.Equals check that only guards against showing the wrong text isn't
    /// enough - it's the whole "did the update actually take effect" guard. A version mismatch here
    /// means either ApplyUpdatesAndRestart failed right after Save (this launch is still the old
    /// version) or the marker is otherwise stale, and either way it must never resolve to "you're on
    /// vX now."
    /// </summary>
    [Fact]
    public void Constructor_PendingNotesVersionMismatch_DoesNotShowAndDiscardsMarker()
    {
        _pendingNotesService.Save(MakeRelease("999.999.999", "notes for a version we're not running"));

        var vm = CreateViewModel();

        Assert.False(vm.ShowWhatsNew);
        Assert.Null(_pendingNotesService.TryRead()); // discarded, not left to retry against a version that will never match
    }

    [Fact]
    public void Constructor_PendingNotesWithEmptyVersion_DoesNotShowAndDiscardsMarker()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(Path.Combine(_dataDir, "pending-update.json"), """{"Version":"","NotesMarkdown":"notes"}""");

        var vm = CreateViewModel();

        Assert.False(vm.ShowWhatsNew);
        Assert.Null(_pendingNotesService.TryRead());
    }

    [Fact]
    public void Constructor_NoNotesMarkdownProvided_FallsBackToPlaceholderText()
    {
        _pendingNotesService.Save(MakeRelease(AppInfo.Version, notes: null));

        var vm = CreateViewModel();

        Assert.True(vm.ShowWhatsNew);
        Assert.Equal("No release notes were provided for this update.", vm.WhatsNewNotes);
    }

    /// <summary>
    /// The crash-safety guarantee this whole split (TryRead vs Acknowledge) exists for: if the app
    /// never reaches AcknowledgeWhatsNew (crash, forced shutdown, a scan that hangs before the dialog
    /// is shown), the marker must still be there - and still show - on the very next launch.
    /// </summary>
    [Fact]
    public void Constructor_MatchingVersion_DoesNotDeleteMarkerUntilAcknowledged()
    {
        _pendingNotesService.Save(MakeRelease(AppInfo.Version, "notes"));

        var firstLaunch = CreateViewModel();
        Assert.True(firstLaunch.ShowWhatsNew);
        // AcknowledgeWhatsNew deliberately not called here - simulates a crash before the dialog closed.

        var secondLaunch = CreateViewModel();

        Assert.True(secondLaunch.ShowWhatsNew);
        Assert.Equal(AppInfo.Version, secondLaunch.WhatsNewVersion);
    }

    [Fact]
    public void AcknowledgeWhatsNew_DeletesMarkerAndClearsShowWhatsNew()
    {
        _pendingNotesService.Save(MakeRelease(AppInfo.Version, "notes"));
        var vm = CreateViewModel();
        Assert.True(vm.ShowWhatsNew);

        vm.AcknowledgeWhatsNew();

        Assert.False(vm.ShowWhatsNew);
        Assert.Null(_pendingNotesService.TryRead());
    }

    [Fact]
    public void AcknowledgeWhatsNew_ThenRelaunch_DoesNotShowAgain()
    {
        _pendingNotesService.Save(MakeRelease(AppInfo.Version, "notes"));
        var vm = CreateViewModel();
        vm.AcknowledgeWhatsNew();

        var laterLaunch = CreateViewModel();

        Assert.False(laterLaunch.ShowWhatsNew);
    }
}

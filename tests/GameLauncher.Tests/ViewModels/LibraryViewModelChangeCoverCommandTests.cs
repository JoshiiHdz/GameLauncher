using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>
/// Covers the two UI-facing commands (ChangeCoverCommand/ResetCoverCommand) themselves, as opposed to
/// LibraryViewModelArtworkTests' coverage of the underlying Apply/Reset methods they call: the Preview ->
/// Apply/Cancel flow (picking a file only stages a PREVIEW - nothing is written or persisted unless the
/// preview dialog's own Apply button is used), the Id/Name snapshot taken before either dialog opens, and
/// the exception boundary each command wraps its whole body in - CommunityToolkit's generated async
/// commands otherwise let an unhandled exception reach the UI thread's SynchronizationContext, which this
/// app's dispatcher-level handler treats as fatal.
///
/// Uses the same isolated asset-store directory discipline as LibraryViewModelArtworkTests, for the same
/// reason - a commit that actually runs (the Preview-applied tests) writes a real file, and must never do
/// so under the real %AppData%\GameLauncher\CustomCovers.
/// </summary>
public class LibraryViewModelChangeCoverCommandTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _assetStoreDir;
    private readonly LibraryViewModel _sut;
    private readonly List<string> _tempFiles = new();

    public LibraryViewModelChangeCoverCommandTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _assetStoreDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Assets-" + Guid.NewGuid());
        _sut = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir))
        {
            AssetStoreDirOverrideForTest = _assetStoreDir,
            AutomaticCoverArtLookupForTest = (_, _) => null,
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);

        if (Directory.Exists(_assetStoreDir))
            Directory.Delete(_assetStoreDir, recursive: true);

        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static GameEntry MakeGame(string id = "manual-test", string name = "Test Game") => new()
    {
        Id = id,
        Name = name,
        ExecutablePath = @"C:\Games\TestGame\game.exe",
        InstallDir = @"C:\Games\TestGame",
        Source = GameSource.Manual,
    };

    private static byte[] MakePng(int width = 8, int height = 8)
    {
        var pixels = new byte[width * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private string MakeTempImageFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-CoverInput-{Guid.NewGuid()}.png");
        File.WriteAllBytes(path, MakePng());
        _tempFiles.Add(path);
        return path;
    }

    // ---- ChangeCoverCommand: cancellation preserves the existing state --------------------------------

    [Fact]
    public async Task ChangeCoverCommand_PickerCancelled_PreviewNeverShown_NothingChanges()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var previewShown = false;

        _sut.ChangeCoverFilePickerForTest = _ => null; // "Cancel" in the real OpenFileDialog
        _sut.ChangeCoverPreviewDialogForTest = (_, _) => { previewShown = true; return true; };

        await _sut.ChangeCoverCommand.ExecuteAsync(game);

        Assert.False(previewShown);
        Assert.Null(_sut.GetOverride(game.Id));
    }

    [Fact]
    public async Task ChangeCoverCommand_PreviewCancelled_NothingIsStagedOrPersisted()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var imagePath = MakeTempImageFile();
        var assetWritten = false;

        _sut.ChangeCoverFilePickerForTest = _ => imagePath;
        _sut.ChangeCoverPreviewDialogForTest = (_, _) => false; // "Cancel" inside the preview dialog
        _sut.AfterAssetWrittenForTest = _ => assetWritten = true;

        await _sut.ChangeCoverCommand.ExecuteAsync(game);

        Assert.False(assetWritten);
        Assert.Null(_sut.GetOverride(game.Id));
        Assert.Empty(Directory.Exists(_assetStoreDir) ? Directory.GetFiles(_assetStoreDir) : []);
    }

    [Fact]
    public async Task ChangeCoverCommand_PreviewApplied_CommitsTheChange()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var imagePath = MakeTempImageFile();

        _sut.ChangeCoverFilePickerForTest = _ => imagePath;
        _sut.ChangeCoverPreviewDialogForTest = (_, _) => true; // "Apply"

        await _sut.ChangeCoverCommand.ExecuteAsync(game);

        var over = _sut.GetOverride(game.Id);
        Assert.NotNull(over?.Artwork);
        Assert.True(over!.Artwork!.IsUserSelected);
        Assert.Contains("Cover updated for", _sut.StatusText);
        Assert.True(game.IsCoverArt);
    }

    /// <summary>Proves the preview dialog actually receives the decoded/validated image, not just a
    /// signal to go pick one - the whole point of a "preview," and the one thing a stub returning a
    /// bare bool couldn't accidentally satisfy.</summary>
    [Fact]
    public async Task ChangeCoverCommand_PreviewDialogReceivesTheDecodedImage()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var imagePath = MakeTempImageFile();
        BitmapImage? shownImage = null;

        _sut.ChangeCoverFilePickerForTest = _ => imagePath;
        _sut.ChangeCoverPreviewDialogForTest = (_, image) => { shownImage = image; return false; };

        await _sut.ChangeCoverCommand.ExecuteAsync(game);

        Assert.NotNull(shownImage);
        Assert.True(shownImage!.PixelWidth > 0);
    }

    // ---- Exception boundary: neither command may let an exception escape ------------------------------

    [Fact]
    public async Task ChangeCoverCommand_FilePickerThrows_DoesNotThrow_LogsAndReportsStatus_LeavesOverrideUnchanged()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);

        _sut.ChangeCoverFilePickerForTest = _ => throw new IOException("picker COM failure (simulated)");

        var exception = await Record.ExceptionAsync(() => _sut.ChangeCoverCommand.ExecuteAsync(game));

        Assert.Null(exception);
        Assert.Null(_sut.GetOverride(game.Id));
        Assert.Contains("Couldn't change the cover", _sut.StatusText);
    }

    [Fact]
    public async Task ChangeCoverCommand_PreviewDialogThrows_DoesNotThrow_LogsAndReportsStatus_LeavesOverrideUnchanged()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var imagePath = MakeTempImageFile();

        _sut.ChangeCoverFilePickerForTest = _ => imagePath;
        _sut.ChangeCoverPreviewDialogForTest = (_, _) => throw new InvalidOperationException("dialog failure (simulated)");

        var exception = await Record.ExceptionAsync(() => _sut.ChangeCoverCommand.ExecuteAsync(game));

        Assert.Null(exception);
        Assert.Null(_sut.GetOverride(game.Id));
        Assert.Contains("Couldn't change the cover", _sut.StatusText);
    }

    [Fact]
    public async Task ChangeCoverCommand_InvalidImageFile_ReportsStatus_NeverShowsPreview()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var badPath = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-NotAnImage-{Guid.NewGuid()}.png");
        File.WriteAllText(badPath, "not actually a png");
        _tempFiles.Add(badPath);
        var previewShown = false;

        _sut.ChangeCoverFilePickerForTest = _ => badPath;
        _sut.ChangeCoverPreviewDialogForTest = (_, _) => { previewShown = true; return true; };

        await _sut.ChangeCoverCommand.ExecuteAsync(game);

        Assert.False(previewShown);
        Assert.Null(_sut.GetOverride(game.Id));
        Assert.Contains("couldn't be used", _sut.StatusText);
    }

    // ---- Id/Name snapshot: captured before either dialog opens ----------------------------------------

    /// <summary>Both dialogs are synchronous, real-user-facing seams that can take arbitrarily long - a
    /// rescan (which replaces every GameEntry wholesale) or a rename can land while one is open. The
    /// StatusText message must describe the game as it was named when Change Cover was invoked, and the
    /// commit must still land against the correct (still-live) game id afterward.</summary>
    [Fact]
    public async Task ChangeCoverCommand_GameRenamedWhileDialogsAreOpen_StatusTextUsesOriginalName_CommitStillSucceeds()
    {
        var game = MakeGame(id: "game-1", name: "Original Name");
        _sut.SimulateRefreshResult([game]);
        var imagePath = MakeTempImageFile();

        _sut.ChangeCoverFilePickerForTest = _ =>
        {
            game.Name = "Renamed Mid-Flow"; // simulates a live rename while the (real, modal) picker is open
            return imagePath;
        };
        _sut.ChangeCoverPreviewDialogForTest = (_, _) => true;

        await _sut.ChangeCoverCommand.ExecuteAsync(game);

        Assert.Contains("Cover updated for Original Name", _sut.StatusText);
        Assert.DoesNotContain("Renamed Mid-Flow", _sut.StatusText);
        Assert.NotNull(_sut.GetOverride("game-1")?.Artwork);
    }

    /// <summary>A rescan can replace the GameEntry instance entirely (same id, new object) while a
    /// dialog is open. The commit is keyed on the captured id, not the captured GameEntry reference, so
    /// it must still land on whichever GameOverride the id resolves to now - see CommitArtworkChange's
    /// own re-resolution.</summary>
    [Fact]
    public async Task ChangeCoverCommand_GameInstanceReplacedByRescanWhileDialogsAreOpen_CommitStillLandsOnTheLiveInstance()
    {
        var original = MakeGame(id: "game-1", name: "Original Instance");
        _sut.SimulateRefreshResult([original]);
        var imagePath = MakeTempImageFile();
        var replacement = MakeGame(id: "game-1", name: "Original Instance");

        _sut.ChangeCoverFilePickerForTest = _ =>
        {
            _sut.SimulateRefreshResult([replacement]); // a rescan replaced every GameEntry wholesale
            return imagePath;
        };
        _sut.ChangeCoverPreviewDialogForTest = (_, _) => true;

        await _sut.ChangeCoverCommand.ExecuteAsync(original);

        Assert.NotNull(_sut.GetOverride("game-1")?.Artwork);
        Assert.True(replacement.IsCoverArt); // the NEW live instance shows it, not the discarded original
        Assert.False(original.IsCoverArt);
    }

    // ---- ResetCoverCommand: thin wrapper, same exception-boundary/status-mapping discipline -----------

    [Fact]
    public async Task ResetCoverCommand_Success_ReportsStatusText()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var existing = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };
        _sut.SetArtworkForTest(game.Id, existing, revision: 1);

        await _sut.ResetCoverCommand.ExecuteAsync(game);

        Assert.Contains("Cover reset to automatic for", _sut.StatusText);
        Assert.Null(_sut.GetOverride(game.Id)!.Artwork);
    }

    [Fact]
    public async Task ResetCoverCommand_AlreadyInProgress_ReportsStatusText_DoesNotThrow()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var imagePath = MakeTempImageFile();

        // AfterAssetWrittenForTest fires AFTER ApplyValidatedCoverImageCoreAsync's guard is already held
        // (see LibraryViewModel's own remarks) and BEFORE the commit that would release it - blocking
        // here deterministically keeps the guard held for exactly as long as this test needs, without
        // racing the picker/validate steps that run before the guard is even taken. Plain
        // ManualResetEventSlim rendezvous rather than a blocked-on Task, since this callback is invoked
        // synchronously (Action<string>, not awaitable) from a thread-pool continuation - there is no
        // async alternative available inside it.
        using var writeStarted = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        _sut.ChangeCoverFilePickerForTest = _ => imagePath;
        _sut.ChangeCoverPreviewDialogForTest = (_, _) => true;
        _sut.AfterAssetWrittenForTest = _ =>
        {
            writeStarted.Set();
            releaseWrite.Wait();
        };

        var changeCoverTask = _sut.ChangeCoverCommand.ExecuteAsync(game);
        writeStarted.Wait(); // the guard is now definitely held for game.Id

        await _sut.ResetCoverCommand.ExecuteAsync(game); // must be rejected while Change Cover is mid-commit
        Assert.Contains("Already updating", _sut.StatusText);

        releaseWrite.Set();
        await changeCoverTask; // let Change Cover finish so Dispose() doesn't race a background write
    }
}

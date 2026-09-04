using System.IO;
using GameLauncher.Services;
using Velopack;

namespace GameLauncher.Tests.Services;

/// <summary>Uses a fresh temp directory per test, same idea as SettingsServiceTests, so these never
/// touch a developer's or CI runner's real %AppData%\GameLauncher.</summary>
public class PendingUpdateNotesServiceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly PendingUpdateNotesService _sut;

    public PendingUpdateNotesServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _sut = new PendingUpdateNotesService(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static VelopackAsset MakeRelease(string version, string? notes) => new()
    {
        Version = SemanticVersion.Parse(version),
        NotesMarkdown = notes,
    };

    [Fact]
    public void TryRead_NothingSaved_ReturnsNull()
    {
        Assert.Null(_sut.TryRead());
    }

    [Fact]
    public void SaveThenTryRead_RoundTrips()
    {
        _sut.Save(MakeRelease("1.17.1", "- Added the update banner\n- Fixed a bug"));

        var notes = _sut.TryRead();

        Assert.NotNull(notes);
        Assert.Equal("1.17.1", notes!.Version);
        Assert.Equal("- Added the update banner\n- Fixed a bug", notes.NotesMarkdown);
    }

    [Fact]
    public void TryRead_CalledTwice_ReturnsNotesBothTimes()
    {
        // Non-destructive by design - see the class's remarks on why LibraryViewModel must be able to
        // re-read the same marker across a crash/forced-shutdown between reading it and acknowledging
        // it (which is the only thing that actually deletes it).
        _sut.Save(MakeRelease("1.17.1", "notes"));

        var first = _sut.TryRead();
        var second = _sut.TryRead();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Version, second!.Version);
    }

    [Fact]
    public void Acknowledge_DeletesTheMarker_SoALaterReadReturnsNull()
    {
        _sut.Save(MakeRelease("1.17.1", "notes"));
        _sut.TryRead();

        _sut.Acknowledge();

        Assert.Null(_sut.TryRead());
    }

    [Fact]
    public void Discard_DeletesTheMarkerWithoutEverNeedingToReadIt()
    {
        _sut.Save(MakeRelease("1.17.1", "notes"));

        _sut.Discard();

        Assert.Null(_sut.TryRead());
    }

    [Fact]
    public void Discard_NothingSaved_IsANoOp()
    {
        _sut.Discard(); // must not throw
        Assert.Null(_sut.TryRead());
    }

    [Fact]
    public void TryRead_CorruptFile_ReturnsNullAndDeletesIt()
    {
        Directory.CreateDirectory(_dataDir);
        var path = Path.Combine(_dataDir, "pending-update.json");
        File.WriteAllText(path, "{ not valid json");

        var notes = _sut.TryRead();

        Assert.Null(notes);
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// The bug: the JSON literal `null` deserializes successfully (no JsonException) to a null
    /// PendingUpdateNotes - without an explicit check for that, TryRead would return null without ever
    /// discarding the file, and since File.Exists stays true, every future launch would read it again
    /// forever instead of the file ever being cleaned up.
    /// </summary>
    [Fact]
    public void TryRead_FileContainsJsonNull_ReturnsNullAndDeletesIt()
    {
        Directory.CreateDirectory(_dataDir);
        var path = Path.Combine(_dataDir, "pending-update.json");
        File.WriteAllText(path, "null");

        var notes = _sut.TryRead();

        Assert.Null(notes);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Save_NoNotesProvided_TryReadReturnsNullNotesMarkdown()
    {
        _sut.Save(MakeRelease("1.17.1", notes: null));

        var notes = _sut.TryRead();

        Assert.NotNull(notes);
        Assert.Null(notes!.NotesMarkdown);
    }
}

using System.IO;
using System.Text.Json;
using Velopack;

namespace GameLauncher.Services;

public sealed record PendingUpdateNotes(string Version, string? NotesMarkdown);

/// <summary>
/// Carries "what changed in this update" across the restart Velopack performs when applying an
/// update - ApplyUpdatesAndRestart exits this process and starts a fresh one, so nothing held in
/// memory survives that hop. UpdateService writes a marker just before restarting; the next launch
/// (LibraryViewModel's constructor) reads it to drive the "What's New" dialog.
///
/// Deliberately split into a non-destructive TryRead and a separate Acknowledge rather than a single
/// consume-on-read: LibraryViewModel's constructor runs well before the dialog is actually shown (the
/// library scan and everything else in Loaded happens first), so deleting the marker at read time
/// would lose it for good if the app crashed, was force-closed, or the scan hung anywhere in between -
/// the marker would already be gone, but the user never actually saw what changed. Reading
/// non-destructively and only deleting once the caller confirms the dialog was actually shown and
/// closed (LibraryViewModel.AcknowledgeWhatsNew) means a marker survives until it's been genuinely
/// delivered, not just attempted.
/// </summary>
public sealed class PendingUpdateNotesService
{
    private readonly string _filePath;

    public PendingUpdateNotesService() : this(AppPaths.DataDir)
    {
    }

    /// <summary>Lets tests point at an isolated temp directory instead of the real
    /// %AppData%\GameLauncher - production code always uses the parameterless constructor above.</summary>
    public PendingUpdateNotesService(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "pending-update.json");
    }

    public void Save(VelopackAsset release)
    {
        try
        {
            var notes = new PendingUpdateNotes(release.Version.ToString(), release.NotesMarkdown);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(notes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("Couldn't save pending update notes - the 'What's New' dialog will be skipped next launch.", ex);
        }
    }

    /// <summary>Best-effort removal without ever reading the file - used when a just-saved marker
    /// turns out to be wrong and must not survive to mislead a later launch: ApplyUpdatesAndRestart
    /// throwing right after Save (the update never actually took effect, so there is nothing to
    /// announce), or LibraryViewModel finding a marker whose version doesn't match the version that's
    /// actually running (see PendingUpdateNotes.Version's remarks in LibraryViewModel).</summary>
    public void Discard()
    {
        try
        {
            File.Delete(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("Couldn't discard a stale pending-update marker.", ex);
        }
    }

    /// <summary>Reads the pending notes without deleting anything - safe to call speculatively (e.g.
    /// every launch) since it never destroys state a later attempt might still need. Returns null if
    /// nothing was saved, or the marker is corrupt/unreadable/deserializes to nothing meaningful, all
    /// of which also discard the file - a marker that can never become readable (or never contains a
    /// real object even though it's technically valid JSON, e.g. the literal `null`) would otherwise
    /// return null forever without ever being cleaned up, unlike every other failure case here.</summary>
    public PendingUpdateNotes? TryRead()
    {
        if (!File.Exists(_filePath))
            return null;

        try
        {
            var json = File.ReadAllText(_filePath);
            var notes = JsonSerializer.Deserialize<PendingUpdateNotes>(json);
            if (notes is null)
            {
                Discard();
                return null;
            }

            return notes;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Logger.Warn("Couldn't read pending update notes.", ex);
            Discard();
            return null;
        }
    }

    /// <summary>Call once the notes have actually been delivered (the "What's New" dialog was shown
    /// and closed) - deletes the marker so a later, ordinary launch never shows it again. See this
    /// class's remarks for why this is separate from TryRead.</summary>
    public void Acknowledge() => Discard();
}

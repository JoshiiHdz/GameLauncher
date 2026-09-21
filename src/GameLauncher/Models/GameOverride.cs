using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Serialization;

namespace GameLauncher.Models;

/// <summary>Per-game user customization, keyed by GameEntry.Id in AppSettings.Overrides. Id is
/// stable across exe-repick fixes (every scanner derives it from an install dir, registry id, or
/// app id - never from ExecutablePath), unlike ExecutablePath itself, which can change whenever a
/// scanner's "which .exe is the real game" heuristic is corrected. Keying by Id used to be keyed by
/// ExecutablePath, which silently orphaned a game's Favorite/Hidden/DateAdded the moment its picked
/// exe changed - the EA trial-exe fix was a real, confirmed instance of this.</summary>
public sealed class GameOverride
{
    public string? CustomName { get; set; }
    public bool Hidden { get; set; }
    public bool Favorite { get; set; }
    public DateTime? DateAdded { get; set; }

    public ArtworkSelection? Artwork { get; set; }

    /// <summary>Deliberately independent of Artwork being null - living ON Artwork would mean Reset
    /// (which sets Artwork = null) destroys the very counter needed to reject a scan result computed
    /// before the reset happened. Bumped by every Change Cover / Reset / dedup-merge mutation of this
    /// override's artwork state; a scan's own automatic result only gets applied if the live value here
    /// still equals what the scan captured when it started (see LibraryViewModel.ApplyScanResult). long,
    /// not int: this is a monotonic counter with no natural upper bound across a game's whole history of
    /// merges/changes, however unlikely overflow is in practice.</summary>
    public long ArtworkRevision { get; set; }

    /// <summary>WHICH GAME this entry is - decided separately from which image it shows (Artwork above). See
    /// GameIdentityRecord and docs/design/identity-artwork-pipeline.md. Never holds a display name.</summary>
    [JsonConverter(typeof(TolerantIdentityRecordConverter))]
    public GameIdentityRecord? Identity { get; set; }

    /// <summary>Advances whenever the ACTIVE identity set changes, from any origin (user, merge or automatic).
    /// Guards the premise an identity dialog displayed ("current identity: X").</summary>
    [JsonConverter(typeof(TolerantLongConverter))]
    public long IdentityRevision { get; set; }

    /// <summary>Advances on every meaningful change to the USER's own identity decisions (Confirm/Reject/Clear),
    /// whether or not the active identity changed - never on an automatic write. Guards the user's decisions
    /// against a stale dialog (design 6.1).</summary>
    [JsonConverter(typeof(TolerantLongConverter))]
    public long DecisionRevision { get; set; }

    /// <summary>Forward compatibility: fields a later version adds survive an intermediate version's save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

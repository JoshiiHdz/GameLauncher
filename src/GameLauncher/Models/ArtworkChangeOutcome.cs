namespace GameLauncher.Models;

/// <summary>Result of a Change Cover / Reset-to-Automatic attempt - see LibraryViewModel.
/// CommitArtworkChange for what produces each value.</summary>
public enum ArtworkChangeOutcome
{
    Success,
    AlreadyInProgress,
    InvalidImage,
    GameNoLongerExists,
    SaveFailed,

    /// <summary>The image passed validation, but writing it into ArtworkAssetStore failed (disk full,
    /// permissions, ...) - distinct from SaveFailed (settings.json itself failed to write): here nothing
    /// was ever staged, so there is nothing to roll back and the previous selection is simply left
    /// untouched.</summary>
    StorageFailed,

    /// <summary>This game's ArtworkRevision counter is exhausted (at long.MaxValue) - rejected before
    /// touching the override at all, never by reusing a value that's already been handed out. See
    /// LibraryViewModel.TryGetNextRevision. Astronomically unreachable through real usage (it would take
    /// long.MaxValue prior changes to this exact game), but a defined, safe terminal state rather than a
    /// silent correctness gap.</summary>
    RevisionExhausted,

    /// <summary>The cover changed (a newer Change Cover/Reset, or a merge) while the user was choosing: nothing was applied
    /// and the UI should revalidate instead of silently overwriting it (design 6.7, D3).</summary>
    StaleSelection,
}

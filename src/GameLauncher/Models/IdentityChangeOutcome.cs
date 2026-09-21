namespace GameLauncher.Models;

/// <summary>Result of a user identity operation (Confirm / Reject / Clear) - see LibraryViewModel.CommitIdentityChange.</summary>
public enum IdentityChangeOutcome
{
    Success,

    /// <summary>The operation was meaningless (re-confirming the same identity, rejecting an already-rejected candidate,
    /// clearing when nothing is set): nothing changed, no counter advanced, nothing saved.</summary>
    NoChange,

    /// <summary>The dialog was opened against an older state: the user's own decisions (DecisionRevision) or the identity the
    /// dialog displayed (IdentityRevision) changed since. Nothing was applied; the UI should tell the user what changed.</summary>
    StaleSelection,

    GameNoLongerExists,
    SaveFailed,

    /// <summary>A revision counter is exhausted (long.MaxValue): rejected before anything was mutated.</summary>
    RevisionExhausted,

    /// <summary>Not allowed in the current state (rejecting the confirmed identity - that is Clear or Change; anything but
    /// Clear on a quarantined record).</summary>
    InvalidOperation,

    AlreadyInProgress,
}

namespace GameLauncher.Services.SessionTracking;

/// <summary>
/// Thread-safe holder for the latest immutable snapshot of process ids GameSessionWatcher is currently
/// watching - published (via Publish) each time its own watched batch changes (initial discovery, and
/// every handoff), and read (via Current) on GameWindowTracker's own independent polling cadence through
/// GameWindowTrackerOptions.WindowPollInterval. The volatile field is enough on its own: each published
/// value is a brand-new HashSet instance that is never mutated after being handed to Publish (mirroring
/// GameWindowTracker's own getTrackedProcessIds contract), so a reader can never observe a collection
/// that's being concurrently modified - only ever a whole, consistent snapshot from some point in time.
/// </summary>
internal sealed class ProcessIdSnapshotPublisher
{
    private volatile IReadOnlySet<int> _current = new HashSet<int>();

    public void Publish(IReadOnlySet<int> ids) => _current = ids;

    public IReadOnlySet<int> Current => _current;
}

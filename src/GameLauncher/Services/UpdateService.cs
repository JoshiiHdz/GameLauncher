using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace GameLauncher.Services;

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    NotInstalled,
    CheckFailed,
}

/// <summary>Update is non-null only when Status is UpdateAvailable.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null);

/// <summary>
/// Checks for and applies updates via Velopack, reading releases straight from this repo's GitHub
/// Releases (no separate update-hosting infrastructure needed). Every method here is defensive by
/// design, the same way SteamGridDbCoverArtProvider treats its own remote calls: a failure to reach
/// GitHub, a rate limit, or anything else network-related must never surface as an error the user has
/// to deal with - it should just mean "no update available right now."
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/JoshiiHdz/GameLauncher";

    private readonly PendingUpdateNotesService _pendingNotes = new();
    private UpdateManager? _manager;

    // Constructed lazily, on first actual use, rather than as a field initializer: UpdateManager's
    // constructor itself throws (InvalidOperationException, "No VelopackLocator has been set") unless
    // VelopackApp.Build().Run() already ran earlier in *this process* (App.xaml.cs's Main does this in
    // production) - a field initializer would let that escape straight out of UpdateService's own
    // constructor, before CheckForUpdateAsync's try/catch ever gets a chance to catch it, breaking the
    // "never throws" contract below. Deferring construction to here means every failure - including
    // this one - is caught in the same place. Discovered via LibraryViewModelRunningGameTests, which
    // constructs a real UpdateService outside any Velopack-bootstrapped process.
    private UpdateManager GetManager() =>
        _manager ??= new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    /// <summary>Never throws - callers (including a fire-and-forget startup check) can trust this
    /// always resolves to a result instead of an unobserved exception. The result distinguishes "no
    /// update" from "couldn't check" from "not a real install" so the UI can say something accurate
    /// rather than a blanket "you're up to date" for all three.</summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        Logger.Info("Checking for updates...");
        try
        {
            var update = await GetManager().CheckForUpdatesAsync();
            if (update is null)
            {
                Logger.Info("No update available - already on the latest version.");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
            }

            Logger.Info($"Update available: v{update.TargetFullRelease.Version}.");
            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, update);
        }
        catch (NotInstalledException)
        {
            // Thrown by CheckForUpdatesAsync's internal EnsureInstalled() whenever the app isn't
            // running from a Velopack-managed install - which is every local dev/debug run (F5,
            // dotnet run, launching straight from bin\Debug\...), not a real failure. Logged at Info,
            // not Warn: this is the routine, expected case for anyone working on the code, not
            // something worth flagging as a problem.
            Logger.Info("Skipping update check - not running from an installed copy.");
            return new UpdateCheckResult(UpdateCheckStatus.NotInstalled);
        }
        catch (Exception ex)
        {
            // Deliberately catches everything, not a curated list of network exception types: this
            // method promises never to throw (the startup check awaits it from a fire-and-forget
            // Task - an escaped exception there would only ever surface as a generic "unobserved task
            // exception" log line, well after the fact and with no way to react to it). A corrupt
            // feed, a Velopack-internal error, or anything else unexpected all belong here rather
            // than risk missing one and leaving this guarantee false.
            Logger.Warn("Update check failed.", ex);
            return new UpdateCheckResult(UpdateCheckStatus.CheckFailed);
        }
    }

    /// <summary>Downloads the update, then exits this process, applies it, and relaunches - Velopack
    /// owns that whole sequence, so nothing runs after this call returns on success. Throws on
    /// failure (download error, checksum mismatch, disk full, ...) rather than swallowing it: unlike
    /// a background check, this runs from a user's explicit "Update Now" click, so the caller needs
    /// to know it failed and tell them, not silently do nothing.</summary>
    public async Task DownloadAndApplyAsync(UpdateInfo update, IProgress<int>? progress = null)
    {
        Logger.Info($"Downloading update v{update.TargetFullRelease.Version}...");
        await GetManager().DownloadUpdatesAsync(update, p => progress?.Report(p));

        // Written before the restart below, not after: ApplyUpdatesAndRestart exits this process, so
        // there is no "after" - anything the next launch needs to show a "What's New" dialog has to
        // already be on disk by the time that call is made.
        _pendingNotes.Save(update.TargetFullRelease);

        Logger.Info("Update downloaded - applying and restarting.");
        try
        {
            GetManager().ApplyUpdatesAndRestart(update);
        }
        catch
        {
            // The update never actually took effect - this process is still running the OLD version,
            // and will keep running it (the caller surfaces this failure to the user rather than
            // retrying). Without removing the marker here, that old version's *next* ordinary launch
            // would find it and falsely announce "Updated to vX" for an update that never happened.
            _pendingNotes.Discard();
            throw;
        }
    }
}

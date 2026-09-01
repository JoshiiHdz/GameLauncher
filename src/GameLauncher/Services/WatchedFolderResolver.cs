using System.IO;
using GameLauncher.Models;

namespace GameLauncher.Services;

public static class WatchedFolderResolver
{
    /// <summary>
    /// Ensures wf.Path points at a folder that currently exists. If the stored path is gone but the
    /// folder has a volume anchor, searches currently-connected drives for a matching volume serial
    /// and re-derives the path from it (a plugged-in external drive that came back under a different
    /// letter) - if found, wf.Path is updated in place so the caller can persist the healed path.
    /// Returns false only when the folder truly can't be found anywhere right now.
    /// </summary>
    public static bool TryResolve(WatchedFolder wf)
    {
        if (Directory.Exists(wf.Path))
        {
            CaptureAnchor(wf); // backfill for folders added before the anchor feature existed
            return true;
        }

        if (wf.VolumeSerialNumber is null || wf.RelativePath is null)
            return false;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;

            if (VolumeInfo.GetSerialNumber(drive.RootDirectory.FullName) != wf.VolumeSerialNumber)
                continue;

            var candidate = Path.Combine(drive.RootDirectory.FullName, wf.RelativePath);
            if (!Directory.Exists(candidate))
                continue;

            wf.Path = candidate;
            return true;
        }

        return false;
    }

    /// <summary>Captures a volume anchor for a folder so a future drive-letter change can self-heal.
    /// No-op if one is already captured or the drive's serial number can't be read.</summary>
    public static void CaptureAnchor(WatchedFolder wf)
    {
        if (wf.VolumeSerialNumber is not null)
            return;

        var root = Path.GetPathRoot(wf.Path);
        if (string.IsNullOrEmpty(root))
            return;

        var serial = VolumeInfo.GetSerialNumber(root);
        if (serial is null)
            return;

        wf.VolumeSerialNumber = serial;
        wf.RelativePath = Path.GetRelativePath(root, wf.Path);
    }
}

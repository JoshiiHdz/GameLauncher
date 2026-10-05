using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GameLauncher.Services;

/// <summary>Small helpers every scanner needs: a game's stable id from its folder, and the drives worth looking at.</summary>
internal static class InstallPaths
{
    /// <summary>A hash of a path that stays the same between runs (string.GetHashCode is randomized per process, which would break
    /// anything keyed by it: cached icons, per-game overrides). Case-insensitive, like Windows paths.</summary>
    public static string StableHash(string path) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())));

    /// <summary>The root of every fixed or removable drive that is ready to be read (<c>C:\</c>, <c>D:\</c>, ...). A drive that errors while
    /// being asked is skipped, never fatal.</summary>
    public static IEnumerable<string> ReadyDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            var ready = false;
            try
            {
                ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
            }
            catch (IOException)
            {
            }

            if (ready)
                yield return drive.RootDirectory.FullName;
        }
    }
}

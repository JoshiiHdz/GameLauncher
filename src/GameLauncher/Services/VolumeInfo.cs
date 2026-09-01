using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLauncher.Services;

public static class VolumeInfo
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);

    /// <summary>Returns the volume's serial number (stable across drive-letter reassignment,
    /// changes only on reformat), or null if it can't be determined (drive not ready, network
    /// share, etc.) - callers should treat null as "no anchor available", not an error.</summary>
    public static uint? GetSerialNumber(string driveRoot)
    {
        try
        {
            if (!driveRoot.EndsWith(Path.DirectorySeparatorChar))
                driveRoot += Path.DirectorySeparatorChar;

            return GetVolumeInformation(driveRoot, null, 0, out var serial, out _, out _, null, 0)
                ? serial
                : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }
}

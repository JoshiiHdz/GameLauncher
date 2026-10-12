using System.Diagnostics;
using System.IO;

namespace GameLauncher.Services;

/// <summary>The previous Windows installation (C:\Windows.old) that a Windows upgrade leaves behind - often 10 to 30 GB. It is not deleted from here: its files are owned by
/// the system, and Windows' own Storage settings remove it safely (and Windows removes it itself about ten days after an upgrade). The launcher shows how big it is and takes
/// the person to the right place.</summary>
public static class WindowsOld
{
    public static string Folder => Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Windows.old");

    /// <summary>The size of the folder in bytes (what could be read of it), or null when there is no such folder.</summary>
    public static long? Measure(CancellationToken ct = default)
    {
        var root = Folder;
        if (!Directory.Exists(root))
            return null;

        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = pending.Pop().EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // protected by the system: counted as far as it can be read
            }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue; // a link is never followed

                if (entry is DirectoryInfo directory)
                    pending.Push(directory);
                else if (entry is FileInfo file)
                {
                    try { total += file.Length; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }

        return total;
    }

    /// <summary>Opens Windows' own Storage settings, where "Temporary files" lists "Previous Windows installation(s)" with a remove button.</summary>
    public static void OpenWindowsCleanup()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Warn("Couldn't open Windows' storage settings.", ex);
        }
    }
}

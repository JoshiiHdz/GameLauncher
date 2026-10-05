using System.IO;
using System.Runtime.InteropServices;

namespace GameLauncher.Services;

public sealed record ScanOutcome(long Bytes, int Files);

public sealed record CleanOutcome(long FreedBytes, int Deleted, int Skipped);

/// <summary>One kind of leftover the Optimize page can clear. Scan only measures; Clean deletes - and only inside the category's own
/// folders, never anything the user made.</summary>
public sealed class CleanCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Ticked when the page opens. Only things nobody would miss: caches that rebuild, and temp files.</summary>
    public bool SelectedByDefault { get; init; }

    public required Func<CancellationToken, ScanOutcome> Scan { get; init; }
    public required Func<CancellationToken, CleanOutcome> Clean { get; init; }

    /// <summary>Like <see cref="Clean"/>, but calls the callback with the bytes of each file as it goes, so a progress bar can move while
    /// a big folder is cleared. Null for a category that finishes in one step (the Recycle Bin).</summary>
    public Func<CancellationToken, Action<long>?, CleanOutcome>? CleanReporting { get; init; }
}

public static class StorageCleaner
{
    /// <summary>Clears folders of files older than <paramref name="minAge"/>. Safety rules, all of them tested:
    /// links/junctions are never followed or deleted (a link in a temp folder can point anywhere); the folder itself is never removed;
    /// a file in use, locked or refused is skipped and counted, not forced; nothing outside <paramref name="roots"/> is touched.</summary>
    public static CleanCategory Directories(string id, string name, string description, IReadOnlyList<string> roots, TimeSpan minAge,
        bool selectedByDefault = false, Func<DateTime>? utcNow = null)
    {
        var clock = utcNow ?? (() => DateTime.UtcNow);
        return new CleanCategory
        {
            Id = id, Name = name, Description = description, SelectedByDefault = selectedByDefault,
            Scan = ct => ScanFolders(roots, minAge, clock(), ct),
            Clean = ct => CleanFolders(roots, minAge, clock(), ct, null),
            CleanReporting = (ct, report) => CleanFolders(roots, minAge, clock(), ct, report),
        };
    }

    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>Every file under the root that is old enough, without crossing a link.</summary>
    private static IEnumerable<FileInfo> OldFiles(string root, TimeSpan minAge, DateTime nowUtc, CancellationToken ct)
    {
        if (!Directory.Exists(root))
            yield break;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (IsLink(entry))
                    continue;

                if (entry is DirectoryInfo sub)
                    pending.Push(sub);
                else if (entry is FileInfo file && nowUtc - file.LastWriteTimeUtc >= minAge)
                    yield return file;
            }
        }
    }

    private static ScanOutcome ScanFolders(IReadOnlyList<string> roots, TimeSpan minAge, DateTime nowUtc, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;
        foreach (var root in roots)
        {
            foreach (var file in OldFiles(root, minAge, nowUtc, ct))
            {
                try
                {
                    bytes += file.Length;
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // vanished between listing and measuring
                }
            }
        }

        return new ScanOutcome(bytes, files);
    }

    private static CleanOutcome CleanFolders(IReadOnlyList<string> roots, TimeSpan minAge, DateTime nowUtc, CancellationToken ct,
        Action<long>? report)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (var root in roots)
        {
            // Folder ages are read BEFORE any file goes: deleting a file bumps its folder's own modified time to "now".
            var folders = ListFolders(root);
            foreach (var file in OldFiles(root, minAge, nowUtc, ct).ToList())
            {
                try
                {
                    var length = file.Length;
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                        file.Attributes &= ~FileAttributes.ReadOnly;
                    file.Delete();
                    freed += length;
                    deleted++;
                    report?.Invoke(length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++; // in use or protected: left alone
                }
            }

            RemoveEmptyFolders(folders, minAge, nowUtc);
        }

        return new CleanOutcome(freed, deleted, skipped);
    }

    /// <summary>Every folder under the root with its modified time, without crossing a link. Walked by hand: the framework's recursive
    /// listing would follow a junction into somewhere that is not ours.</summary>
    private static List<(DirectoryInfo Folder, DateTime LastWriteUtc)> ListFolders(string root)
    {
        var folders = new List<(DirectoryInfo, DateTime)>();
        if (!Directory.Exists(root))
            return folders;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            try
            {
                foreach (var child in pending.Pop().EnumerateDirectories())
                {
                    if (IsLink(child))
                        continue;

                    folders.Add((child, child.LastWriteTimeUtc));
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable: leave it
            }
        }

        return folders;
    }

    /// <summary>Tidies up the folders the deleted files leave behind - never the root, never a link, never a folder with anything in
    /// it, and never one newer than the age limit (a running app may have just made it).</summary>
    private static void RemoveEmptyFolders(List<(DirectoryInfo Folder, DateTime LastWriteUtc)> folders, TimeSpan minAge, DateTime nowUtc)
    {
        foreach (var (sub, lastWrite) in folders.Where(f => nowUtc - f.LastWriteUtc >= minAge).OrderByDescending(f => f.Folder.FullName.Length))
        {
            try
            {
                sub.Refresh();
                if (sub.Exists && !sub.EnumerateFileSystemInfos().Any())
                    sub.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use: leave it
            }
        }
    }

    // ---- recycle bin -------------------------------------------------------------------------------

    // Natural alignment (24 bytes): Windows checks cbSize against it and answers E_INVALIDARG for anything else, which once made every
    // real query report an empty bin.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShQueryRbInfo
    {
        public int Size;
        public long SizeInBytes;
        public long NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref ShQueryRbInfo info);

    /// <summary>The real shell query for every drive's bin: the call's HRESULT (0 = success), then the size and item count.</summary>
    internal static (int Result, long Bytes, long Items) QueryRecycleBinNative()
    {
        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        var result = SHQueryRecycleBinW(null, ref info);
        return (result, info.SizeInBytes, info.NumItems);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr window, string? rootPath, uint flags);

    private const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;

    /// <summary>The Recycle Bin, through Windows' own shell calls. <paramref name="query"/> and <paramref name="empty"/> are the real
    /// calls unless a test supplies its own.</summary>
    public static CleanCategory RecycleBin(Func<(long Bytes, long Items)>? query = null, Func<bool>? empty = null)
    {
        query ??= () =>
        {
            var (result, bytes, items) = QueryRecycleBinNative();
            if (result == 0)
                return (bytes, items);
            Logger.Warn($"Optimize: the Recycle Bin could not be measured (HRESULT 0x{result:X8}).");
            return (0, 0);
        };
        empty ??= () => SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound) == 0;

        return new CleanCategory
        {
            Id = "recycle-bin",
            Name = "Recycle Bin",
            Description = "Everything you have deleted and not yet restored. This one cannot be undone.",
            SelectedByDefault = false,
            Scan = _ =>
            {
                var (bytes, items) = query();
                return new ScanOutcome(bytes, (int)Math.Min(int.MaxValue, items));
            },
            Clean = _ =>
            {
                var (bytes, items) = query();
                return empty() ? new CleanOutcome(bytes, (int)Math.Min(int.MaxValue, items), 0) : new CleanOutcome(0, 0, (int)Math.Min(int.MaxValue, items));
            },
        };
    }

    // ---- the real list -----------------------------------------------------------------------------

    public static IReadOnlyList<CleanCategory> DefaultCategories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var oneDay = TimeSpan.FromHours(24);

        return
        [
            Directories("user-temp", "Temporary files",
                "Leftovers in your temp folder. Only files untouched for a day are removed, so a running installer is never disturbed.",
                [Path.GetTempPath()], oneDay, selectedByDefault: true),
            Directories("windows-temp", "Windows temporary files",
                "The system temp folder. Some files need administrator rights and are skipped if they can't be removed.",
                [Path.Combine(windows, "Temp")], oneDay),
            Directories("crash-dumps", "Crash dumps and error reports",
                "Memory dumps and reports left behind by crashed apps and games.",
                [Path.Combine(local, "CrashDumps"), Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"),
                 Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue")], TimeSpan.Zero, selectedByDefault: true),
            Directories("shader-cache", "Graphics shader caches",
                "DirectX, NVIDIA and AMD shader caches. They rebuild on their own - the first launch of each game may stutter once while they do.",
                [Path.Combine(local, "D3DSCache"), Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache"),
                 Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "AMD", "GLCache")], TimeSpan.Zero),
            RecycleBin(),
        ];
    }
}

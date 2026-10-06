using System.IO;
using System.Runtime.InteropServices;

namespace GameLauncher.Services;

public sealed record ScanOutcome(long Bytes, int Files);

public sealed record CleanOutcome(long FreedBytes, int Deleted, int Skipped);

/// <summary>A folder a category clears. <see cref="Path"/> may have a wildcard in a folder name ("User Data\*\Cache" is every browser profile's cache); with
/// <see cref="Recursive"/> false only the files directly inside it are looked at.</summary>
public sealed record CleanRoot(string Path, bool Recursive = true)
{
    public static implicit operator CleanRoot(string path) => new(path);
}

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
        bool selectedByDefault = false, Func<DateTime>? utcNow = null, IReadOnlyList<string>? filePatterns = null) =>
        Directories(id, name, description, () => roots.Select(r => (CleanRoot)r), minAge, selectedByDefault, utcNow, filePatterns);

    /// <summary>The general form: the roots are worked out each time the category is scanned or cleared (a Steam installed since the app started is
    /// found), may carry wildcards and may be top-level only; <paramref name="filePatterns"/> ("*.dmp") limits it to files with those names. Every root
    /// is checked by <see cref="IsSafeRoot"/> first, so a mistake in a list can never point the cleaner at a drive or a system folder.</summary>
    public static CleanCategory Directories(string id, string name, string description, Func<IEnumerable<CleanRoot>> roots, TimeSpan minAge,
        bool selectedByDefault = false, Func<DateTime>? utcNow = null, IReadOnlyList<string>? filePatterns = null)
    {
        var clock = utcNow ?? (() => DateTime.UtcNow);
        return new CleanCategory
        {
            Id = id, Name = name, Description = description, SelectedByDefault = selectedByDefault,
            Scan = ct => ScanFolders(Resolve(roots(), filePatterns), minAge, clock(), ct, filePatterns),
            Clean = ct => CleanFolders(Resolve(roots(), filePatterns), minAge, clock(), ct, null, filePatterns),
            CleanReporting = (ct, report) => CleanFolders(Resolve(roots(), filePatterns), minAge, clock(), ct, report, filePatterns),
        };
    }

    // ---- which folders are ever allowed to be cleared -----------------------------------------------

    /// <summary>The folders no category may use as a root, however it is spelled: the profile, the Windows and program folders, the data folders and the
    /// libraries. Their contents are not leftovers, and one wrong wildcard must not be able to reach them.</summary>
    private static IEnumerable<string> ForbiddenRoots()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                     Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
                     Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.CommonProgramFiles,
                 })
        {
            if (Environment.GetFolderPath(folder) is { Length: > 0 } path)
                yield return path.TrimEnd('\\');
        }
    }

    /// <summary>True for a real folder path that is not a drive root, a link, or one of the folders in <see cref="ForbiddenRoots"/>. A forbidden folder is
    /// allowed only as a top-level-only root that picks files by name (Windows' own dump file sits directly in the Windows folder): then nothing inside
    /// it is ever entered and nothing is touched but the named files.</summary>
    internal static bool IsSafeRoot(string path, bool recursive = true, bool hasFilePatterns = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('*') || path.Contains('?'))
                return false;

            var full = Path.GetFullPath(path).TrimEnd('\\');
            if (Path.GetPathRoot(full)?.TrimEnd('\\') is { } root && string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
                return false; // a drive

            if ((recursive || !hasFilePatterns) && ForbiddenRoots().Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)))
                return false;

            return !Directory.Exists(full) || !IsLink(new DirectoryInfo(full));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Turns wildcards in folder names into the folders that exist, and drops every root that is not safe.</summary>
    internal static List<CleanRoot> Resolve(IEnumerable<CleanRoot> roots, IReadOnlyList<string>? filePatterns = null)
    {
        var resolved = new List<CleanRoot>();
        foreach (var root in roots)
        {
            foreach (var path in ExpandRoot(root.Path))
            {
                if (IsSafeRoot(path, root.Recursive, filePatterns is { Count: > 0 }) && !resolved.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
                    resolved.Add(root with { Path = path });
            }
        }

        return resolved;
    }

    /// <summary>"C:\Users\x\AppData\Local\Google\Chrome\User Data\*\Cache" becomes the Cache folder of each profile that has one. Only folder names may carry
    /// a wildcard, and a link is never entered.</summary>
    internal static IEnumerable<string> ExpandRoot(string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            yield return pattern;
            yield break;
        }

        string? start;
        try
        {
            start = Path.GetPathRoot(pattern);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        if (string.IsNullOrEmpty(start))
            yield break;

        var current = new List<string> { start };
        foreach (var segment in pattern[start.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = new List<string>();
            foreach (var folder in current)
            {
                if (!segment.Contains('*') && !segment.Contains('?'))
                {
                    var joined = Path.Combine(folder, segment);
                    if (Directory.Exists(joined))
                        next.Add(joined);

                    continue;
                }

                try
                {
                    foreach (var match in Directory.EnumerateDirectories(folder, segment))
                    {
                        if (!IsLink(new DirectoryInfo(match)))
                            next.Add(match);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // missing or unreadable: nothing there to clear
                }
            }

            current = next;
        }

        foreach (var path in current)
            yield return path;
    }

    private static bool Matches(FileInfo file, IReadOnlyList<string>? patterns) =>
        patterns is null || patterns.Count == 0 || patterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, file.Name, ignoreCase: true));

    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>Every file under the root that is old enough, without crossing a link.</summary>
    private static IEnumerable<FileInfo> OldFiles(CleanRoot root, TimeSpan minAge, DateTime nowUtc, CancellationToken ct, IReadOnlyList<string>? patterns)
    {
        if (!Directory.Exists(root.Path))
            yield break;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root.Path));
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
                {
                    if (root.Recursive)
                        pending.Push(sub);
                }
                else if (entry is FileInfo file && nowUtc - file.LastWriteTimeUtc >= minAge && Matches(file, patterns))
                {
                    yield return file;
                }
            }
        }
    }

    private static ScanOutcome ScanFolders(IReadOnlyList<CleanRoot> roots, TimeSpan minAge, DateTime nowUtc, CancellationToken ct, IReadOnlyList<string>? patterns)
    {
        long bytes = 0;
        var files = 0;
        foreach (var root in roots)
        {
            foreach (var file in OldFiles(root, minAge, nowUtc, ct, patterns))
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

    private static CleanOutcome CleanFolders(IReadOnlyList<CleanRoot> roots, TimeSpan minAge, DateTime nowUtc, CancellationToken ct,
        Action<long>? report, IReadOnlyList<string>? patterns)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (var root in roots)
        {
            // Folder ages are read BEFORE any file goes: deleting a file bumps its folder's own modified time to "now".
            // A category that picks files by name (or looks only at the top of a folder) does not tidy folders: an empty folder it did not empty is not its to remove.
            var tidy = root.Recursive && patterns is null or { Count: 0 };
            var folders = tidy ? ListFolders(root.Path) : [];
            foreach (var file in OldFiles(root, minAge, nowUtc, ct, patterns).ToList())
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

            if (tidy)
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

    /// <summary>The kinds of leftover the Optimize page clears. Each is only ever caches, logs, temp files and installers that something else rebuilds or
    /// re-downloads - never settings, saves, logins, history or anything a person made. Only the first and third are ticked when the page opens.</summary>
    public static IReadOnlyList<CleanCategory> DefaultCategories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windows) ?? @"C:\";
        var oneDay = TimeSpan.FromHours(24);
        var oneWeek = TimeSpan.FromDays(7);

        // The same three caches in every Chromium browser's profile folders ("Default", "Profile 1", ...), plus its shader caches.
        IEnumerable<CleanRoot> Chromium(string userData) =>
        [
            Path.Combine(userData, "*", "Cache"), Path.Combine(userData, "*", "Code Cache"), Path.Combine(userData, "*", "GPUCache"),
            Path.Combine(userData, "*", "DawnGraphiteCache"), Path.Combine(userData, "*", "DawnWebGPUCache"),
            Path.Combine(userData, "ShaderCache"), Path.Combine(userData, "GrShaderCache"), Path.Combine(userData, "GraphiteDawnCache"),
        ];

        IEnumerable<CleanRoot> BrowserCaches() =>
        [
            .. Chromium(Path.Combine(local, "Google", "Chrome", "User Data")),
            .. Chromium(Path.Combine(local, "Microsoft", "Edge", "User Data")),
            .. Chromium(Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            .. Chromium(Path.Combine(local, "Vivaldi", "User Data")),
            .. Chromium(Path.Combine(local, "Chromium", "User Data")),
            .. new[] { "Opera Stable", "Opera GX Stable" }.SelectMany(opera => new CleanRoot[]
            {
                Path.Combine(roaming, "Opera Software", opera, "Cache"), Path.Combine(roaming, "Opera Software", opera, "Code Cache"),
                Path.Combine(roaming, "Opera Software", opera, "GPUCache"),
            }),
            .. new CleanRoot[]
            {
                Path.Combine(local, "Mozilla", "Firefox", "Profiles", "*", "cache2"), Path.Combine(local, "Mozilla", "Firefox", "Profiles", "*", "startupCache"),
                Path.Combine(local, "Mozilla", "Firefox", "Profiles", "*", "jumpListCache"),
            },
        ];

        IEnumerable<CleanRoot> LauncherCaches()
        {
            var roots = new List<CleanRoot>
            {
                Path.Combine(local, "Steam", "htmlcache"),
                Path.Combine(local, "EpicGamesLauncher", "Saved", "webcache*"), Path.Combine(local, "EpicGamesLauncher", "Saved", "Logs"),
                Path.Combine(local, "EpicGamesLauncher", "Saved", "Crashes"),
                Path.Combine(local, "Ubisoft Game Launcher", "cache"), Path.Combine(local, "Ubisoft Game Launcher", "logs"),
            };
            foreach (var discord in new[] { "discord", "discordptb", "discordcanary" })
            {
                roots.Add(Path.Combine(roaming, discord, "Cache"));
                roots.Add(Path.Combine(roaming, discord, "Code Cache"));
                roots.Add(Path.Combine(roaming, discord, "GPUCache"));
            }

            // Steam's own logs and crash dumps, in its folder wherever that is.
            if (SteamScanner.FindSteamFolders() is { Count: > 0 } steam)
            {
                roots.Add(Path.Combine(steam[0], "logs"));
                roots.Add(Path.Combine(steam[0], "dumps"));
            }

            return roots;
        }

        IEnumerable<CleanRoot> ShaderCaches()
        {
            var roots = new List<CleanRoot>
            {
                Path.Combine(local, "D3DSCache"), Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache"),
                Path.Combine(local, "NVIDIA", "ComputeCache"), Path.Combine(programData, "NVIDIA Corporation", "NV_Cache"),
                Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "AMD", "GLCache"), Path.Combine(local, "AMD", "VkCache"),
                Path.Combine(local, "Intel", "ShaderCache"),
            };

            // Steam keeps compiled shaders for each game beside the game, in every library folder; Steam fetches or rebuilds them.
            foreach (var library in SteamScanner.FindSteamFolders())
                roots.Add(Path.Combine(library, "steamapps", "shadercache"));

            return roots;
        }

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
                 Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"), Path.Combine(local, "Microsoft", "Windows", "WER", "Temp"),
                 Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"), Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
                 Path.Combine(programData, "Microsoft", "Windows", "WER", "Temp")], TimeSpan.Zero, selectedByDefault: true),
            Directories("shader-cache", "Graphics shader caches",
                "DirectX, NVIDIA, AMD, Intel and Steam shader caches. They rebuild on their own - the first launch of each game may stutter once while they do.",
                ShaderCaches, TimeSpan.Zero),
            Directories("browser-caches", "Browser caches",
                "Cached pages and images in Edge, Chrome, Firefox, Brave, Opera and Vivaldi. Passwords, history, cookies and logins are never touched. Pages load a little slower once while the cache refills; close the browser first for the best result.",
                BrowserCaches, TimeSpan.Zero),
            Directories("launcher-caches", "Game launcher caches and logs",
                "Web caches, logs and crash files kept by Steam, Epic Games, Ubisoft Connect and Discord. They rebuild themselves; your games, saves and logins are untouched. Close the launcher first for the best result.",
                LauncherCaches, TimeSpan.Zero),
            Directories("driver-installers", "Old graphics driver installers",
                "Driver files left behind after installing NVIDIA or AMD drivers (the C:\\NVIDIA and C:\\AMD folders and the GeForce download cache). Only files a week old or more; the driver already installed is not affected.",
                [Path.Combine(systemDrive, "NVIDIA"), Path.Combine(systemDrive, "AMD"), Path.Combine(programData, "NVIDIA Corporation", "Downloader")],
                oneWeek),
            Directories("windows-update-cache", "Windows Update downloads",
                "Update files Windows has already installed and its Delivery Optimization cache. Windows downloads again only if it needs them. Needs administrator rights; skipped without.",
                [Path.Combine(windows, "SoftwareDistribution", "Download"),
                 Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")],
                oneWeek),
            Directories("windows-logs", "Old Windows log files",
                "Servicing, repair and update logs more than a week old. Needs administrator rights for most of them.",
                [Path.Combine(windows, "Logs", "CBS"), Path.Combine(windows, "Logs", "DISM"), Path.Combine(windows, "Logs", "WindowsUpdate")],
                oneWeek, filePatterns: ["*.log", "*.etl", "*.cab"]),
            Directories("system-dumps", "Windows crash dumps",
                "The dump files Windows writes when it blue-screens - often several GB. Keep them if you are chasing a blue screen. Needs administrator rights.",
                () => new CleanRoot[]
                {
                    new(windows, Recursive: false), // only the dump file directly in it (MEMORY.DMP): nothing else in the Windows folder is looked at
                    Path.Combine(windows, "Minidump"), Path.Combine(windows, "LiveKernelReports"),
                }, TimeSpan.Zero, filePatterns: ["*.dmp"]),
            RecycleBin(),
        ];
    }
}

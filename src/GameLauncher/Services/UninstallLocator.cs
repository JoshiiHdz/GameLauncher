using System.IO;
using GameLauncher.Models;
using Microsoft.Win32;

namespace GameLauncher.Services;

/// <summary>One program Windows lists under "Installed apps": what it is called, how to uninstall it, and where it lives.</summary>
public sealed record InstalledProgram(string DisplayName, string UninstallString, string? InstallLocation, string? DisplayIcon);

/// <summary>An uninstaller to start: the exe and its arguments.</summary>
public sealed record UninstallCommand(string Exe, string Arguments);

/// <summary>
/// Finds the real uninstaller for a game, the same one Windows' Installed apps list would run. It looks the game up in the
/// installed-programs registry (by its install folder first, then by exact name), then for an uninstaller sitting in the game's own
/// folder (Inno Setup's <c>unins000.exe</c>, <c>uninstall.exe</c>). Axis starts the wizard and stops there: the wizard asks the
/// user, and removes whatever it owns - nothing is ever deleted from here.
/// </summary>
public static class UninstallLocator
{
    private static readonly string[] UninstallKeyPaths =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    /// <summary>Everything in the installed-programs registry (machine-wide and per-user, 64 and 32 bit) that can be uninstalled.</summary>
    public static IReadOnlyList<InstalledProgram> ReadInstalledPrograms()
    {
        var programs = new List<InstalledProgram>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var path in UninstallKeyPaths)
            {
                try
                {
                    using var root = hive.OpenSubKey(path);
                    if (root is null)
                        continue;

                    foreach (var name in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var key = root.OpenSubKey(name);
                            var displayName = key?.GetValue("DisplayName") as string;
                            var uninstall = key?.GetValue("UninstallString") as string;
                            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(uninstall))
                                continue;

                            // "SystemComponent" entries are hidden from Installed apps on purpose; they are not ours to open.
                            if (key!.GetValue("SystemComponent") is int and 1)
                                continue;

                            programs.Add(new InstalledProgram(displayName, uninstall,
                                key.GetValue("InstallLocation") as string, key.GetValue("DisplayIcon") as string));
                        }
                        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                        {
                            // One unreadable entry must not hide the rest.
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    Logger.Warn($"Uninstall: couldn't read '{path}'.", ex);
                }
            }
        }

        return programs;
    }

    /// <summary>The uninstaller for this game, or null when none can be found.</summary>
    public static UninstallCommand? Find(GameEntry game, IEnumerable<InstalledProgram> programs, Func<string, bool>? fileExists = null,
        Func<string, IEnumerable<string>>? filesIn = null)
    {
        fileExists ??= File.Exists;
        filesIn ??= SafeFiles;

        var installDir = Normalize(game.InstallDir);
        if (installDir is not null && IsDriveRoot(installDir))
            installDir = null; // a drive's top folder holds many things: nothing in it can be called this game's own uninstaller
        InstalledProgram? byName = null;

        foreach (var program in programs)
        {
            var command = ParseCommand(program.UninstallString, fileExists);
            if (command is null)
                continue;

            if (installDir is not null && SamePlace(installDir, program, command))
                return command; // its own folder: the surest match

            if (byName is null && NameKey(program.DisplayName) is { Length: > 0 } key && key == NameKey(game.DetectedTitle))
                byName = program;
        }

        if (byName is not null)
            return ParseCommand(byName.UninstallString, fileExists);

        return installDir is null ? null : FindInFolder(installDir, fileExists, filesIn);
    }

    /// <summary>True when the entry belongs to this exact install folder: its install location is the folder, or its uninstaller
    /// (or icon) lives inside the folder. A launcher's own entry - or a parent folder shared by many games - never matches.</summary>
    private static bool SamePlace(string installDir, InstalledProgram program, UninstallCommand command)
    {
        if (Normalize(program.InstallLocation) is { } location && string.Equals(location, installDir, StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsInside(installDir, command.Exe))
            return true;

        return IconPath(program.DisplayIcon) is { } icon && IsInside(installDir, icon);
    }

    private static bool IsInside(string folder, string path)
    {
        var full = Normalize(path);
        return full is not null && full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An uninstaller shipped inside the game's own folder: Inno Setup's unins000.exe first, then uninstall.exe and kin,
    /// in the folder itself or one level down.</summary>
    internal static UninstallCommand? FindInFolder(string installDir, Func<string, bool> fileExists, Func<string, IEnumerable<string>> filesIn)
    {
        foreach (var folder in new[] { installDir }.Concat(SafeFolders(installDir)))
        {
            var candidates = filesIn(folder)
                .Where(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                .Where(f => IsUninstallerName(Path.GetFileNameWithoutExtension(f)))
                .OrderBy(f => Path.GetFileNameWithoutExtension(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (fileExists(candidate))
                    return new UninstallCommand(candidate, "");
            }
        }

        return null;
    }

    private static bool IsUninstallerName(string name) =>
        name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)       // Inno Setup: unins000
        || name.Equals("uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Equals("uninstaller", StringComparison.OrdinalIgnoreCase)
        || name.Equals("uninst", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits an UninstallString ("C:\x\unins000.exe" /SILENT, or MsiExec.exe /I{GUID}) into the exe and its arguments. A
    /// Windows Installer "modify" entry (/I) becomes the uninstall (/X). Null when the exe is not there or the string is unusable.</summary>
    internal static UninstallCommand? ParseCommand(string? raw, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Trim();
        string exe;
        string arguments;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close < 0)
                return null;

            exe = text[1..close];
            arguments = text[(close + 1)..].Trim();
        }
        else
        {
            var end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                return null;

            exe = text[..(end + 4)];
            arguments = text[(end + 4)..].Trim();
        }

        if (Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(exe).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            arguments = arguments.Replace("/I{", "/X{", StringComparison.OrdinalIgnoreCase);
            return new UninstallCommand(exe, arguments); // a system program, found on the PATH
        }

        return Path.IsPathFullyQualified(exe) && fileExists(exe) ? new UninstallCommand(exe, arguments) : null;
    }

    private static bool IsDriveRoot(string path) =>
        string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string? IconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
            return null;

        var text = displayIcon.Trim().Trim('"');
        var comma = text.LastIndexOf(',');
        if (comma > 1 && int.TryParse(text[(comma + 1)..], out _))
            text = text[..comma].Trim().Trim('"');

        return text;
    }

    /// <summary>Case-, punctuation- and trademark-insensitive name, so "Hades™" and "HADES" are the same program.</summary>
    private static string NameKey(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.exe").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeFolders(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder).Where(d =>
            {
                var name = Path.GetFileName(d);
                return name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Equals("_Uninstall", StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

using System.IO;
using Microsoft.Win32;

namespace GameLauncher.Services;

/// <summary>One program that starts when the person signs in to Windows.</summary>
/// <param name="Id">Stable key: where it is registered and its name ("run|Discord", "folder|Steam.lnk").</param>
/// <param name="Name">What Windows shows (the entry's name, or the shortcut's file name without .lnk).</param>
/// <param name="Command">What it runs.</param>
/// <param name="Enabled">Whether Windows will start it at sign-in.</param>
/// <param name="Protected">A security or Windows component: shown, but never switched from here.</param>
public sealed record StartupApp(string Id, string Name, string Command, bool Enabled, bool Protected);

/// <summary>The registry and folder access the startup list needs. A seam, so tests never read or change a real PC's startup entries.</summary>
public interface IStartupSource
{
    /// <summary>Entries of the Run keys (current user and, read-only, all users): name to command.</summary>
    IReadOnlyList<(string Name, string Command)> RunEntries();

    /// <summary>File names (with extension) of the shortcuts in the user's Startup folder.</summary>
    IReadOnlyList<string> FolderEntries();

    /// <summary>The Task Manager state of an entry: "Run" for Run-key entries, "StartupFolder" for shortcuts. Null when the person never changed it (enabled).</summary>
    bool? ReadApproved(string area, string name);

    /// <summary>Writes the Task Manager state, the way Task Manager does: reversible, nothing is deleted.</summary>
    void WriteApproved(string area, string name, bool enabled);
}

/// <summary>Lists the programs that start with Windows and switches each on or off the way Task Manager's Startup tab does (the per-user StartupApproved state), so the
/// change is reversible, needs no permission, and nothing is deleted or edited. It is the startup list, not a cleaner: what stays is the person's choice.</summary>
public sealed class StartupAppsService(IStartupSource source)
{
    private static readonly string[] ProtectedWords =
    [
        "securityhealth", "windows defender", "windowsdefender", "msmpeng", "security", "antivirus", "defender", "onedrive setup", "ctfmon", "realtek", "audio", "driver",
    ];

    public IReadOnlyList<StartupApp> List()
    {
        var apps = new List<StartupApp>();
        foreach (var (name, command) in source.RunEntries())
            apps.Add(Make("run", name, name, command, source.ReadApproved("Run", name)));

        foreach (var file in source.FolderEntries())
        {
            var display = Path.GetFileNameWithoutExtension(file);
            apps.Add(Make("folder", file, display, file, source.ReadApproved("StartupFolder", file)));
        }

        return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static StartupApp Make(string kind, string key, string name, string command, bool? approved) =>
        new($"{kind}|{key}", name, command, approved ?? true, IsProtected(name, command));

    internal static bool IsProtected(string name, string command)
    {
        var text = (name + " " + command).ToLowerInvariant();
        return ProtectedWords.Any(w => text.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>Switches an entry. Refuses (false) a protected one or one that is not in the list.</summary>
    public bool SetEnabled(string id, bool enabled)
    {
        var app = List().FirstOrDefault(a => a.Id == id);
        if (app is null || app.Protected)
            return false;

        var parts = id.Split('|', 2);
        source.WriteApproved(parts[0] == "folder" ? "StartupFolder" : "Run", parts[1], enabled);
        return true;
    }
}

/// <summary>The real startup entries.</summary>
public sealed class SystemStartupSource : IStartupSource
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public IReadOnlyList<(string Name, string Command)> RunEntries()
    {
        var list = new List<(string, string)>();
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(RunKey);
                if (key is null)
                    continue;

                foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
                {
                    if (key.GetValue(name) is string command && !list.Any(e => e.Item1.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        list.Add((name, command));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return list;
    }

    public IReadOnlyList<string> FolderEntries()
    {
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(f => f is not null && !f.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)).Cast<string>().ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public bool? ReadApproved(string area, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey + area);
            if (key?.GetValue(name) is not byte[] { Length: > 0 } data)
                return null;

            return (data[0] & 1) == 0 ? true : false; // 02 / 06 enabled; 03 / 07 disabled (the low bit)
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public void WriteApproved(string area, string name, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ApprovedKey + area);
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4); // when it was switched off, as Task Manager records it

        key.SetValue(name, data, RegistryValueKind.Binary);
    }
}

/// <summary>What a startup program says it is: the description and publisher its own file carries (the same two Task Manager's Startup tab shows). Read-only.</summary>
public static class StartupAppInfo
{
    /// <summary>The program's file out of a startup command (quoted or not, with or without arguments); null when there is no file path in it.</summary>
    public static string? ExecutableOf(string command)
    {
        var text = command.Trim();
        if (text.Length == 0)
            return null;

        string candidate;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            candidate = end > 1 ? text[1..end] : text[1..];
        }
        else
        {
            // Unquoted: up to the first ".exe" (a path may hold spaces), else the first word.
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = exe >= 0 ? text[..(exe + 4)] : text.Split(' ')[0];
        }

        candidate = Environment.ExpandEnvironmentVariables(candidate.Trim());
        return candidate.Length > 0 && candidate.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(candidate) ? candidate : null;
    }

    /// <summary>"Discord - Discord Inc." style text read from the file, or null when the file carries nothing useful (or is not there).</summary>
    public static string? Describe(string command)
    {
        try
        {
            if (ExecutableOf(command) is not { } path || !File.Exists(path))
                return null;

            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var what = info.FileDescription?.Trim();
            var who = info.CompanyName?.Trim();
            if (string.IsNullOrEmpty(what) || what.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
                what = info.ProductName?.Trim();

            return (what, who) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => $"{what} - {who}",
                ({ Length: > 0 }, _) => what,
                (_, { Length: > 0 }) => $"By {who}",
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

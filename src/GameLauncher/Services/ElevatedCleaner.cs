using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameLauncher.Services;

/// <summary>Clears Optimize categories with administrator rights, for the files a normal run is refused (the NVIDIA and AMD driver folders, Windows Update downloads). The
/// launcher asks first, then starts <em>itself</em> with the Windows "run as administrator" prompt and a switch; that copy does nothing but clear the named categories,
/// writes what it did to a file and exits (<see cref="RunFromArguments"/>, called from <c>App.Main</c> before any window exists). It can only clear categories the launcher
/// itself defines, by id: it is never handed a path.</summary>
public static class ElevatedCleaner
{
    internal const string Switch = "--clean-elevated";

    /// <summary>One category's outcome as the elevated copy reports it.</summary>
    internal sealed record Result(string Id, long Freed, int Deleted, int Skipped);

    /// <summary>True when this process is the launcher itself (a copy of it may be given any file name - a test build is "GameLauncher-controller-test.exe" - so the
    /// entry assembly is what is asked, not the exe's name), and not a test host or anything else that happens to run this code. Only the launcher may start an elevated
    /// copy of itself.</summary>
    internal static bool IsTheLauncher => IsLauncherProcess(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name, Environment.ProcessPath);

    /// <summary>The rule behind <see cref="IsTheLauncher"/>: the entry assembly is the launcher's, whatever the exe is called, and the process has a file to start again.</summary>
    internal static bool IsLauncherProcess(string? entryAssemblyName, string? processPath) =>
        entryAssemblyName == "GameLauncher" && !string.IsNullOrEmpty(processPath);

    /// <summary>Why the last <see cref="Run"/> returned nothing: it could not start the helper, or the permission prompt was declined. For the sentence the person reads.</summary>
    internal static string? LastProblem { get; private set; }

    internal static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static readonly Regex IdPattern = new("^[a-z0-9-]{1,40}$", RegexOptions.Compiled);

    /// <summary>The elevated copy's work: <c>--clean-elevated id,id --result path</c>. Returns the process exit code (0 when it ran, 2 when the arguments make no sense).</summary>
    internal static int RunFromArguments(IReadOnlyList<string> args, IReadOnlyList<CleanCategory>? categories = null)
    {
        var at = args.ToList().FindIndex(a => a == Switch);
        var resultAt = args.ToList().FindIndex(a => a == "--result");
        if (at < 0 || at + 1 >= args.Count || resultAt < 0 || resultAt + 1 >= args.Count)
            return 2;

        var ids = args[at + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(i => IdPattern.IsMatch(i)).Distinct().ToList();
        var known = (categories ?? StorageCleaner.DefaultCategories()).ToDictionary(c => c.Id);
        var results = new List<Result>();
        foreach (var id in ids)
        {
            if (!known.TryGetValue(id, out var category))
                continue; // an id the launcher does not have: never anything but its own categories

            try
            {
                var outcome = category.Clean(CancellationToken.None);
                results.Add(new Result(id, outcome.FreedBytes, outcome.Deleted, outcome.Skipped));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Elevated clean: '{id}' failed.", ex);
                results.Add(new Result(id, 0, 0, 0));
            }
        }

        try
        {
            File.WriteAllText(args[resultAt + 1], JsonSerializer.Serialize(results));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 3;
        }

        return 0;
    }

    /// <summary>Starts the elevated copy for these categories and waits for it. Null when the user declined the Windows prompt (or it could not start); otherwise what each
    /// category did there.</summary>
    internal static IReadOnlyDictionary<string, CleanOutcome>? Run(IReadOnlyList<string> categoryIds)
    {
        var ids = categoryIds.Where(i => IdPattern.IsMatch(i)).Distinct().ToList();
        LastProblem = null;
        if (ids.Count == 0)
            return null;

        // Only the launcher itself can be started this way: a test host, or anything else that happens to be running this code, must never raise a permission prompt.
        if (!IsTheLauncher || Environment.ProcessPath is not { } exe)
        {
            LastProblem = "this copy of the launcher cannot start its administrator helper";
            return null;
        }

        var resultPath = Path.Combine(Path.GetTempPath(), "axis-clean-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = true, // required for the "runas" verb: Windows shows its own permission prompt
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"{Switch} {string.Join(',', ids)} --result \"{resultPath}\"",
            };

            using var process = Process.Start(start);
            if (process is null)
                return null;

            process.WaitForExit((int)TimeSpan.FromMinutes(30).TotalMilliseconds);
            if (!File.Exists(resultPath))
                return null;

            var results = JsonSerializer.Deserialize<List<Result>>(File.ReadAllText(resultPath)) ?? [];
            return results.ToDictionary(r => r.Id, r => new CleanOutcome(r.Freed, r.Deleted, r.Skipped));
        }
        catch (Win32Exception ex)
        {
            Logger.Info($"Elevated clean: not started ({ex.Message}) - the permission prompt was probably declined.");
            LastProblem = ex.NativeErrorCode == 1223 ? "the Windows permission prompt was declined" : $"Windows would not start the helper ({ex.Message})";
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.Warn("Elevated clean: the result could not be read.", ex);
            return null;
        }
        finally
        {
            try { File.Delete(resultPath); } catch (IOException) { }
        }
    }
}

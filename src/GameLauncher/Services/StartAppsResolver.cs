using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GameLauncher.Services;

/// <summary>
/// Resolves the AppUserModelID (AUMID) for installed Store/MSIX apps via PowerShell's Get-StartApps,
/// so packaged games (Xbox/Game Pass titles) can be launched the way a real Start Menu or desktop
/// shortcut launches them: "shell:appsFolder\{AUMID}".
///
/// This exists because running an Xbox title's exe (e.g. gamelaunchhelper.exe) directly, the way a
/// normal Win32 game is launched, skips the activation context the OS sets up when a shortcut
/// activates the package properly - a documented source of failures for third-party launchers
/// handling Store apps. Get-StartApps entries for real packaged apps have an AUMID containing "!"
/// (PackageFamilyName!AppId); plain Start Menu shortcuts to traditional exes don't, and are ignored.
/// </summary>
public static class StartAppsResolver
{
    public static IReadOnlyList<(string Name, string Aumid)> GetPackagedApps()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // Real Start Menu titles often carry ®/™ (e.g. "Call of Duty®"). A first attempt
                    // forced [Console]::OutputEncoding = UTF8 on the child and StandardOutputEncoding
                    // on the parent to match - verified working on the dev machine, but a real log from
                    // a different PC still came back mangled ("Call of DutyÂ®" - the classic signature
                    // of UTF-8 bytes read back as Windows-1252/Latin-1), meaning [Console]::OutputEncoding
                    // doesn't reliably apply to a console-less redirected process across every Windows
                    // PowerShell version/locale. Base64-encoding the JSON before it ever leaves
                    // PowerShell sidesteps the whole problem: the Base64 alphabet (A-Z, a-z, 0-9, +, /,
                    // =) is a subset of plain ASCII, so it reads back identically no matter which
                    // codepage either side is using - there is no codepage left to disagree over.
                    Arguments = "-NoProfile -NonInteractive -Command "
                        + "\"Get-StartApps | ConvertTo-Json -Compress | "
                        + "ForEach-Object { [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($_)) }\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            var rawOutput = RunAndReadStdout(process, TimeSpan.FromSeconds(10));
            if (rawOutput is null)
            {
                Logger.Warn("Get-StartApps timed out.");
                return [];
            }

            var base64Output = rawOutput.Trim();
            if (string.IsNullOrWhiteSpace(base64Output))
                return [];

            var output = Encoding.UTF8.GetString(Convert.FromBase64String(base64Output));
            using var doc = JsonDocument.Parse(output);
            var results = new List<(string, string)>();

            // A single result comes back as an object, not an array.
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : new[] { doc.RootElement }.AsEnumerable();

            foreach (var item in items)
            {
                var name = item.GetProperty("Name").GetString();
                var appId = item.GetProperty("AppID").GetString();

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId) || !appId.Contains('!'))
                    continue; // not a packaged app's AUMID - a plain Start Menu shortcut

                results.Add((name, appId));
            }

            return results;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                        or JsonException or IOException or FormatException)
        {
            Logger.Warn("Couldn't resolve Store app IDs via Get-StartApps.", ex);
            return [];
        }
    }

    /// <summary>Starts `process` and drains its stdout ASYNCHRONOUSLY while waiting for it to exit,
    /// both under one shared cancellation budget (`timeout`) - not the previous version's two
    /// independent steps (a blocking, synchronous StandardOutput.ReadToEnd() followed by a SEPARATE
    /// WaitForExit(10_000)). That ordering meant the 10-second timeout could never actually fire for a
    /// child that never closes stdout: a synchronous ReadToEnd() blocks until EOF regardless of how much
    /// time has passed, so a hung child (or one that simply produces more output than the OS pipe
    /// buffer holds while nothing is draining it - the same deadlock Microsoft's own Process docs warn
    /// about) left this method waiting forever, well before WaitForExit's own timeout was ever reached.
    /// Reading and waiting together under one token closes that gap.
    ///
    /// Returns the process's full stdout on success, or null if `timeout` elapsed first - in which case
    /// termination of the process (and, best-effort via Kill(entireProcessTree: true), any children the
    /// OS recorded under it at that moment) is REQUESTED and then confirmed with a short, bounded wait -
    /// see TryKill's own remarks for exactly what "confirmed" does and doesn't guarantee here.</summary>
    internal static string? RunAndReadStdout(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        process.Start();

        try
        {
            var readTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var waitTask = process.WaitForExitAsync(cts.Token);
            Task.WhenAll(readTask, waitTask).GetAwaiter().GetResult();
            return readTask.Result;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return null;
        }
    }

    // Bounded deliberately: this exists specifically to confirm Kill() actually finished, without
    // reintroducing a second, unbounded wait of the exact kind RunAndReadStdout's own timeout exists to
    // avoid. 2 seconds is generous for an OS to tear down one already-signalled PowerShell process.
    private const int KillConfirmationTimeoutMs = 2000;

    /// <summary>Process.Kill() only REQUESTS termination - it's documented as asynchronous, so trusting
    /// HasExited (or nothing at all) immediately after calling it is timing-dependent and can read
    /// "still running" even though termination is already in flight. This confirms with a short, bounded
    /// WaitForExit instead of assuming success. Kill(entireProcessTree: true) also targets whatever
    /// children the OS process tree recorded under this process AT THE MOMENT it's called - that is a
    /// best-effort request, not a provable guarantee against every descendant in every race (a
    /// grandchild spawned in the narrow window between the tree walk and actual termination could still
    /// survive; this method doesn't attempt to detect or verify that). If confirmation times out, this
    /// logs rather than silently assuming the cleanup succeeded.</summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (process.HasExited)
                return;

            process.Kill(entireProcessTree: true);

            if (!process.WaitForExit(KillConfirmationTimeoutMs))
            {
                Logger.Warn("Get-StartApps: requested termination of a hung PowerShell process, but "
                    + $"couldn't confirm it (and any children it spawned) actually exited within "
                    + $"{KillConfirmationTimeoutMs}ms - it may still be running.");
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill attempt - nothing left to clean up.
        }
    }
}

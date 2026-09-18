using System.Diagnostics;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>
/// Exercises StartAppsResolver.RunAndReadStdout directly against REAL child processes - unlike the rest
/// of this test suite (built around fakes/FakeTimeProvider for speed and determinism), the bug this
/// covers is a genuine OS-level deadlock between a child process's stdout pipe and .NET's own
/// synchronous Process APIs, which can't be meaningfully faked without a full process-abstraction
/// rewrite. Timeouts here are kept short (well under a second) specifically so this stays fast despite
/// spawning real processes.
/// </summary>
public class StartAppsResolverTests
{
    private static Process MakeProcess(string arguments) => new()
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        },
    };

    [Fact]
    public void ProcessCompletesPromptly_ReturnsItsOutput()
    {
        using var process = MakeProcess("-NoProfile -NonInteractive -Command \"Write-Output 'hello'\"");
        var output = StartAppsResolver.RunAndReadStdout(process, TimeSpan.FromSeconds(10));

        Assert.NotNull(output);
        Assert.Contains("hello", output);
    }

    [Fact]
    public void ProcessKeepsStdoutOpenIndefinitely_DoesNotHang_ReturnsNullAfterTimeout()
    {
        // Writes some output (so a pipe is genuinely open and non-empty, not just idle) and then sleeps
        // far longer than this test's own timeout budget, without ever closing stdout on its own -
        // exactly the shape that deadlocked the previous synchronous ReadToEnd()-before-WaitForExit()
        // implementation: that call blocks until EOF no matter how much time passes, so the old
        // 10-second WaitForExit() timeout was never even reached.
        using var process = MakeProcess(
            "-NoProfile -NonInteractive -Command \"Write-Output 'partial'; Start-Sleep -Seconds 60\"");

        var stopwatch = Stopwatch.StartNew();
        var output = StartAppsResolver.RunAndReadStdout(process, TimeSpan.FromMilliseconds(500));
        stopwatch.Stop();

        Assert.Null(output); // timed out, not a successful (if partial) read
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"RunAndReadStdout took {stopwatch.Elapsed.TotalSeconds:0.0}s - should have returned shortly after its own timeout, not hung on the child's still-open stdout.");
        Assert.True(process.HasExited, "The hung child process should have been killed on timeout, not left running.");
    }
}

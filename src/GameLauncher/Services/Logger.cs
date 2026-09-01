using System.IO;
using System.Text;

namespace GameLauncher.Services;

/// <summary>
/// Minimal file logger - one plain-text file per run under Data\logs, so a crash or a "why didn't
/// this game show up" report from a machine with no debugger attached can just be pasted back.
/// Deliberately dependency-free (no logging framework) to keep the app lean.
/// </summary>
public static class Logger
{
    private const int MaxLogFiles = 10;

    private static readonly object Lock = new();
    public static readonly string LogDir = Path.Combine(AppPaths.DataDir, "logs");

    public static string CurrentLogPath { get; } = Path.Combine(LogDir, $"log-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

    static Logger()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            RotateOldLogs();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        Info($"Game Launcher {AppInfo.Version} starting up.");
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var line = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] ")
                .Append(level).Append(": ").Append(message);

            if (ex is not null)
                line.Append(Environment.NewLine).Append(ex);

            lock (Lock)
            {
                File.AppendAllText(CurrentLogPath, line + Environment.NewLine);
            }
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
        {
            // Logging must never itself take down the app.
        }
    }

    private static void RotateOldLogs()
    {
        var files = Directory.GetFiles(LogDir, "log-*.txt").OrderByDescending(f => f).Skip(MaxLogFiles - 1);
        foreach (var file in files)
            File.Delete(file);
    }
}

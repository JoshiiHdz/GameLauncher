using System.Text;

namespace GameLauncher.Services;

/// <summary>Builds the "Report a bug" GitHub issue link, prefilled with the facts every report needs (app version, Windows build,
/// where the log is) so the reporter only has to describe what went wrong. Nothing is sent from here: it only makes a URL.</summary>
public static class BugReport
{
    public const string IssuesUrl = "https://github.com/JoshiiHdz/GameLauncher/issues/new";

    public static string BuildIssueUrl(string appVersion, string osVersion)
    {
        var body = new StringBuilder()
            .AppendLine("**What happened?**")
            .AppendLine()
            .AppendLine("**What did you expect?**")
            .AppendLine()
            .AppendLine("**Steps to reproduce**")
            .AppendLine()
            .AppendLine("**Which game / launcher (if it's about a game)?**")
            .AppendLine()
            .AppendLine("_Please drag the newest file from the logs folder into this box (Settings > Open Logs Folder)._")
            .AppendLine()
            .AppendLine("---")
            .AppendLine($"Axis Game Launcher {appVersion} - {osVersion}")
            .ToString();

        return $"{IssuesUrl}?title={Uri.EscapeDataString("Bug: ")}&body={Uri.EscapeDataString(body)}";
    }
}

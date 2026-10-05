using System.IO;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>"Report a bug" opens a prefilled GitHub issue: the version and Windows build are filled in so a report is never missing
/// them, and the free text is escaped so it survives as a link.</summary>
public class BugReportTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    [Fact]
    public void TheIssueLink_PointsAtTheNewIssueForm_AndCarriesTheVersionAndWindowsBuild()
    {
        var url = BugReport.BuildIssueUrl("1.20.0", "Microsoft Windows NT 10.0.26200.0");
        var uri = new Uri(url);

        Assert.Equal("https", uri.Scheme);
        Assert.StartsWith(BugReport.IssuesUrl + "?", url);

        var body = Uri.UnescapeDataString(url[(url.IndexOf("body=", StringComparison.Ordinal) + 5)..]);
        Assert.Contains("Axis Game Launcher 1.20.0", body);
        Assert.Contains("Microsoft Windows NT 10.0.26200.0", body);
        Assert.Contains("What happened?", body);
    }

    [Fact]
    public void TheIssueLink_HasNoRawSpacesOrNewlines()
    {
        var url = BugReport.BuildIssueUrl("1.0.0", "Windows 11");

        Assert.DoesNotContain(' ', url);
        Assert.DoesNotContain('\n', url);
        Assert.Contains("title=Bug%3A%20", url);
    }

    [Fact]
    public void TheReportBugCommand_OpensTheFeedbackForm()
    {
        var vm = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));
        FeedbackViewModel? opened = null;
        vm.FeedbackDialogForTest = f => opened = f;

        vm.ReportBugCommand.Execute(null);

        Assert.NotNull(opened);
        Assert.True(opened.IncludeDetails);
    }
}

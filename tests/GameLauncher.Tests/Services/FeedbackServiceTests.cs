using System.Net;
using System.Net.Http;
using System.Text.Json;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Services;

/// <summary>The in-app feedback form: what is sent, where, when it refuses, and what the user is told when it can't.</summary>
public class FeedbackServiceTests : IDisposable
{
    private static readonly RelayEndpoint Relay = new(new Uri("https://relay.test"), ["sha256/test"]);

    public FeedbackServiceTests() => FeedbackService.ResetRateLimitForTest();

    public void Dispose() => FeedbackService.ResetRateLimitForTest();

    private sealed class FakeHandler(HttpStatusCode status = HttpStatusCode.NoContent, Exception? throws = null) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (throws is not null)
                throw throws;
            return new HttpResponseMessage(status);
        }
    }

    private static (FeedbackService Service, FakeHandler Handler) Make(HttpStatusCode status = HttpStatusCode.NoContent, Exception? throws = null, Func<DateTime>? clock = null)
    {
        var handler = new FakeHandler(status, throws);
        return (new FeedbackService(Relay, new HttpClient(handler), clock ?? (() => DateTime.UtcNow)), handler);
    }

    // ---- the message -------------------------------------------------------------------------------

    [Fact]
    public void TheMessage_NamesTheKind_AndCarriesTheText_TheContact_AndTheDetails()
    {
        var text = FeedbackService.BuildMessage(FeedbackKind.Idea, "  Add a stats page  ", "me#1234", "Axis Game Launcher 1.0.0 - Windows");

        Assert.StartsWith("**Idea**\nAdd a stats page", text);
        Assert.Contains("Contact: me#1234", text);
        Assert.Contains("Axis Game Launcher 1.0.0 - Windows", text);
    }

    [Fact]
    public void ALongMessage_IsCappedBelowTheLimitTheDestinationAccepts()
    {
        var text = FeedbackService.BuildMessage(FeedbackKind.Bug, new string('x', 5000), new string('c', 500), new string('d', 3000));

        Assert.True(text.Length <= FeedbackService.MaxMessageLength);
    }

    [Fact]
    public void WithoutContactOrDetails_NeitherLineAppears()
    {
        var text = FeedbackService.BuildMessage(FeedbackKind.Bug, "It crashed", "  ", null);

        Assert.DoesNotContain("Contact", text);
        Assert.DoesNotContain("```", text);
    }

    [Fact]
    public void TheDetails_HaveTheVersion_TheWindowsBuild_AndOnlyTheLastFewProblemLines()
    {
        var log = string.Join("\n",
            "[10:00:00.000] INFO: starting",
            "[10:00:01.000] WARN: one",
            "[10:00:02.000] ERROR: two",
            "[10:00:03.000] INFO: fine",
            "[10:00:04.000] WARN: three");

        var details = FeedbackService.BuildDetails("1.2.3", "Windows 11", log, maxProblems: 2);

        Assert.StartsWith("Axis Game Launcher 1.2.3 - Windows 11", details);
        Assert.DoesNotContain("INFO", details);
        Assert.DoesNotContain("one", details);
        Assert.Contains("ERROR: two", details);
        Assert.Contains("WARN: three", details);
    }

    [Fact]
    public void ALongLogLine_IsShortened_AndCannotCloseTheCodeBlock()
    {
        var log = "[10:00:00.000] ERROR: " + new string('y', 400) + "```";

        var details = FeedbackService.BuildDetails("1.0.0", "Windows", log);

        Assert.DoesNotContain("```", details);
        Assert.True(details.Length < 400);
    }

    // ---- sending -----------------------------------------------------------------------------------

    [Fact]
    public async Task Sending_PostsAWebhookShapedBody_ToTheRelaysFeedbackPath_AndNeverAllowsMentions()
    {
        var (service, handler) = Make();

        var outcome = await service.SendAsync(FeedbackKind.Bug, "Wrong cover for @everyone", null, null);

        Assert.Equal(FeedbackOutcome.Sent, outcome);
        var (uri, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://relay.test/feedback", uri!.ToString());
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("Wrong cover", doc.RootElement.GetProperty("content").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
    }

    [Fact]
    public async Task WithNoRelayInTheBuild_NothingIsSent_AndItIsReported()
    {
        var handler = new FakeHandler();
        var service = new FeedbackService(null, new HttpClient(handler), () => DateTime.UtcNow);

        Assert.Equal(FeedbackOutcome.NotConfigured, await service.SendAsync(FeedbackKind.Bug, "x", null, null));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ABlankMessage_IsNotSent()
    {
        var (service, handler) = Make();

        Assert.Equal(FeedbackOutcome.Failed, await service.SendAsync(FeedbackKind.Bug, "   ", null, null));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ARefusedMessage_IsReportedAsFailed(HttpStatusCode status)
    {
        var (service, _) = Make(status);

        Assert.Equal(FeedbackOutcome.Failed, await service.SendAsync(FeedbackKind.Bug, "x", null, null));
    }

    [Fact]
    public async Task AnUnreachableRelay_IsReportedAsFailed_NotThrown()
    {
        var (service, _) = Make(throws: new HttpRequestException("no route"));

        Assert.Equal(FeedbackOutcome.Failed, await service.SendAsync(FeedbackKind.Bug, "x", null, null));
    }

    [Fact]
    public async Task ASecondMessageRightAfterTheFirst_IsHeldBack_ButAllowedAfterTheGap()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var (service, handler) = Make(clock: () => now);

        Assert.Equal(FeedbackOutcome.Sent, await service.SendAsync(FeedbackKind.Bug, "one", null, null));
        now += TimeSpan.FromSeconds(5);
        Assert.Equal(FeedbackOutcome.TooSoon, await service.SendAsync(FeedbackKind.Bug, "two", null, null));
        now += FeedbackService.MinimumGap;
        Assert.Equal(FeedbackOutcome.Sent, await service.SendAsync(FeedbackKind.Bug, "three", null, null));
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---- the form's view model ---------------------------------------------------------------------

    [Fact]
    public void TheSendButton_NeedsAMessage()
    {
        var (service, _) = Make();
        var vm = new FeedbackViewModel(service, () => "details");

        Assert.False(vm.SendCommand.CanExecute(null));
        vm.Message = "  ";
        Assert.False(vm.SendCommand.CanExecute(null));
        vm.Message = "Something broke";
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task ASentForm_SaysThankYou_AndCannotBeSentTwice()
    {
        var (service, handler) = Make();
        var vm = new FeedbackViewModel(service, () => "DETAILS-LINE") { Message = "Please add stats" };
        vm.SelectedKind = vm.Kinds[1];

        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(vm.IsSent);
        Assert.Contains("thank you", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.SendCommand.CanExecute(null));
        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("Idea", body);
        Assert.Contains("DETAILS-LINE", body);
    }

    [Fact]
    public async Task WithTheDetailsBoxUnticked_NoDetailsAreSent()
    {
        var (service, handler) = Make();
        var vm = new FeedbackViewModel(service, () => "DETAILS-LINE") { Message = "hello", IncludeDetails = false };

        await vm.SendCommand.ExecuteAsync(null);

        Assert.DoesNotContain("DETAILS-LINE", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task AFailedSend_KeepsTheText_AndOffersGitHubInstead()
    {
        var (service, _) = Make(HttpStatusCode.BadGateway);
        var vm = new FeedbackViewModel(service, () => "d") { Message = "keep me" };
        string? opened = null;
        vm.OpenUrlForTest = u => opened = u;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.False(vm.IsSent);
        Assert.Equal("keep me", vm.Message);
        Assert.True(vm.ShowGitHubFallback);
        Assert.True(vm.SendCommand.CanExecute(null)); // they can try again

        vm.OpenGitHubCommand.Execute(null);
        Assert.StartsWith(BugReport.IssuesUrl, opened);
    }

    [Fact]
    public async Task ABuildWithNoRelay_ExplainsItAndOffersGitHub()
    {
        var vm = new FeedbackViewModel(new FeedbackService(null, null, () => DateTime.UtcNow), () => "d") { Message = "hi" };

        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(vm.ShowGitHubFallback);
        Assert.Contains("GitHub", vm.StatusText);
    }
}

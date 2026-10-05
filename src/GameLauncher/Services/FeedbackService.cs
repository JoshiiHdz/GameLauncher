using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services;

public enum FeedbackKind { Bug, Idea, Other }

public enum FeedbackOutcome
{
    Sent,

    /// <summary>This build has no relay address, so there is nowhere to send to.</summary>
    NotConfigured,

    /// <summary>Another message was sent a moment ago.</summary>
    TooSoon,

    /// <summary>The relay did not answer, or refused the message.</summary>
    Failed,
}

/// <summary>Sends the in-app feedback form to the project's relay, which forwards it to the developer (see docs/deploy/igdb-relay).
/// No account, no email client and no website is needed. The destination's secret lives only on the server: the launcher knows the relay
/// address and nothing else. The payload is shaped like a Discord webhook body so the relay can pass it through unchanged.</summary>
public sealed class FeedbackService
{
    /// <summary>Discord rejects a message over 2000 characters; leave headroom.</summary>
    internal const int MaxMessageLength = 1900;
    internal const int MaxUserTextLength = 1200;
    internal static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(30);

    private static DateTime _lastSentUtc = DateTime.MinValue;

    private readonly RelayEndpoint? _relay;
    private readonly HttpClient? _clientOverride;
    private readonly Func<DateTime> _utcNow;

    public FeedbackService() : this(DefaultIgdbRelay.Current, null, () => DateTime.UtcNow) { }

    internal FeedbackService(RelayEndpoint? relay, HttpClient? client, Func<DateTime> utcNow)
    {
        _relay = relay;
        _clientOverride = client;
        _utcNow = utcNow;
    }

    /// <summary>The text that is sent. Plain text, capped, with an optional line of technical details.</summary>
    internal static string BuildMessage(FeedbackKind kind, string text, string? contact, string? details)
    {
        var label = kind switch { FeedbackKind.Bug => "Bug", FeedbackKind.Idea => "Idea", _ => "Feedback" };
        var body = text.Trim();
        if (body.Length > MaxUserTextLength)
            body = body[..MaxUserTextLength] + "...";

        var message = new StringBuilder().Append("**").Append(label).Append("**\n").Append(body);
        if (!string.IsNullOrWhiteSpace(contact))
            message.Append("\nContact: ").Append(contact.Trim().Replace('\n', ' '));
        if (!string.IsNullOrWhiteSpace(details))
            message.Append("\n```\n").Append(details.Trim()).Append("\n```");

        var result = message.ToString();
        return result.Length <= MaxMessageLength ? result : result[..MaxMessageLength];
    }

    /// <summary>App version, Windows version and the last few WARN/ERROR lines of this run's log (each shortened).</summary>
    internal static string BuildDetails(string appVersion, string osVersion, string? logText, int maxProblems = 5)
    {
        var details = new StringBuilder().Append("Axis Game Launcher ").Append(appVersion).Append(" - ").Append(osVersion);
        foreach (var line in RecentProblemLines(logText, maxProblems))
            details.Append('\n').Append(line);
        return details.ToString().Replace("```", "'''");
    }

    internal static IReadOnlyList<string> RecentProblemLines(string? logText, int max)
    {
        if (string.IsNullOrEmpty(logText))
            return [];

        return logText.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Contains("] WARN:", StringComparison.Ordinal) || l.Contains("] ERROR:", StringComparison.Ordinal))
            .TakeLast(max)
            .Select(l => l.Length > 160 ? l[..160] + "..." : l)
            .ToList();
    }

    /// <summary>The details for a form that has "include technical details" ticked, read from the log of this run.</summary>
    public static string CurrentDetails()
    {
        string? log = null;
        try
        {
            if (File.Exists(Logger.CurrentLogPath))
                log = File.ReadAllText(Logger.CurrentLogPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return BuildDetails(AppInfo.Version, Environment.OSVersion.VersionString, log);
    }

    public async Task<FeedbackOutcome> SendAsync(FeedbackKind kind, string text, string? contact, string? details, CancellationToken ct = default)
    {
        if (_relay is null)
            return FeedbackOutcome.NotConfigured;
        if (string.IsNullOrWhiteSpace(text))
            return FeedbackOutcome.Failed;
        if (_utcNow() - _lastSentUtc < MinimumGap)
            return FeedbackOutcome.TooSoon;

        var payload = JsonSerializer.Serialize(new
        {
            content = BuildMessage(kind, text, contact, details),
            allowed_mentions = new { parse = Array.Empty<string>() }, // a typed @everyone must never ping anyone
        });

        try
        {
            var client = _clientOverride ?? RelayTransport.ClientFor(_relay.Pins);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri(_relay.Address, "/feedback"), content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"Feedback: the relay answered {(int)response.StatusCode}.");
                return FeedbackOutcome.Failed;
            }

            _lastSentUtc = _utcNow();
            Logger.Info("Feedback: sent.");
            return FeedbackOutcome.Sent;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
                throw;
            Logger.Warn("Feedback: couldn't reach the relay.", ex);
            return FeedbackOutcome.Failed;
        }
    }

    /// <summary>Test seam: forget the last send so each test starts fresh.</summary>
    internal static void ResetRateLimitForTest() => _lastSentUtc = DateTime.MinValue;
}

using System.Text.Json.Serialization;

namespace GameLauncher.Models;

/// <summary>One stretch of play time this app tracked: when it began and how long it lasted. A launched session is one record; a game noticed
/// running outside the launcher is recorded in the same way, and records that follow on within a couple of minutes are joined into one.</summary>
public sealed class PlaySessionRecord
{
    public DateTime StartUtc { get; set; }

    public long Seconds { get; set; }

    [JsonIgnore]
    public DateTime EndUtc => StartUtc.AddSeconds(Seconds);
}

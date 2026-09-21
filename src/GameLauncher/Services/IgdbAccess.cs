namespace GameLauncher.Services;

/// <summary>An IGDB (Twitch application) Client ID and Client Secret the USER entered. The default ToString of a record would print the
/// secret into any log line or exception message that formats it, so it is replaced.</summary>
public sealed record IgdbCredentials(string ClientId, string ClientSecret)
{
    public override string ToString() => $"IgdbCredentials(ClientId={ClientId}, ClientSecret=<hidden>)";
}

public enum IgdbAccessKind { None, UserCredentials, SharedRelay }

/// <summary>How the launcher reaches IGDB: the user's own credentials directly, else the project's relay, else not at all.</summary>
public sealed record IgdbAccess(IgdbAccessKind Kind, IgdbCredentials? Credentials = null, RelayEndpoint? Relay = null)
{
    /// <summary>A COMPLETE user pair wins. A half-entered pair is ignored - never combined with anything - and falls through to the relay;
    /// with no relay in this build, IGDB is simply off (SteamGridDB only).</summary>
    public static IgdbAccess Resolve(string? userClientId, string? userClientSecret, RelayEndpoint? relay)
    {
        if (!string.IsNullOrWhiteSpace(userClientId) && !string.IsNullOrWhiteSpace(userClientSecret))
            return new IgdbAccess(IgdbAccessKind.UserCredentials, new IgdbCredentials(userClientId, userClientSecret));

        return relay is not null ? new IgdbAccess(IgdbAccessKind.SharedRelay, Relay: relay) : new IgdbAccess(IgdbAccessKind.None);
    }
}

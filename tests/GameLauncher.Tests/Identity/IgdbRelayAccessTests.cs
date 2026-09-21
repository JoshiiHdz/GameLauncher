using System.IO;
using System.Net;
using System.Net.Http;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;

namespace GameLauncher.Tests.Identity;

/// <summary>How the launcher reaches IGDB with nothing to set up: the project's relay. The relay (docs/deploy/igdb-relay) holds the Twitch
/// credentials, so the launcher holds none and sends none. Everything here is against a fake transport; whether the deployed relay
/// answers is checked separately, end to end. No test contains a real address or credential.</summary>
[Collection(IgdbStaticStateCollection.Name)]
public class IgdbRelayAccessTests : IDisposable
{
    private const string PinA = "sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string PinB = "sha256/BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=";
    private static readonly RelayEndpoint Relay = new(new Uri("https://relay.example.test"), [PinA]);
    private readonly List<string> _dirs = new();

    public IgdbRelayAccessTests()
    {
        IgdbCoverArtProvider.ResetRateLimitForTest();
    }

    public void Dispose()
    {
        foreach (var dir in _dirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Relay-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    // ---- the address + pin file -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://relay.example.test\n" + PinA, "https://relay.example.test/", PinA)]
    [InlineData("  https://198.51.100.7/  \r\n  " + PinA + "  \r\n", "https://198.51.100.7/", PinA)]
    [InlineData("https://relay.example.test:8443\n" + PinA + "," + PinB, "https://relay.example.test:8443/", PinA + "," + PinB)]   // a second pin allows key rotation
    public void TheFile_WithAnHttpsAddressAndPins_IsAccepted(string text, string address, string pins)
    {
        var parsed = DefaultIgdbRelay.Parse(text)!;

        Assert.Equal(address, parsed.Address.AbsoluteUri);
        Assert.Equal(pins.Split(','), parsed.Pins);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://relay.example.test")]                                              // no pin: an unauthenticated relay is never accepted
    [InlineData("http://relay.example.test\n" + PinA)]                                      // plain http is never accepted
    [InlineData("relay.example.test\n" + PinA)]                                             // no scheme
    [InlineData("ftp://relay.example.test\n" + PinA)]
    [InlineData("https://user:pass@relay.example.test\n" + PinA)]                           // credentials in an address
    [InlineData("https://relay.example.test/v4/games\n" + PinA)]                            // no path: the launcher adds /v4/{endpoint} itself
    [InlineData("https://relay.example.test/?x=1\n" + PinA)]
    [InlineData("https://relay.example.test/#frag\n" + PinA)]
    [InlineData("https://relay.example.test\nsha256/short=")]                               // wrong pin length
    [InlineData("https://relay.example.test\nAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]  // no sha256/ prefix
    [InlineData("https://relay.example.test\nsha1/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("https://relay.example.test\n" + PinA + "," + PinA)]                        // duplicate pin
    [InlineData("https://relay.example.test\n" + PinA + "," + PinB + ",sha256/CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC=,sha256/DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD=")]   // four pins
    [InlineData("https://a.example.test\nhttps://b.example.test\n" + PinA)]                  // three lines: never a guess
    public void TheFile_WithAnythingElse_MeansNoBuiltInAccess(string? text) => Assert.Null(DefaultIgdbRelay.Parse(text));

    [Fact]
    public void TheEmbeddedAddress_LoadsWithoutCrashing_WhateverTheBuildEmbeds()
    {
        // Regression: Current is initialised by Load() -> Parse() during the type's static initialisation. A static field that Parse needed but that was
        // declared AFTER Current was still null then, and every build that embedded an address file died with a TypeInitializationException. This build
        // may or may not embed one; either way reading it must not throw, and whatever it yields must itself be a valid endpoint.
        var current = Record.Exception(() => DefaultIgdbRelay.Current);

        Assert.Null(current);
        if (DefaultIgdbRelay.Current is { } endpoint)
        {
            Assert.Equal("https", endpoint.Address.Scheme);
            Assert.NotEmpty(endpoint.Pins);
        }
    }

    // ---- whose access is used ----------------------------------------------------------------------------------------------

    [Fact]
    public void ACompleteUserPair_IsUsedDirectly_EvenWhenARelayExists()
    {
        var access = IgdbAccess.Resolve("my-id", "my-secret", Relay);

        Assert.Equal(IgdbAccessKind.UserCredentials, access.Kind);
        Assert.Equal(new IgdbCredentials("my-id", "my-secret"), access.Credentials);
        Assert.Null(access.Relay);
    }

    [Theory]
    [InlineData("my-id", null)]
    [InlineData("my-id", "")]
    [InlineData(null, "my-secret")]
    [InlineData("  ", "my-secret")]
    public void AHalfEnteredPair_IsIgnored_AndTheRelayIsUsed(string? id, string? secret)
    {
        var access = IgdbAccess.Resolve(id, secret, Relay);

        Assert.Equal(IgdbAccessKind.SharedRelay, access.Kind);
        Assert.Null(access.Credentials);
        Assert.Equal(Relay, access.Relay);
    }

    [Fact]
    public void WithNothingEnteredAndNoRelayInTheBuild_ThereIsNoIgdb() =>
        Assert.Equal(IgdbAccessKind.None, IgdbAccess.Resolve(null, null, relay: null).Kind);

    [Fact]
    public void ThePair_NeverPrintsItsSecret_WhenFormatted()
    {
        var text = IgdbAccess.Resolve("my-id", "my-secret", Relay).ToString();

        Assert.DoesNotContain("my-secret", text);
        Assert.Contains("my-id", text);
    }

    // ---- what the provider list becomes -------------------------------------------------------------------------------------

    [Fact]
    public void WithARelayAndNothingEntered_IgdbIsPrimary_ForAUserWhoSetNothingUp()
    {
        var providers = CatalogProviders.Create(null, null, "sgdb-key", relayOverride: () => Relay);

        Assert.IsType<IgdbCatalog>(providers[0]);
        Assert.IsType<SteamGridDbCatalog>(providers[1]);
    }

    [Fact]
    public void WithoutARelay_AndNothingEntered_TheLauncherRunsSteamGridDbOnly_AsBefore()
    {
        var providers = CatalogProviders.Create(null, null, "sgdb-key", relayOverride: () => null);

        Assert.Single(providers);
        Assert.IsType<SteamGridDbCatalog>(providers[0]);
    }

    [Fact]
    public void AUserPairStillWorksWithoutAnyRelay() =>
        Assert.Contains(CatalogProviders.Create("my-id", "my-secret", "sgdb-key", relayOverride: () => null), p => p is IgdbCatalog);

    // ---- the transport in relay mode -----------------------------------------------------------------------------------------

    private sealed class RecordingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(Uri Uri, string Body, bool HadAuthorization, bool HadClientId)> Requests = new();

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content?.ReadAsStringAsync(ct).Result ?? "";
            lock (Requests)
                Requests.Add((request.RequestUri!, body, request.Headers.Authorization is not null, request.Headers.Contains("Client-ID")));
            return respond(request, body);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Send(request, ct));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    private static IgdbCoverArtProvider RelayProvider(RecordingHandler handler) =>
        new(Relay) { HttpHandlerOverrideForTest = handler, BackoffDelayOverrideForTest = _ => { } };

    [Fact]
    public void InRelayMode_ARequestGoesToTheRelay_WithNoCredentialsAndNoTokenRequest()
    {
        var handler = new RecordingHandler((_, _) => Json("""[{"id":11,"name":"Foo"}]"""));

        var result = RelayProvider(handler).SearchByTitleForIdentity("Foo", CancellationToken.None);

        Assert.Equal(CatalogStatus.Match, result.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://relay.example.test/v4/games", request.Uri.AbsoluteUri);       // same path IGDB itself uses
        Assert.False(request.HadAuthorization);                                              // the relay attaches credentials, we send none
        Assert.False(request.HadClientId);
        Assert.Contains("Foo", request.Body);                                                // and the query itself is unchanged
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host == "id.twitch.tv");         // no token was ever requested
    }

    [Fact]
    public void InRelayMode_TheCoverImage_IsStillFetchedStraightFromIgdbsCdn_NotThroughTheRelay()
    {
        var handler = new RecordingHandler((request, _) => request.RequestUri!.Host == "relay.example.test"
            ? Json("""[{"image_id":"co1"}]""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Png(60, 90)) });

        var result = RelayProvider(handler).FetchCoverForId("7", "Foo", Dir(), CancellationToken.None);

        Assert.Equal(CoverLookupStatus.Resolved, result.Status);
        Assert.Contains(handler.Requests, r => r.Uri.Host == "relay.example.test" && r.Uri.AbsolutePath == "/v4/covers");
        Assert.Contains(handler.Requests, r => r.Uri.Host == "images.igdb.com");            // image bytes never cost the relay anything
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]         // the relay's own token is bad: an outage to us, nothing we can refresh
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void ARelayThatAnswersWithAnError_IsUnavailable_NeverAMissingGame_AndIsNotRetriedInAStorm(HttpStatusCode status)
    {
        var handler = new RecordingHandler((_, _) => Json("{}", status));

        Assert.Equal(CatalogStatus.Unavailable, RelayProvider(handler).SearchByTitleForIdentity("Foo", CancellationToken.None).Status);
        Assert.Single(handler.Requests);                                                    // in particular a 401 is not "refresh and retry"
    }

    [Fact]
    public void ABusyRelay_429WithRetryAfter_IsRetriedBoundedly_ThenUnavailable()
    {
        var handler = new RecordingHandler((_, _) =>
        {
            var response = Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return response;
        });

        Assert.Equal(CatalogStatus.Unavailable, RelayProvider(handler).SearchByTitleForIdentity("Foo", CancellationToken.None).Status);
        Assert.InRange(handler.Requests.Count, 2, 6);                                       // it honoured Retry-After, but gave up
    }

    [Fact]
    public void ARelayThatIsUnreachable_IsUnavailable_NotAnException()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("connection refused"));

        Assert.Equal(CatalogStatus.Unavailable, RelayProvider(handler).SearchByTitleForIdentity("Foo", CancellationToken.None).Status);
    }
}

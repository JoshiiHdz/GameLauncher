using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;

namespace GameLauncher.Tests.Identity;

/// <summary>Audit finding: plain HTTP would let anyone on the path substitute game identities, cross-references and cover ids, and HTTPS
/// image downloads do not authenticate the lookup that chose them. The relay is therefore reached over TLS and authenticated by a PINNED KEY.
/// These tests use the REAL pinned client (no handler override) against a real TLS server on loopback: the right key is served, and a wrong pin -
/// or an impostor that holds a different key, however valid its certificate looks - is refused before a single request is sent.</summary>
[Collection(IgdbStaticStateCollection.Name)]
public class RelayPinnedTransportTests : IDisposable
{
    private readonly List<TlsServer> _servers = new();

    public RelayPinnedTransportTests()
    {
        IgdbCoverArtProvider.ResetRateLimitForTest();
    }

    public void Dispose()
    {
        foreach (var server in _servers)
            server.Dispose();
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=test-relay", key, HashAlgorithmName.SHA256);
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return new X509Certificate2(selfSigned.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>A real TLS server that answers every POST with one IGDB-shaped game, counting the requests it actually receives.</summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private int _requests;

        public TlsServer(X509Certificate2 certificate)
        {
            _certificate = certificate;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }

        public int Port { get; }
        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { return; }

                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    });

                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await ssl.ReadAsync(one) == 0)
                            return;
                        head.Append((char)one[0]);
                    }

                    var length = 0;
                    foreach (var line in head.ToString().Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = int.Parse(line["Content-Length:".Length..].Trim());
                    }

                    var body = new byte[length];
                    for (var read = 0; read < length;)
                        read += await ssl.ReadAsync(body.AsMemory(read));

                    Interlocked.Increment(ref _requests);
                    var json = Encoding.UTF8.GetBytes("""[{"id":11,"name":"Foo"}]""");
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n"));
                    await ssl.WriteAsync(json);
                    await ssl.FlushAsync();
                }
                catch (Exception ex) when (ex is IOException or AuthenticationException or ObjectDisposedException or InvalidOperationException)
                {
                    // a client that refuses our certificate hangs up mid-handshake - exactly what several tests here expect
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }

    private TlsServer Serve(X509Certificate2 certificate)
    {
        var server = new TlsServer(certificate);
        _servers.Add(server);
        return server;
    }

    private static IgdbCoverArtProvider PinnedTo(TlsServer server, params string[] pins) =>
        new(new RelayEndpoint(new Uri($"https://127.0.0.1:{server.Port}"), pins)) { BackoffDelayOverrideForTest = _ => { } };

    // ---- the pin itself -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePin_IsTheSha256OfThePublicKey_AndOnlyThatKeyMatchesIt()
    {
        using var legit = NewCertificate();
        using var impostor = NewCertificate();
        var pin = RelayTransport.PinOf(legit);

        Assert.Matches(@"^sha256/[A-Za-z0-9+/]{43}=$", pin);
        Assert.Equal(pin, RelayTransport.PinOf(legit));                                  // stable
        Assert.NotEqual(pin, RelayTransport.PinOf(impostor));
        Assert.True(RelayTransport.PinMatches(legit, [pin]));
        Assert.True(RelayTransport.PinMatches(legit, ["sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", pin]));   // any of several pins (key rotation)
        Assert.False(RelayTransport.PinMatches(impostor, [pin]));
        Assert.False(RelayTransport.PinMatches(null, [pin]));
        Assert.False(RelayTransport.PinMatches(legit, []));
    }

    // ---- the real client against a real TLS server ------------------------------------------------------------------------------

    [Fact]
    public void WithTheRightPin_TheRelayAnswers_EvenThoughTheCertificateIsSelfSigned()
    {
        using var certificate = NewCertificate();
        var server = Serve(certificate);

        var result = PinnedTo(server, RelayTransport.PinOf(certificate)).SearchByTitleForIdentity("Foo", CancellationToken.None);

        Assert.Equal(CatalogStatus.Match, result.Status);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public void WithAWrongPin_NoRequestIsEverSent_AndTheLookupIsUnavailable_NeverAnAnswer()
    {
        using var certificate = NewCertificate();
        var server = Serve(certificate);

        var result = PinnedTo(server, "sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=").SearchByTitleForIdentity("Foo", CancellationToken.None);

        Assert.Equal(CatalogStatus.Unavailable, result.Status);
        Assert.Equal(0, server.Requests);                                                // the handshake failed first: nothing reached the server
    }

    [Fact]
    public void AnImpostorHoldingADifferentKey_CannotSubstituteAnswers_EvenWithAWellFormedCertificate()
    {
        using var legit = NewCertificate();
        using var impostor = NewCertificate();
        var attacker = Serve(impostor);                                                  // the interceptor answers on the relay's behalf

        var result = PinnedTo(attacker, RelayTransport.PinOf(legit)).SearchByTitleForIdentity("Foo", CancellationToken.None);

        Assert.Equal(CatalogStatus.Unavailable, result.Status);
        Assert.Equal(0, attacker.Requests);
    }

    [Fact]
    public void AnImpostor_AlsoCannotFeedCoverIds()
    {
        using var legit = NewCertificate();
        using var impostor = NewCertificate();
        var attacker = Serve(impostor);
        var provider = PinnedTo(attacker, RelayTransport.PinOf(legit));
        var cache = Path.Combine(Path.GetTempPath(), "GameLauncherTests-PinCache-" + Guid.NewGuid());
        try
        {
            var cover = provider.FetchCoverForId("7", "Foo", cache, CancellationToken.None);

            Assert.Equal(CoverLookupStatus.Unavailable, cover.Status);
            Assert.Equal(0, attacker.Requests);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, recursive: true);
        }
    }
}

using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GameLauncher.Services.CoverArt;

/// <summary>The HTTPS client used ONLY to talk to the project's relay. It accepts the server's certificate if - and only if - the SHA-256 of
/// its public key equals one of the pins built into the launcher. Certificate authorities, DNS names, expiry and chain errors are
/// deliberately not what it trusts: the key is. Anything that presents a different key (an interceptor, a hijacked DNS name, a stolen
/// address) fails the TLS handshake before a single request byte is sent, so a lookup can never be answered by someone else.
/// Nothing else in the launcher uses it: IGDB's image CDN and the direct user-credential path keep ordinary certificate validation.</summary>
internal static class RelayTransport
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();

    internal static HttpClient ClientFor(IReadOnlyList<string> pins) => Clients.GetOrAdd(string.Join(",", pins), _ => Create(pins));

    private static HttpClient Create(IReadOnlyList<string> pins)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => PinMatches(certificate, pins),
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>"sha256/&lt;base64&gt;" of a certificate's SubjectPublicKeyInfo - the same value igdb-relay-make-cert.sh prints.</summary>
    internal static string PinOf(X509Certificate certificate)
    {
        using var cert = new X509Certificate2(certificate);
        return "sha256/" + Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    internal static bool PinMatches(X509Certificate? certificate, IReadOnlyList<string> pins)
    {
        if (certificate is null)
            return false;

        var actual = Encoding.ASCII.GetBytes(PinOf(certificate));
        return pins.Any(p => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p), actual));
    }
}

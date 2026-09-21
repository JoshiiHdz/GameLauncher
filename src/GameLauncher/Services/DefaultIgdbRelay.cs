using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GameLauncher.Services;

/// <summary>Where the project's IGDB relay is and how to recognise it: an https address plus the SHA-256 pin(s) of the relay's public
/// KEY ("sha256/<base64>", the value igdb-relay-make-cert.sh prints). The launcher trusts the relay by that key alone - not a certificate
/// authority, not a DNS name - so a network attacker cannot substitute game identities, cross-references or cover ids. More than one pin
/// allows a key rotation without stranding builds in the field.</summary>
public sealed record RelayEndpoint(Uri Address, IReadOnlyList<string> Pins);

/// <summary>
/// The relay's address and pin, compiled into the build so a fresh install gets IGDB identification and covers with no setup and no
/// credentials. Supplied by an embedded "default-igdb-relay.txt" (two lines: the https address, then the pin(s), comma-separated) that is
/// gitignored, so it reaches the exe at build time and not source control. A build without that file simply has no built-in IGDB
/// access, and credentials entered in Settings always take precedence. (Release builds must not lack it - release.yml refuses to build
/// one; see installer/Check-RelayConfig.ps1.)
///
/// The relay holds the Twitch Client ID/Secret and token on ITS server; nothing secret is in the launcher, and neither the address nor
/// the pin is secret (every install has them). Images still come straight from images.igdb.com over ordinary HTTPS.
/// </summary>
public static class DefaultIgdbRelay
{
    public static RelayEndpoint? Current { get; } = Load();

    // A pattern string, NOT a static Regex field: `Current` below is initialised by Load() -> Parse(), and a static field declared after it
    // would still be null at that moment (type-initializer order is textual), crashing every build that embeds an address file.
    private const string PinPattern = @"^sha256/[A-Za-z0-9+/]{43}=$";

    /// <summary>Two lines: an absolute https address with a host and nothing else (no credentials, path, query or fragment), then one to
    /// three pins of the exact shape sha256/&lt;44-char base64&gt;. Anything else - including plain http, or a missing pin - is "no
    /// built-in access", never a guess and never an unauthenticated relay.</summary>
    internal static RelayEndpoint? Parse(string? text)
    {
        var lines = (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count != 2 || !Uri.TryCreate(lines[0], UriKind.Absolute, out var uri))
            return null;

        var bareRoot = uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 || !bareRoot)
            return null;

        var pins = lines[1].Split(',').Select(p => p.Trim()).ToList();
        if (pins.Count is < 1 or > 3 || pins.Any(p => !Regex.IsMatch(p, PinPattern)) || pins.Distinct().Count() != pins.Count)
            return null;

        return new RelayEndpoint(new Uri(uri.GetLeftPart(UriPartial.Authority)), pins);
    }

    private static RelayEndpoint? Load()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("default-igdb-relay.txt", StringComparison.OrdinalIgnoreCase));

            if (name is null)
                return null;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
                return null;

            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or NotSupportedException)
        {
            Logger.Warn("Couldn't read the built-in IGDB relay address.", ex);
            return null;
        }
    }
}

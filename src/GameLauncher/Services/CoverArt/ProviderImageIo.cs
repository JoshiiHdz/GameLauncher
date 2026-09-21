using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace GameLauncher.Services.CoverArt;

/// <summary>The bounded image and cache-file I/O every automatic cover-art provider shares (IGDB, SteamGridDB,
/// Steam CDN). Extracted from IgdbCoverArtProvider's audited implementation, unchanged in contract, so the
/// three providers cannot drift apart in what "a bounded download" or "a bounded cache read" means - the
/// providers used to differ: IGDB bounded its download while SteamGridDB and Steam CDN called
/// HttpClient.GetByteArrayAsync (an unbounded read into memory with no time budget once headers arrive). The
/// request deadline, caller cancellation and JSON body cap live in ProviderHttp, which this builds on.</summary>
internal static class ProviderImageIo
{
    internal static readonly TimeSpan DefaultDownloadTimeout = ProviderHttp.DefaultRequestTimeout;

    /// <summary>Downloads `url` under a hard byte cap (ArtworkImageValidator.MaxFileBytes), a hard time budget
    /// AND the caller's cancellation, all enforced DURING the read via cancellable ReadAsync calls - not a
    /// synchronous stream.Read(), which blocks until data arrives or the connection is torn down with no way to
    /// interrupt it if a server sends headers and then stops sending bytes. Microsoft's own documentation
    /// notes HttpClient.Timeout does not reliably bound this phase once ResponseHeadersRead is used, so this
    /// owns its own timeout (ProviderHttp.Send).
    ///
    /// Return/throw contract (it matters - see CoverLookupStatus): null means "no usable image" (a 404, or
    /// over the byte cap); a stall, timeout, or any other HTTP failure THROWS HttpRequestException, because
    /// it says nothing about whether the cover exists; the CALLER's cancellation propagates as
    /// OperationCanceledException. `providerLabel` is for logs only.</summary>
    internal static byte[]? FetchBoundedImageBytes(HttpClient http, string url, string providerLabel,
        TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return ProviderHttp.Send(http, request, providerLabel + " cover image", timeout, ct, (response, token) =>
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Logger.Warn($"{providerLabel}: cover image is missing on the CDN (404).");
                return null;
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{providerLabel} cover image request returned {(int)response.StatusCode} {response.StatusCode}.");

            using var stream = response.Content.ReadAsStream(token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.ReadAsync(chunk, 0, chunk.Length, token).GetAwaiter().GetResult()) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > ArtworkImageValidator.MaxFileBytes)
                {
                    Logger.Warn($"{providerLabel}: cover image exceeded the {ArtworkImageValidator.MaxFileBytes}-byte cap during download - aborting.");
                    return null;
                }
            }

            return buffer.ToArray();
        });
    }

    /// <summary>Reads a cached image file under the same hard byte cap, enforced against the bytes actually
    /// read (not a FileInfo.Length check a lying or concurrently-changing file could defeat) - the cache hit
    /// path used to call File.ReadAllBytes, which loads an arbitrarily large file into memory before any
    /// validation could reject it. Returns null when the file exceeds the cap (the caller treats that as an
    /// invalid cache entry: delete and re-fetch). IO errors propagate to the caller's own handling.</summary>
    internal static byte[]? ReadBoundedCacheFile(string path, string providerLabel) =>
        ReadBoundedFile(path, ArtworkImageValidator.MaxFileBytes, providerLabel);

    /// <summary>Reads a cache SIDECAR (`.meta.json`) as text under ProviderHttp.MaxSidecarBytes - a sidecar is
    /// roughly 250 bytes, and File.ReadAllText would load a file of any size (and any garbage) into memory
    /// before JSON parsing could reject it. Returns null when the file exceeds the cap; the caller treats that
    /// exactly like a corrupt sidecar (unverifiable evidence: delete both files and re-fetch). IO errors
    /// propagate to the caller's own handling.</summary>
    internal static string? ReadBoundedSidecarText(string path, string providerLabel)
    {
        var bytes = ReadBoundedFile(path, ProviderHttp.MaxSidecarBytes, providerLabel + " sidecar");
        if (bytes is null)
            return null;

        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static byte[]? ReadBoundedFile(string path, int maxBytes, string label)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                Logger.Warn($"{label}: cached file exceeds the {maxBytes}-byte cap while reading.");
                return null;
            }
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

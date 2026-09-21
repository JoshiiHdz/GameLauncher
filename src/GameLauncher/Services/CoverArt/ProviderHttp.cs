using System.IO;
using System.Net.Http;
using System.Text;

namespace GameLauncher.Services.CoverArt;

/// <summary>The one HTTP exchange every automatic cover-art provider (IGDB, SteamGridDB, Steam CDN) goes through,
/// for JSON API calls and image downloads alike: a hard REQUEST DEADLINE, the CALLER's cancellation, and a
/// hard BYTE CAP on whatever body is read - all enforced DURING the exchange, not around it.
///
/// Why it exists: HttpClient.Send(request) buffers the whole body (ResponseContentRead) under an
/// HttpClient.Timeout that covers it, but with a 2 GB buffer limit - so a server could make a provider hold
/// gigabytes of "JSON" in memory - and it took no caller cancellation at all on SteamGridDB and Steam CDN, so a
/// superseded scan could sit in a stalled request for the full timeout. Here the headers are read first
/// (ResponseHeadersRead), the body is read in cancellable chunks under a cap, and one linked token carries both
/// the deadline and the caller's cancellation.
///
/// The contract that matters (see CoverLookupStatus): the CALLER's cancellation propagates as
/// OperationCanceledException and is never converted into a lookup outcome; a deadline overrun, a stall, an
/// oversized body or any other failure throws (HttpRequestException / InvalidDataException), which every
/// provider boundary reports as Unavailable - none of them says anything about whether the game exists.</summary>
internal static class ProviderHttp
{
    /// <summary>Time budget for one request (headers AND body). Providers make several sequential requests
    /// per game; each gets its own budget.</summary>
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Cap for a JSON API response body. Real bodies are a few hundred bytes to a few tens of KB (a
    /// search of at most 20-ish candidates, a cover/grid listing); 2 MiB is orders of magnitude of headroom
    /// while still bounding memory. Over the cap is unreadable, not "empty" - it throws.</summary>
    internal const int MaxJsonResponseBytes = 2 * 1024 * 1024;

    /// <summary>Cap for an OAuth token response, which is a few hundred bytes.</summary>
    internal const int MaxTokenResponseBytes = 64 * 1024;

    /// <summary>Cap for a cache sidecar (`.meta.json`), which is roughly 250 bytes.</summary>
    internal const int MaxSidecarBytes = 16 * 1024;

    /// <summary>Sends `request` and runs `handle` on the response inside ONE deadline-and-cancellation scope.
    /// `handle` receives the response as soon as its headers arrive and the linked token, and reads the body
    /// (if it wants it) via ReadBoundedText / its own capped read - so a stall in the body is bounded by the
    /// same deadline as a stall before the headers. `handle` runs before the response is disposed; anything
    /// it needs afterwards it must copy out.
    ///
    /// Throws: OperationCanceledException when the CALLER's token was cancelled; HttpRequestException when the
    /// deadline expired (a stall says nothing about the catalog); whatever `handle` throws otherwise.</summary>
    internal static T Send<T>(HttpClient http, HttpRequestMessage request, string providerLabel, TimeSpan timeout,
        CancellationToken ct, Func<HttpResponseMessage, CancellationToken, T> handle)
    {
        ct.ThrowIfCancellationRequested();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            return handle(response, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new HttpRequestException($"{providerLabel} request stalled or exceeded its {timeout.TotalSeconds:0.##}s time budget.");
        }
    }

    /// <summary>Reads a response body as text under a hard byte cap enforced against the bytes actually read
    /// (a declared Content-Length over the cap is refused before reading anything, but is not trusted when it
    /// is under it). Over the cap throws InvalidDataException - an oversized "JSON" body is unreadable, not
    /// an empty result. UTF-8, honouring a byte-order mark, as StreamReader did before.</summary>
    internal static string ReadBoundedText(HttpResponseMessage response, string providerLabel, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
            throw new InvalidDataException($"{providerLabel} response declares {declared} bytes, over the {maxBytes}-byte cap.");

        using var stream = response.Content.ReadAsStream(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.ReadAsync(chunk, 0, chunk.Length, ct).GetAwaiter().GetResult()) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"{providerLabel} response exceeded the {maxBytes}-byte cap while reading.");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

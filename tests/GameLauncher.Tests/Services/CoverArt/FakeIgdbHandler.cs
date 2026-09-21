using System.IO;
using System.Net;
using System.Net.Http;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>A real HttpMessageHandler standing in for id.twitch.tv, api.igdb.com and images.igdb.com, so
/// transport-level behavior (a stalled body after headers, a 401, a 429 with Retry-After, cancellation
/// arriving mid-request) is exercised through the real HttpClient pipeline without any external server.
/// Handed to an IgdbCoverArtProvider INSTANCE (HttpHandlerOverrideForTest) - never installed globally, so
/// it can't leak into another test's provider.
///
/// Both Send and SendAsync are implemented: IgdbCoverArtProvider calls the synchronous HttpClient.Send,
/// and HttpMessageHandler's own Send throws NotSupportedException unless overridden.</summary>
internal sealed class FakeIgdbHandler : HttpMessageHandler
{
    /// <summary>Token endpoint. Default: a valid token response.</summary>
    public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? OnToken { get; init; }

    /// <summary>api.igdb.com (/games and /covers). Receives the request and the 1-based number of API calls
    /// seen so far (this one included). Default: /games returns [], /covers returns one image_id.</summary>
    public Func<HttpRequestMessage, int, CancellationToken, HttpResponseMessage>? OnApi { get; init; }

    /// <summary>images.igdb.com. Default: 404.</summary>
    public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? OnImage { get; init; }

    private int _tokenCalls;
    private int _apiCalls;
    private int _imageCalls;

    public int TokenCalls => Volatile.Read(ref _tokenCalls);
    public int ApiCalls => Volatile.Read(ref _apiCalls);
    public int ImageCalls => Volatile.Read(ref _imageCalls);

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var host = request.RequestUri!.Host;

        if (host == "id.twitch.tv")
        {
            Interlocked.Increment(ref _tokenCalls);
            return OnToken?.Invoke(request, cancellationToken)
                ?? Json("""{"access_token":"fake-token","expires_in":5000000}""");
        }

        if (host == "api.igdb.com")
        {
            var callNumber = Interlocked.Increment(ref _apiCalls);
            if (OnApi is not null)
                return OnApi(request, callNumber, cancellationToken);

            return request.RequestUri.AbsolutePath.EndsWith("/covers", StringComparison.Ordinal)
                ? Json("""[{"image_id":"fake-image"}]""")
                : Json("[]");
        }

        Interlocked.Increment(ref _imageCalls);
        return OnImage?.Invoke(request, cancellationToken) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));

    /// <summary>Blocks until `ct` is cancelled, then throws - a request that "never completes on its own".
    /// Only cancellation ends it, so a test using it proves the token really reached this call.</summary>
    public static void BlockUntilCancelled(CancellationToken ct)
    {
        ct.WaitHandle.WaitOne();
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Real headers immediately, then a body whose ReadAsync never completes on its own - "the
    /// server sends headers and then simply stops sending bytes". Only cancellation ends a read.</summary>
    public static HttpResponseMessage StallingBody() => new(HttpStatusCode.OK) { Content = new StreamContent(new NeverEndingStream()) };

    /// <summary>Real headers immediately, then a body that never ends and never stalls - bytes forever.</summary>
    public static HttpResponseMessage EndlessBody() => new(HttpStatusCode.OK) { Content = new StreamContent(new EverFlowingStream()) };

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Only ReadAsync is expected.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0; // unreachable - only ever returns via cancellation
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class EverFlowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Array.Fill(buffer, (byte)1, offset, count);
            return Task.FromResult(count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill(1);
            return new ValueTask<int>(buffer.Length);
        }
    }
}

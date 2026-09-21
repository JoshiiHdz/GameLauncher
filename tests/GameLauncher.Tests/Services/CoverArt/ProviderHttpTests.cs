using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>The shared exchange (ProviderHttp) and the bounded sidecar read (ProviderImageIo.ReadBoundedSidecarText)
/// pinned at the unit that owns them. Provider-level tests can only see outcomes; here the deadline, the caller's
/// cancellation and the byte cap are asserted directly, including the ways they must NOT be confused: a deadline is
/// an HttpRequestException (an outage-shaped failure), the caller's cancellation is an OperationCanceledException
/// (never a lookup outcome), and an oversized body is InvalidDataException (unreadable, not empty).</summary>
public class ProviderHttpTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var f in _files.Where(File.Exists))
            File.Delete(f);
    }

    private static HttpClient Client(FuncHttpHandler handler) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpRequestMessage Request() => new(HttpMethod.Get, "https://example.test/resource");

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    // ---- Send: deadline vs cancellation ----------------------------------------------------------------------

    [Fact]
    public void Send_AlreadyCancelled_ThrowsBeforeAnyRequestIsMade()
    {
        var handler = new FuncHttpHandler((_, _) => Text("x"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ProviderHttp.Send(Client(handler), Request(), "test", TimeSpan.FromSeconds(5), cts.Token, (r, _) => 1));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Send_TheCallersCancellation_WhileWaitingForHeaders_PropagatesAsCancellation_NotAsATimeout()
    {
        var handler = new FuncHttpHandler((_, ct) => { FakeIgdbHandler.BlockUntilCancelled(ct); return Text("unreachable"); });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var sw = Stopwatch.StartNew();
        var ex = Record.Exception(() =>
            ProviderHttp.Send(Client(handler), Request(), "test", TimeSpan.FromSeconds(30), cts.Token, (r, _) => 1));
        sw.Stop();

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<HttpRequestException>(ex);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"cancellation must end the wait promptly, took {sw.Elapsed}");
    }

    [Fact]
    public void Send_TheDeadline_WhileWaitingForHeaders_IsAnHttpRequestException_NotACancellation()
    {
        var handler = new FuncHttpHandler((_, ct) => { FakeIgdbHandler.BlockUntilCancelled(ct); return Text("unreachable"); });

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<HttpRequestException>(() =>
            ProviderHttp.Send(Client(handler), Request(), "test", TimeSpan.FromMilliseconds(200), CancellationToken.None, (r, _) => 1));
        sw.Stop();

        Assert.Contains("time budget", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the deadline must end the wait, took {sw.Elapsed}");
    }

    [Fact]
    public void Send_TheDeadline_WhileReadingAStalledBody_IsAnHttpRequestException()
    {
        // Headers arrive at once; the body then never delivers. HttpClient.Timeout does not bound this phase once
        // ResponseHeadersRead is used - the deadline here does.
        var handler = new FuncHttpHandler((_, _) => FakeIgdbHandler.StallingBody());

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<HttpRequestException>(() =>
            ProviderHttp.Send(Client(handler), Request(), "test", TimeSpan.FromMilliseconds(250), CancellationToken.None,
                (r, token) => ProviderHttp.ReadBoundedText(r, "test", 1000, token)));
        sw.Stop();

        Assert.Contains("time budget", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Send_TheCallersCancellation_WhileReadingAStalledBody_PropagatesAsCancellation()
    {
        var handler = new FuncHttpHandler((_, _) => FakeIgdbHandler.StallingBody());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var ex = Record.Exception(() =>
            ProviderHttp.Send(Client(handler), Request(), "test", TimeSpan.FromSeconds(30), cts.Token,
                (r, token) => ProviderHttp.ReadBoundedText(r, "test", 1000, token)));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<HttpRequestException>(ex);
    }

    [Fact]
    public void Send_WithinTheDeadline_ReturnsWhatTheHandlerProduced()
    {
        var handler = new FuncHttpHandler((_, _) => Text("hello"));

        var body = ProviderHttp.Send(Client(handler), Request(), "test", TimeSpan.FromSeconds(5), CancellationToken.None,
            (r, token) => ProviderHttp.ReadBoundedText(r, "test", 1000, token));

        Assert.Equal("hello", body);
    }

    // ---- ReadBoundedText: the byte cap ------------------------------------------------------------------------

    private static string Read(HttpResponseMessage response, int cap) =>
        ProviderHttp.ReadBoundedText(response, "test", cap, CancellationToken.None);

    [Fact]
    public void ReadBoundedText_ExactlyAtTheCap_IsReturnedWhole()
    {
        var body = new string('a', 1000);

        Assert.Equal(body, Read(Text(body), 1000));
    }

    [Fact]
    public void ReadBoundedText_OneByteOverTheCap_IsUnreadable_NotTruncated()
    {
        Assert.Throws<InvalidDataException>(() => Read(Text(new string('a', 1001)), 1000));
    }

    [Fact]
    public void ReadBoundedText_TheRealJsonCap_AcceptsABodyAtIt_AndRefusesOneByteMore()
    {
        Assert.Equal(ProviderHttp.MaxJsonResponseBytes, Read(Text(new string('a', ProviderHttp.MaxJsonResponseBytes)), ProviderHttp.MaxJsonResponseBytes).Length);
        Assert.Throws<InvalidDataException>(() => Read(Text(new string('a', ProviderHttp.MaxJsonResponseBytes + 1)), ProviderHttp.MaxJsonResponseBytes));
    }

    [Fact]
    public void ReadBoundedText_NoDeclaredLength_ExactlyAtTheCap_IsReturnedWhole()
    {
        // A chunked body declares no length, so ONLY the streaming cap applies (the declared-length check cannot mask it).
        var body = new string('a', 1000);

        Assert.Equal(body, Read(UnknownLengthBody.Response(body), 1000));
    }

    [Fact]
    public void ReadBoundedText_NoDeclaredLength_OneByteOverTheCap_IsUnreadable_NotTruncated()
    {
        Assert.Throws<InvalidDataException>(() => Read(UnknownLengthBody.Response(new string('a', 1001)), 1000));
    }

    [Fact]
    public void ReadBoundedText_NoDeclaredLength_TheRealJsonCap_AcceptsABodyAtIt_AndRefusesOneByteMore()
    {
        Assert.Equal(ProviderHttp.MaxJsonResponseBytes,
            Read(UnknownLengthBody.Response(new string('a', ProviderHttp.MaxJsonResponseBytes)), ProviderHttp.MaxJsonResponseBytes).Length);
        Assert.Throws<InvalidDataException>(() =>
            Read(UnknownLengthBody.Response(new string('a', ProviderHttp.MaxJsonResponseBytes + 1)), ProviderHttp.MaxJsonResponseBytes));
    }

    [Fact]
    public void ReadBoundedText_ADeclaredLengthOverTheCap_IsRefusedWithoutReadingAnything()
    {
        var stream = new ThrowingOnReadStream();
        var content = new StreamContent(stream);
        content.Headers.ContentLength = 5000;
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        Assert.Throws<InvalidDataException>(() => Read(response, 1000));
        Assert.False(stream.WasRead, "an over-cap declared length must be refused before the body is touched");
    }

    [Fact]
    public void ReadBoundedText_ALyingUnderstatedLength_IsStillCappedOnTheBytesActuallyRead()
    {
        // Content-Length says 10, the body never ends: the declared length is not trusted when it is under the cap.
        using var response = FakeIgdbHandler.EndlessBody();
        response.Content.Headers.ContentLength = 10;

        var sw = Stopwatch.StartNew();
        Assert.Throws<InvalidDataException>(() => Read(response, 100_000));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the cap should end this promptly, took {sw.Elapsed}");
    }

    [Fact]
    public void ReadBoundedText_AnEndlessBody_IsCapped()
    {
        using var response = FakeIgdbHandler.EndlessBody();

        Assert.Throws<InvalidDataException>(() => Read(response, ProviderHttp.MaxJsonResponseBytes));
    }

    [Fact]
    public void ReadBoundedText_DecodesUtf8_AndSkipsAByteOrderMark()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"name\":\"Pokémon ✓\"}")).ToArray();
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(withBom) };

        Assert.Equal("{\"name\":\"Pokémon ✓\"}", Read(response, 1000));
    }

    // ---- ReadBoundedSidecarText -----------------------------------------------------------------------------------

    private string TempFile(string? text = null, long? length = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Sidecar-" + Guid.NewGuid());
        _files.Add(path);
        if (text is not null)
            File.WriteAllText(path, text);
        else
        {
            using var f = File.Create(path);
            f.SetLength(length ?? 0);
        }
        return path;
    }

    [Fact]
    public void ReadBoundedSidecarText_AnOrdinarySidecar_RoundTrips()
    {
        const string json = """{"Id":11,"Title":"Test Game","SearchedName":"Test Game","ImageSha256":"AB"}""";

        Assert.Equal(json, ProviderImageIo.ReadBoundedSidecarText(TempFile(json), "test"));
    }

    [Fact]
    public void ReadBoundedSidecarText_ExactlyAtTheCap_IsReturned()
    {
        var text = ProviderImageIo.ReadBoundedSidecarText(TempFile(new string(' ', ProviderHttp.MaxSidecarBytes)), "test");

        Assert.NotNull(text);
        Assert.Equal(ProviderHttp.MaxSidecarBytes, text!.Length);
    }

    [Fact]
    public void ReadBoundedSidecarText_OneByteOverTheCap_IsNull()
    {
        Assert.Null(ProviderImageIo.ReadBoundedSidecarText(TempFile(new string(' ', ProviderHttp.MaxSidecarBytes + 1)), "test"));
    }

    [Fact]
    public void ReadBoundedSidecarText_AFileTooLargeToEverLoadIntoMemory_IsNull_NotAnException()
    {
        // File.ReadAllText cannot read a file this large (it throws); the bounded read treats it as unverifiable.
        // SetLength writes no data.
        Assert.Null(ProviderImageIo.ReadBoundedSidecarText(TempFile(length: int.MaxValue + 1L), "test"));
    }

    [Fact]
    public void ReadBoundedSidecarText_AMissingFile_ThrowsForTheCallersOwnHandling()
    {
        Assert.ThrowsAny<IOException>(() => ProviderImageIo.ReadBoundedSidecarText(
            Path.Combine(Path.GetTempPath(), "GameLauncherTests-missing-" + Guid.NewGuid()), "test"));
    }

    private sealed class ThrowingOnReadStream : Stream
    {
        public bool WasRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { WasRead = true; throw new InvalidOperationException("must not be read"); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { WasRead = true; throw new InvalidOperationException("must not be read"); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { WasRead = true; throw new InvalidOperationException("must not be read"); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

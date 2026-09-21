using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>The shared bounded I/O (ProviderImageIo) pinned directly. Provider-level tests can only see its
/// OUTCOME - and for an oversized cache file the outcome is the same whether or not the file was loaded into
/// memory first (ArtworkImageValidator rejects it either way) - so the bound itself is asserted here, at the
/// unit that owns it.</summary>
public class ProviderImageIoTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var f in _files.Where(File.Exists))
            File.Delete(f);
    }

    private string TempFile(byte[]? content = null, long? length = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Io-" + Guid.NewGuid());
        _files.Add(path);
        if (content is not null)
            File.WriteAllBytes(path, content);
        else
        {
            using var f = File.Create(path);
            f.SetLength(length ?? 0);
        }
        return path;
    }

    // ---- ReadBoundedCacheFile ----------------------------------------------------------------------------

    [Fact]
    public void ReadBoundedCacheFile_ExactlyAtTheCap_IsReturnedWhole()
    {
        var path = TempFile(length: ArtworkImageValidator.MaxFileBytes);

        var bytes = ProviderImageIo.ReadBoundedCacheFile(path, "test");

        Assert.NotNull(bytes);
        Assert.Equal(ArtworkImageValidator.MaxFileBytes, bytes!.Length);
    }

    [Fact]
    public void ReadBoundedCacheFile_OneByteOverTheCap_IsRejected_NotReturned()
    {
        var path = TempFile(length: ArtworkImageValidator.MaxFileBytes + 1L);

        Assert.Null(ProviderImageIo.ReadBoundedCacheFile(path, "test"));
    }

    [Fact]
    public void ReadBoundedCacheFile_AnOrdinaryFile_RoundTrips()
    {
        var content = TestImages.Png();

        Assert.Equal(content, ProviderImageIo.ReadBoundedCacheFile(TempFile(content), "test"));
    }

    [Fact]
    public void ReadBoundedCacheFile_AMissingFile_ThrowsForTheCallersOwnHandling()
    {
        Assert.ThrowsAny<IOException>(() => ProviderImageIo.ReadBoundedCacheFile(Path.Combine(Path.GetTempPath(), "GameLauncherTests-missing-" + Guid.NewGuid()), "test"));
    }

    // ---- FetchBoundedImageBytes: the contract every provider relies on -----------------------------------

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) =>
        new(new FuncHttpHandler(respond), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(8) };

    private static byte[]? Fetch(HttpClient http, TimeSpan? timeout = null, CancellationToken ct = default) =>
        ProviderImageIo.FetchBoundedImageBytes(http, "https://example.test/cover.png", "test", timeout ?? TimeSpan.FromSeconds(8), ct);

    [Fact]
    public void Fetch_ASuccessfulBody_IsReturned()
    {
        var png = TestImages.Png();
        using var http = Client((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });

        Assert.Equal(png, Fetch(http));
    }

    [Fact]
    public void Fetch_A404_IsNull_NoImage_NotAnError()
    {
        using var http = Client((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(Fetch(http));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void Fetch_AnyOtherFailureStatus_Throws_BecauseItSaysNothingAboutWhetherTheCoverExists(HttpStatusCode code)
    {
        using var http = Client((_, _) => new HttpResponseMessage(code));

        Assert.Throws<HttpRequestException>(() => Fetch(http));
    }

    [Fact]
    public void Fetch_AnEndlessBody_IsCapped_ReturningNull()
    {
        using var http = Client((_, _) => FakeIgdbHandler.EndlessBody());

        var sw = Stopwatch.StartNew();
        var result = Fetch(http);
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Fetch_AStalledBody_ThrowsHttpRequestException_WithinItsOwnBudget()
    {
        using var http = Client((_, _) => FakeIgdbHandler.StallingBody());

        var sw = Stopwatch.StartNew();
        Assert.Throws<HttpRequestException>(() => Fetch(http, TimeSpan.FromMilliseconds(300)));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Fetch_TheCallersOwnCancellation_PropagatesAsCancellation_NotAsAFailure()
    {
        using var http = Client((_, ct) => { FakeIgdbHandler.BlockUntilCancelled(ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => Fetch(http, ct: cts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
    }
}

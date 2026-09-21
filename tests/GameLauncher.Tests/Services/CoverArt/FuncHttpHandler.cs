using System.Net.Http;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>A real HttpMessageHandler driven by a delegate, for the SteamGridDB and Steam CDN transport-level
/// tests (FakeIgdbHandler is specific to IGDB's three hosts). Records every requested URL so a test can
/// assert what was and was not requested. Handed to ONE provider INSTANCE (HttpHandlerOverrideForTest),
/// never installed globally.
///
/// Both Send and SendAsync are implemented: the providers call the synchronous HttpClient.Send, and
/// HttpMessageHandler's own Send throws NotSupportedException unless overridden.</summary>
internal sealed class FuncHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<string> _urls = new();
    private readonly object _gate = new();

    public IReadOnlyList<string> Urls
    {
        get { lock (_gate) return _urls.ToList(); }
    }

    public int Calls
    {
        get { lock (_gate) return _urls.Count; }
    }

    public int CallsMatching(string urlFragment)
    {
        lock (_gate) return _urls.Count(u => u.Contains(urlFragment, StringComparison.Ordinal));
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            _urls.Add(request.RequestUri!.ToString());
        return respond(request, cancellationToken);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));
}

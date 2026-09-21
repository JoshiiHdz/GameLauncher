using System.IO;
using System.Net;
using System.Net.Http;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>A response whose body has NO declared Content-Length - what a chunked-transfer server sends, and so the
/// realistic shape of a JSON API response. HttpContent computes the length of a seekable stream (StringContent,
/// ByteArrayContent and StreamContent-over-MemoryStream all declare one), and ProviderHttp.ReadBoundedText refuses a
/// declared length over its cap BEFORE reading - which masks the streaming cap behind it. A test of the streaming cap
/// itself needs a finite body that declares nothing.</summary>
internal static class UnknownLengthBody
{
    internal static HttpResponseMessage Response(byte[] body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ForwardOnlyStream(body)) };
        if (response.Content.Headers.ContentLength is not null)
            throw new InvalidOperationException("the fixture is meant to declare no length");
        return response;
    }

    internal static HttpResponseMessage Response(string body) => Response(System.Text.Encoding.UTF8.GetBytes(body));

    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

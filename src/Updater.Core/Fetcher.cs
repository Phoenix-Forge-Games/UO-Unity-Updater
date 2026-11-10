using System.Net;

namespace Updater.Core;

/// <summary>Reads http(s) and file:// URLs, with retries for transient network errors.</summary>
public sealed class Fetcher(HttpClient http)
{
    public int Retries { get; init; } = 4;

    public static HttpClient CreateHttpClient(string userAgent)
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return h;
    }

    public async Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken ct)
    {
        return await WithRetry(uri, async () =>
        {
            await using var s = await OpenAsync(uri, ct);
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > maxBytes) throw new UpdaterException($"{uri} is larger than expected.");
            }
            return ms.ToArray();
        }, ct);
    }

    /// <summary>Runs <paramref name="consume"/> on a fresh stream; retried from scratch on network failure.</summary>
    public Task<T> WithStreamAsync<T>(Uri uri, Func<Stream, Task<T>> consume, CancellationToken ct) =>
        WithRetry(uri, async () =>
        {
            await using var s = await OpenAsync(uri, ct);
            return await consume(s);
        }, ct);

    private async Task<Stream> OpenAsync(Uri uri, CancellationToken ct)
    {
        if (uri.IsFile)
        {
            if (!File.Exists(uri.LocalPath)) throw new NotFoundException(uri);
            return new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        }
        var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) { resp.Dispose(); throw new NotFoundException(uri); }
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            resp.Dispose();
            throw code >= 500 || code == 429 ? new HttpRequestException($"HTTP {code} from {uri}") : new UpdaterException($"HTTP {code} from {uri}");
        }
        return new ResponseStream(resp, await resp.Content.ReadAsStreamAsync(ct));
    }

    private async Task<T> WithRetry<T>(Uri uri, Func<Task<T>> op, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await op(); }
            catch (Exception ex) when (attempt < Retries && !ct.IsCancellationRequested &&
                                       ex is HttpRequestException or IOException or TaskCanceledException && ex is not NotFoundException)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new UpdaterException($"Could not download {uri}: {ex.Message}", ex);
            }
        }
    }

    private sealed class ResponseStream(HttpResponseMessage resp, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); resp.Dispose(); } base.Dispose(disposing); }
    }
}

public sealed class NotFoundException(Uri uri) : UpdaterException($"Not found: {uri}");

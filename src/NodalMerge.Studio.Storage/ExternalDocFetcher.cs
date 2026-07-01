using System.IO.Compression;
using System.Net;
using System.Text;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class ExternalDocFetcher : IExternalDocFetcher
{
    private readonly HttpClient _httpClient;

    public ExternalDocFetcher()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.Deflate | DecompressionMethods.GZip,
            UseCookies = false,
            UseDefaultCredentials = false,
            PreAuthenticate = false,
            Credentials = null,
        };

        _httpClient = new HttpClient(handler, disposeHandler: true);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("NodalMerge-Studio-DocFetch/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/plain, text/html, application/json;q=0.9, */*;q=0.1");
    }

    public async Task<ExternalDocFetchContent> FetchAsync(
        Uri normalizedUrl,
        int maxBytes,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be greater than zero.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, normalizedUrl);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);

        var targetBytes = await ReadBoundedAsync(stream, maxBytes, linked.Token).ConfigureAwait(false);
        var encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);
        var snapshot = encoding.GetString(targetBytes.Content, 0, targetBytes.Content.Length);

        return new ExternalDocFetchContent(
            contentType,
            snapshot,
            targetBytes.Truncated,
            targetBytes.Content.Length);
    }

    private static async Task<(byte[] Content, bool Truncated)> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream(capacity: Math.Min(maxBytes, 8 * 1024));
        var buffer = new byte[8 * 1024];
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0)
                break;

            var remaining = maxBytes - (int)ms.Length;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var toWrite = Math.Min(remaining, read);
            await ms.WriteAsync(buffer.AsMemory(0, toWrite), ct).ConfigureAwait(false);
            if (toWrite < read)
            {
                truncated = true;
                break;
            }
        }

        return (ms.ToArray(), truncated);
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using XivMate.DataGathering.Forays.Dalamud.Core;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

/// <summary>
/// The collector overlay's own HTTP client for the public coverage files. Anonymous: no API key, no cookies, no
/// redirects, nothing in the request but the file name and the cache validators. Separate from <see cref="ApiService"/>.
/// </summary>
internal sealed class HttpCoverageFetcher : ICoverageFetcher, IDisposable
{
    private readonly HttpClient client;

    public HttpCoverageFetcher(string version)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli | DecompressionMethods.Deflate
        };
        client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = CoverageParser.MaxBytes
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XivForays/" + version);
    }

    public async Task<CoverageResponse> Get(string url, string? etag, string? lastModified, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(etag) && EntityTagHeaderValue.TryParse(etag, out var tag))
            request.Headers.IfNoneMatch.Add(tag);
        else if (!string.IsNullOrEmpty(lastModified) && DateTimeOffset.TryParse(lastModified, out var since))
            request.Headers.IfModifiedSince = since;

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var newTag = response.Headers.ETag?.ToString();
            var modified = response.Content.Headers.LastModified?.ToString("R");
            if (status != 200)
                return new CoverageResponse(status, null, newTag, modified);
            if (response.Content.Headers.ContentLength is > CoverageParser.MaxBytes)
                return new CoverageResponse(413, null, null, null);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new CoverageResponse(status, body, newTag, modified);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CoverageResponse(0, null, null, null);
        }
        catch (HttpRequestException)
        {
            return new CoverageResponse(0, null, null, null);
        }
    }

    public void Dispose() => client.Dispose();
}

/// <summary>The last good coverage file per zone, in the plugin's own folder (public data and its validators only).</summary>
internal sealed class FileCoverageStore(string directory) : ICoverageStore
{
    public (byte[] Body, string? ETag, string? LastModified)? Load(string zone)
    {
        var body = Path.Combine(directory, "coverage-" + zone + ".json");
        if (!File.Exists(body))
            return null;
        var meta = Path.Combine(directory, "coverage-" + zone + ".meta");
        string? etag = null, modified = null;
        if (File.Exists(meta))
        {
            var lines = File.ReadAllLines(meta);
            etag = lines.Length > 0 && lines[0].Length > 0 ? lines[0] : null;
            modified = lines.Length > 1 && lines[1].Length > 0 ? lines[1] : null;
        }

        return (File.ReadAllBytes(body), etag, modified);
    }

    public void Save(string zone, byte[] body, string? etag, string? lastModified)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "coverage-" + zone + ".json");
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, body);
        File.Move(temp, path, true);
        File.WriteAllLines(Path.Combine(directory, "coverage-" + zone + ".meta"), [etag ?? "", lastModified ?? ""]);
    }
}

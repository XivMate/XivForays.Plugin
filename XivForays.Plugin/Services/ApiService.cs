using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

/// <summary>
/// HTTP client for the ingest. Never logs the API key, headers or request bodies; callers only see an
/// <see cref="UploadOutcome"/>.
/// </summary>
public sealed class ApiService(Plugin plugin, IDalamudPluginInterface pluginInterface) : IDisposable
{
    private readonly HttpClient httpClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private async Task<UploadOutcome> Post<T>(T body, string endpoint, CancellationToken cancellationToken,
        JsonSerializerOptions? options = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = plugin.Configuration;
        if (!config.CanCrowdsourceData)
            throw new OperationCanceledException("Crowdsourcing is disabled.");

        var system = config.SystemConfiguration;
        if (!Guid.TryParse(system.ApiKey, out var key))
            return UploadOutcome.Unauthorized;

        if (!Uri.TryCreate(system.ApiUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !(baseUri.IsLoopback && baseUri.Scheme == Uri.UriSchemeHttp)) ||
            baseUri.UserInfo.Length != 0 || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0)
            return UploadOutcome.Drop;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, endpoint));
        request.Headers.Add("User-Agent", $"XivForays/{pluginInterface.Manifest.AssemblyVersion}");
        request.Headers.Add("X-Api-Key", key.ToString());
        request.Content = JsonContent.Create(body, options: options);

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return UploadPolicy.Classify((int)response.StatusCode, timedOut: false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UploadPolicy.Classify(null, timedOut: true);
        }
        catch (HttpRequestException)
        {
            return UploadPolicy.Classify(null, timedOut: false);
        }
    }

    internal Task<UploadOutcome> UploadFate(Fate fate, CancellationToken cancellationToken) =>
        Post(fate, "fateended", cancellationToken);

    internal Task<UploadOutcome> UploadEnemyPosition(List<EnemyPosition> enemies, CancellationToken cancellationToken) =>
        Post(enemies, "enemyposition", cancellationToken);

    internal Task<UploadOutcome> UploadObservations(ObservationBatch batch, CancellationToken cancellationToken) =>
        Post(batch, "observations", cancellationToken, ObservationJson.Options);

    public void Dispose() => httpClient.Dispose();
}

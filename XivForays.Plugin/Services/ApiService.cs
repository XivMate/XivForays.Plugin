using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

public sealed class ApiService(Plugin plugin, IDalamudPluginInterface pluginInterface) : IDisposable
{
    private static readonly JsonSerializerOptions ObservationJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private async Task<HttpStatusCode> Post<T>(T body, string endpoint, CancellationToken cancellationToken, JsonSerializerOptions? options = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = plugin.Configuration;
        if (!config.CanCrowdsourceData)
            throw new OperationCanceledException("Crowdsourcing is disabled.");

        var system = config.SystemConfiguration;
        if (!Guid.TryParse(system.ApiKey, out var key))
            throw new InvalidOperationException("A valid API key is required.");

        if (!Uri.TryCreate(system.ApiUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !(baseUri.IsLoopback && baseUri.Scheme == Uri.UriSchemeHttp)) ||
            baseUri.UserInfo.Length != 0 || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0)
            throw new InvalidOperationException("The API URL must use HTTPS or loopback HTTP.");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, endpoint));
        request.Headers.Add("User-Agent", $"XivForays/{pluginInterface.Manifest.AssemblyVersion}");
        request.Headers.Add("X-Api-Key", key.ToString());
        request.Content = JsonContent.Create(body, options: options);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.StatusCode;
    }

    public Task UploadFate(Fate fate, CancellationToken cancellationToken) =>
        PostLegacy(fate, "fateended", cancellationToken);

    public Task UploadEnemyPosition(List<EnemyPosition> enemies, CancellationToken cancellationToken) =>
        PostLegacy(enemies, "enemyposition", cancellationToken);

    internal async Task<bool> UploadObservations(ObservationBatch batch, CancellationToken cancellationToken)
    {
        var status = await Post(batch, "observations", cancellationToken, ObservationJson);
        if (status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest or
            HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity)
            return false;

        EnsureAccepted(status);
        return true;
    }

    private async Task PostLegacy<T>(T body, string endpoint, CancellationToken cancellationToken) =>
        EnsureAccepted(await Post(body, endpoint, cancellationToken));

    private static void EnsureAccepted(HttpStatusCode status)
    {
        if (status != HttpStatusCode.OK)
            throw new HttpRequestException($"Foray ingest returned HTTP {(int)status}.", null, status);
    }

    public void Dispose() => httpClient.Dispose();
}

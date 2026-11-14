using System;
using System.Threading;
using System.Threading.Tasks;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>One anonymous GET of a public coverage file. Status 0 = no answer (offline, timeout, DNS ...).</summary>
internal readonly record struct CoverageResponse(int Status, byte[]? Body, string? ETag, string? LastModified);

/// <summary>Does the HTTP request. The plugin's implementation is anonymous: no API key, no cookies.</summary>
internal interface ICoverageFetcher
{
    Task<CoverageResponse> Get(string url, string? etag, string? lastModified, CancellationToken cancellationToken);
}

/// <summary>The last good file of a zone on disk (validated public coverage and its validators only).</summary>
internal interface ICoverageStore
{
    (byte[] Body, string? ETag, string? LastModified)? Load(string zone);
    void Save(string zone, byte[] body, string? etag, string? lastModified);
}

/// <summary>Where the feed stands, for the overlay window.</summary>
internal enum CoverageFeedState : byte
{
    /// <summary>Not in a supported zone, or the overlay is off.</summary>
    Idle,
    /// <summary>Waiting for the first answer, no cache.</summary>
    Loading,
    /// <summary>The last check succeeded (200 or 304).</summary>
    Fresh,
    /// <summary>The last check failed; the last good file (from memory or disk) is shown.</summary>
    OfflineCached,
    /// <summary>The last check failed and there is no good file: global coverage unavailable.</summary>
    Unavailable
}

/// <summary>
/// The per-zone coverage download: last-good cache, ETag / If-None-Match (If-Modified-Since as fallback), a poll about
/// every 3 minutes with jitter, one request in flight, increasing retry delays, cancellation on zone change and
/// disable, and late answers of a previous zone or request dropped. <see cref="Tick"/> and <see cref="Stop"/> run on
/// the framework thread; the request and the parse run on the thread pool and hand back a finished snapshot.
/// </summary>
internal sealed class CoverageFeed(ICoverageFetcher fetcher, ICoverageStore? store, string baseUrl = CoverageFeed.DefaultBaseUrl)
{
    public const string DefaultBaseUrl = "https://beta.xivforays.com/live/";
    public const long PollMs = 180_000;
    public const long PollJitterMs = 15_000;
    private static readonly long[] RetryMs = [15_000, 30_000, 60_000, 120_000, 180_000];

    private readonly object gate = new();
    private readonly Random random = new();
    private CancellationTokenSource? inFlight;
    private int generation;
    private uint territory;
    private string? zone;
    private string? etag;
    private string? lastModified;
    private long nextCheckMs;
    private int failures;
    private Completed? completed;

    /// <summary>The current zone's last good snapshot (null = none yet). Framework thread.</summary>
    public CoverageSnapshot? Snapshot { get; private set; }

    public CoverageFeedState State { get; private set; } = CoverageFeedState.Idle;

    /// <summary>Monotonic ms of the last successful check (200 or 304), or null.</summary>
    public long? LastCheckedMs { get; private set; }

    /// <summary>The last failure reason ("offline", "HTTP 503", "wrong zone" ...), or "".</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Requests started since creation (tests: the overlay off must start none).</summary>
    public int RequestsStarted { get; private set; }

    public bool Busy => inFlight != null;

    /// <summary>The coverage zone name of a supported field territory, or null.</summary>
    public static string? ZoneName(uint territory) => territory switch
    {
        ForayZones.EurekaAnemos => "anemos",
        ForayZones.EurekaPagos => "pagos",
        ForayZones.EurekaPyros => "pyros",
        ForayZones.EurekaHydatos => "hydatos",
        _ => null
    };

    /// <summary>
    /// Per overlay tick. <paramref name="supportedTerritory"/> is the territory when the player is on a supported field
    /// map, else 0 (unsupported zone or map layer: nothing is fetched and the snapshot is cleared).
    /// </summary>
    public void Tick(long nowMs, uint supportedTerritory)
    {
        ApplyCompleted(nowMs);
        if (supportedTerritory != territory)
            SwitchZone(supportedTerritory, nowMs);
        if (zone == null || inFlight != null || nowMs < nextCheckMs)
            return;
        Start(nowMs);
    }

    /// <summary>The overlay was turned off or the plugin unloads: cancel and forget the zone.</summary>
    public void Stop()
    {
        lock (gate)
        {
            generation++;
            completed = null;
        }

        Cancel();
        territory = 0;
        zone = null;
        Snapshot = null;
        State = CoverageFeedState.Idle;
    }

    private void SwitchZone(uint next, long nowMs)
    {
        lock (gate)
        {
            generation++;
            completed = null;
        }

        Cancel();
        territory = next;
        zone = ZoneName(next);
        etag = null;
        lastModified = null;
        failures = 0;
        Snapshot = null;
        LastCheckedMs = null;
        LastError = "";
        nextCheckMs = nowMs;
        State = zone == null ? CoverageFeedState.Idle : CoverageFeedState.Loading;
        if (zone == null || store == null)
            return;

        // The last good file of this zone shows at once; a conditional request follows straight away.
        try
        {
            if (store.Load(zone) is { } cached &&
                CoverageParser.Parse(cached.Body, next, out _) is { } snapshot)
            {
                Snapshot = snapshot;
                etag = cached.ETag;
                lastModified = cached.LastModified;
                State = CoverageFeedState.OfflineCached;
            }
        }
        catch (Exception)
        {
            // A broken cache file is ignored; the download replaces it.
        }
    }

    private void Start(long nowMs)
    {
        var source = new CancellationTokenSource();
        inFlight = source;
        RequestsStarted++;
        int requestGeneration;
        lock (gate)
            requestGeneration = generation;
        var requestTerritory = territory;
        var requestZone = zone!;
        var url = baseUrl + "coverage-" + requestZone + ".json";
        var sentEtag = Snapshot == null ? null : etag;
        var sentModified = Snapshot == null ? null : lastModified;
        _ = Task.Run(async () =>
        {
            Completed result;
            try
            {
                var response = await fetcher.Get(url, sentEtag, sentModified, source.Token).ConfigureAwait(false);
                result = Interpret(response, requestTerritory, requestZone, requestGeneration);
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                result = new Completed(requestGeneration, requestTerritory, false, false, null, null, null, "offline");
            }

            lock (gate)
            {
                if (result.Generation == generation)
                    completed = result;
            }
        });
    }

    private Completed Interpret(CoverageResponse response, uint requestTerritory, string requestZone, int requestGeneration)
    {
        if (response.Status == 304)
            return new Completed(requestGeneration, requestTerritory, true, true, null, response.ETag, response.LastModified, "");
        if (response.Status != 200 || response.Body == null)
            return new Completed(requestGeneration, requestTerritory, false, false, null, null, null,
                response.Status == 0 ? "offline" : "HTTP " + response.Status);

        var snapshot = CoverageParser.Parse(response.Body, requestTerritory, out var error);
        if (snapshot == null)
            return new Completed(requestGeneration, requestTerritory, false, false, null, null, null, error);
        try
        {
            store?.Save(requestZone, response.Body, response.ETag, response.LastModified);
        }
        catch (Exception)
        {
            // The cache is a convenience only.
        }

        return new Completed(requestGeneration, requestTerritory, true, false, snapshot, response.ETag, response.LastModified, "");
    }

    private void ApplyCompleted(long nowMs)
    {
        Completed? done;
        lock (gate)
        {
            done = completed;
            completed = null;
            if (done != null && done.Generation != generation)
                done = null;
        }

        if (done == null)
            return;
        inFlight?.Dispose();
        inFlight = null;
        if (done.Territory != territory)
            return;

        if (done.Ok)
        {
            if (done.Snapshot != null)
                Snapshot = done.Snapshot;
            if (done.ETag != null)
                etag = done.ETag;
            if (done.LastModified != null)
                lastModified = done.LastModified;
            if (done.NotModified && Snapshot == null)
            {
                // A 304 without a body to keep: ask again without validators.
                etag = null;
                lastModified = null;
                nextCheckMs = nowMs;
                return;
            }

            failures = 0;
            LastError = "";
            LastCheckedMs = nowMs;
            State = CoverageFeedState.Fresh;
            nextCheckMs = nowMs + PollMs + (long)(random.NextDouble() * PollJitterMs);
            return;
        }

        LastError = done.Error;
        State = Snapshot != null ? CoverageFeedState.OfflineCached : CoverageFeedState.Unavailable;
        nextCheckMs = nowMs + RetryMs[Math.Min(failures, RetryMs.Length - 1)];
        failures++;
    }

    private void Cancel()
    {
        var source = inFlight;
        inFlight = null;
        if (source == null)
            return;
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Test hook: waits until the in-flight request handed back its result.</summary>
    internal bool WaitForCompletion(int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            lock (gate)
                if (completed != null)
                    return true;
            Thread.Sleep(5);
        }

        return false;
    }

    private sealed record Completed(int Generation, uint Territory, bool Ok, bool NotModified, CoverageSnapshot? Snapshot,
        string? ETag, string? LastModified, string Error);
}

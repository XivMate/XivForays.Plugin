using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Gathering;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

internal sealed class ObservationService(
    IClientState clientState,
    ForayService forayService,
    SchedulerService scheduler,
    ApiService api,
    IPluginLog log) : IModule
{
    private readonly object gate = new();
    private readonly ObservationBatcher batcher = new();
    private readonly UploadPolicy policy = new();
    private int uploading;
    private volatile bool enabled;
    private UploadOutcome? lastWarned;

    public bool Enabled => enabled;

    public void LoadConfig(Configuration.Configuration configuration)
    {
        if (configuration.CanCrowdsourceData == enabled)
            return;

        lock (gate)
        {
            enabled = configuration.CanCrowdsourceData;
            if (!enabled)
                batcher.Clear();
        }

        if (enabled)
            scheduler.ScheduleOnNewThread(UploadObservations, UploadPolicy.WorkerIntervalMs);
        else
            scheduler.CancelScheduledTask(UploadObservations);
    }

    // All game reads are performed by the producer on the framework thread.
    public unsafe Observation GetContext()
    {
        var territory = clientState.TerritoryType;
        var weather = WeatherManager.Instance();
        var weatherId = weather == null ? (byte)0 : weather->GetCurrentWeather();
        var player = PlayerState.Instance();
        var result = new Observation
        {
            ObservedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            VisitId = forayService.VisitId,
            TerritoryId = territory,
            MapId = clientState.MapId,
            WeatherId = weatherId == 0 ? null : weatherId
        };

        if (player == null)
            return result;

        if (ForayZones.IsEureka(territory))
        {
            var effective = player->GetContentValue(2);
            return result with
            {
                PlayerEffectiveElementalLevel = Nonzero(effective),
                PlayerElementalLevel = Nonzero(player->GetContentValue(4)),
                PlayerElementalSynced = effective == 0 ? null : player->GetContentValue(3) != 0
            };
        }

        if (ForayZones.IsOccultCrescent(territory))
        {
            return result with
            {
                PlayerEffectiveKnowledgeLevel = Nonzero(player->GetContentValue(6)),
                PlayerKnowledgeLevel = Nonzero(player->GetContentValue(7))
            };
        }

        return result;
    }

    private static uint? Nonzero(uint value) => value == 0 ? null : value;

    public void Record(Observation observation)
    {
        lock (gate)
        {
            if (!enabled)
                return;

            batcher.Enqueue(observation);
        }
    }

    private void UploadObservations(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref uploading, 1) != 0)
            return;

        try
        {
            ObservationBatch? batch;
            lock (gate)
            {
                if (cancellationToken.IsCancellationRequested || !enabled || !batcher.HasWork ||
                    !policy.CanAttempt(Environment.TickCount64))
                    return;

                // At most 256 records and 400 KB serialized per batch; a 413 splits it (see ObservationBatcher).
                batch = batcher.Next();
                if (batch == null)
                    return;
            }

            if (!enabled)
                return;

            var outcome = api.UploadObservations(batch, cancellationToken).GetAwaiter().GetResult();
            var keep = policy.Record(outcome, Environment.TickCount64);
            lock (gate)
                batcher.Complete(batch, outcome, keep);

            Report(outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disabling sharing or unloading cancels this worker.
        }
        catch (Exception ex)
        {
            log.Warning($"Foray observation upload failed ({ex.GetType().Name}).");
        }
        finally
        {
            Volatile.Write(ref uploading, 0);
        }
    }

    // One line per change of state, never the key, URL or body.
    private void Report(UploadOutcome outcome)
    {
        if (outcome == UploadOutcome.Accepted)
        {
            lastWarned = null;
            return;
        }

        if (outcome == UploadOutcome.TooLarge)
        {
            if (lastWarned != outcome)
                log.Warning("XivForays: the server refused an observation batch as too large (413); it is split and resent.");
            lastWarned = outcome;
            return;
        }

        if (lastWarned == outcome)
            return;

        lastWarned = outcome;
        log.Warning(outcome switch
        {
            UploadOutcome.EndpointMissing =>
                "XivForays: observations endpoint returned 404; the web friends branch is not deployed. New data is being dropped.",
            UploadOutcome.Unauthorized =>
                "XivForays: the server refused the API key (401/403). Check the key in /xivforays config. Uploads paused.",
            UploadOutcome.Drop => "XivForays: the server rejected an observation batch (HTTP 4xx); it was dropped.",
            _ => "XivForays: observation upload failed (server error or timeout); retrying with backoff."
        });
    }

    public void Dispose()
    {
        if (!enabled)
            return;

        LoadConfig(new Configuration.Configuration());
    }
}

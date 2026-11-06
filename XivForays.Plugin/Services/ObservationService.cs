using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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
    private const int MaxBatchSize = 256;
    private const int MaxPending = 4096;
    private readonly object gate = new();
    private readonly Queue<Observation> pending = new();
    private ObservationBatch? retryBatch;
    private int uploading;
    private volatile bool enabled;

    public bool Enabled => enabled;

    public void LoadConfig(Configuration.Configuration configuration)
    {
        if (configuration.CanCrowdsourceData == enabled)
            return;

        lock (gate)
        {
            enabled = configuration.CanCrowdsourceData;
            if (!enabled)
            {
                pending.Clear();
                retryBatch = null;
            }
        }

        if (enabled)
            scheduler.ScheduleOnNewThread(UploadObservations, 2000);
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

        if (ForayService.IsEureka(territory))
        {
            var effective = player->GetContentValue(2);
            return result with
            {
                PlayerEffectiveElementalLevel = Nonzero(effective),
                PlayerElementalLevel = Nonzero(player->GetContentValue(4)),
                PlayerElementalSynced = effective == 0 ? null : player->GetContentValue(3) != 0
            };
        }

        if (ForayService.IsOccultCrescent(territory))
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

            if (pending.Count == MaxPending)
                pending.Dequeue();
            pending.Enqueue(observation);
        }
    }

    private void UploadObservations()
    {
        if (Interlocked.Exchange(ref uploading, 1) != 0)
            return;

        try
        {
            ObservationBatch batch;
            lock (gate)
            {
                if (!enabled || (retryBatch == null && pending.Count == 0))
                    return;

                if (retryBatch == null)
                {
                    var observations = new Observation[Math.Min(MaxBatchSize, pending.Count)];
                    for (var i = 0; i < observations.Length; i++)
                        observations[i] = pending.Dequeue();
                    retryBatch = new ObservationBatch(1, Guid.NewGuid(), observations);
                }

                batch = retryBatch;
            }

            if (!enabled)
                return;

            var accepted = api.UploadObservations(batch).GetAwaiter().GetResult();
            lock (gate)
            {
                if (ReferenceEquals(retryBatch, batch))
                    retryBatch = null;
            }

            if (!accepted)
                log.Warning("The server rejected or does not support versioned Foray observations.");
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

    public void Dispose()
    {
        if (!enabled)
            return;

        LoadConfig(new Configuration.Configuration());
    }
}

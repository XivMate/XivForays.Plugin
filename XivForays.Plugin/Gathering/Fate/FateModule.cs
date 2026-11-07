using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Gathering.Fate;

internal sealed class FateModule(
    IObjectTable objectTable,
    IFateTable fateTable,
    SchedulerService scheduler,
    IPluginLog log,
    ApiService api,
    ForayService forayService,
    ObservationService observations) : IModule
{
    private readonly FateLifecycle lifecycle = new();
    // Legacy FATE-end route records (name, level) for the FATEs the lifecycle is tracking.
    private readonly Dictionary<uint, Models.Fate> legacy = new();
    private const int MaxQueuedFates = 32;
    private readonly UploadPolicy policy = new();
    private ConcurrentQueue<Models.Fate> queue = new();
    private Guid visitId;
    private volatile bool enabled;

    public bool Enabled => enabled;
    public IEnumerable<Models.Fate> ActiveFates => legacy.Values.ToList();

    public void LoadConfig(Configuration.Configuration configuration)
    {
        if (configuration.CanCrowdsourceData == enabled)
            return;

        enabled = configuration.CanCrowdsourceData;
        if (enabled)
        {
            scheduler.ScheduleOnFrameworkThread(FateTick, StreamRates.FateScanMs);
            scheduler.ScheduleOnNewThread(FateUpload, 2500);
        }
        else
        {
            scheduler.CancelScheduledTask(FateTick);
            scheduler.CancelScheduledTask(FateUpload);
            lifecycle.Clear();
            legacy.Clear();
            queue = new();
        }
    }

    private void FateTick()
    {
        if (!enabled)
            return;

        if (visitId != forayService.VisitId)
        {
            // Left the zone/instance: FateDisappeared (not FateEnded) for anything still tracked.
            lifecycle.LeaveVisit(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), observations.Record);
            legacy.Clear();
            visitId = forayService.VisitId;
        }

        if (objectTable.LocalPlayer == null || !forayService.IsInRecordableTerritory())
        {
            lifecycle.Interrupt();
            return;
        }

        try
        {
            SampleFates(observations.GetContext());
        }
        catch (Exception ex)
        {
            log.Warning($"FATE sampling failed ({ex.GetType().Name}).");
        }
    }

    private void SampleFates(Observation context)
    {
        var snapshots = new List<FateSnapshot>();
        var byId = new Dictionary<uint, IFate>();
        foreach (var fate in fateTable)
        {
            if (fate.TerritoryType.RowId != context.TerritoryId)
                continue;

            byId[fate.FateId] = fate;
            snapshots.Add(new FateSnapshot(fate.FateId, (byte)fate.State, fate.Progress, fate.StartTimeEpoch,
                fate.Position.X, fate.Position.Y, fate.Position.Z, fate.Radius));
        }

        lifecycle.Sample(context, snapshots, observation =>
        {
            observations.Record(observation);
            var id = observation.GameId ?? 0;
            switch (observation.Kind)
            {
                case ObservationKind.FateStarted when byId.TryGetValue(id, out var fate):
                    legacy[id] = NewLegacy(fate, context);
                    break;
                case ObservationKind.FateEnded or ObservationKind.FateDisappeared when legacy.Remove(id, out var done):
                    QueueLegacy(done, context);
                    break;
            }
        });
    }

    private static Models.Fate NewLegacy(IFate fate, Observation context) => new()
    {
        FateId = fate.FateId,
        Name = fate.Name.TextValue,
        X = fate.Position.X,
        Y = fate.Position.Y,
        Z = fate.Position.Z,
        Radius = fate.Radius,
        StartedAt = fate.StartTimeEpoch,
        InstanceId = context.VisitId,
        TerritoryId = (int)context.TerritoryId,
        MapId = (int)context.MapId,
        LevelId = fate.Level
    };

    private void QueueLegacy(Models.Fate fate, Observation context)
    {
        fate.EndedAt = context.ObservedAtMs / 1000;
        if (queue.Count >= MaxQueuedFates)
            queue.TryDequeue(out _);
        queue.Enqueue(fate);
    }

    private void FateUpload(CancellationToken cancellationToken)
    {
        var pending = queue;
        if (cancellationToken.IsCancellationRequested || !enabled || !policy.CanAttempt(Environment.TickCount64) ||
            !pending.TryDequeue(out var fate))
            return;

        try
        {
            var outcome = api.UploadFate(fate, cancellationToken).GetAwaiter().GetResult();
            var failuresBefore = policy.ConsecutiveFailures;
            // Retry only transient failures (5xx/timeout), with backoff; 4xx records are dropped.
            if (policy.Record(outcome, Environment.TickCount64) && enabled && pending.Count < MaxQueuedFates)
                pending.Enqueue(fate);
            if (outcome != UploadOutcome.Accepted && failuresBefore == 0)
                log.Warning($"FATE completion upload failed ({outcome}).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disabling sharing or unloading cancels this worker.
        }
        catch (Exception ex)
        {
            log.Warning($"FATE completion upload failed ({ex.GetType().Name}).");
        }
    }

    public void Dispose() => LoadConfig(new Configuration.Configuration());

}

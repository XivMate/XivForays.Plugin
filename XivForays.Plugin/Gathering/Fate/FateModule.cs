using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    private readonly Dictionary<uint, TrackedFate> fates = new();
    private const int MaxQueuedFates = 32;
    private readonly UploadPolicy policy = new();
    private ConcurrentQueue<Models.Fate> queue = new();
    private Guid visitId;
    private volatile bool enabled;

    public bool Enabled => enabled;
    public IEnumerable<Models.Fate> ActiveFates => fates.Values.Select(fate => fate.Legacy).ToList();

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
            fates.Clear();
            queue = new();
        }
    }

    private void FateTick()
    {
        if (!enabled)
            return;

        if (visitId != forayService.VisitId)
        {
            fates.Clear();
            visitId = forayService.VisitId;
        }

        if (objectTable.LocalPlayer == null || !forayService.IsInRecordableTerritory())
            return;

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
        var visible = new HashSet<uint>();
        foreach (var fate in fateTable)
        {
            if (fate.TerritoryType.RowId != context.TerritoryId)
                continue;

            var id = fate.FateId;
            visible.Add(id);
            fates.TryGetValue(id, out var tracked);
            if (tracked != null && fate.State is (FateState.Ended or FateState.Failed))
            {
                Finish(tracked with { State = fate.State, Progress = fate.Progress }, context, ObservationKind.FateEnded);
                continue;
            }

            if (fate.State != FateState.Running || fate.StartTimeEpoch <= 0 || fate.Position == Vector3.Zero)
                continue;

            if (tracked != null && tracked.Legacy.StartedAt != fate.StartTimeEpoch)
            {
                Finish(tracked, context, ObservationKind.FateDisappeared);
                tracked = null;
            }

            if (tracked == null)
            {
                var legacy = new Models.Fate
                {
                    FateId = id,
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
                var started = new TrackedFate(legacy, fate.State, fate.Progress, fate.Position, fate.Radius);
                fates[id] = started;
                Record(started, context, ObservationKind.FateStarted);
            }
            else if (tracked.Progress != fate.Progress || tracked.Position != fate.Position || tracked.Radius != fate.Radius)
            {
                var updated = tracked with { Progress = fate.Progress, Position = fate.Position, Radius = fate.Radius };
                fates[id] = updated;
                Record(updated, context, ObservationKind.FateUpdated);
            }
        }

        foreach (var id in fates.Keys.Where(id => !visible.Contains(id)).ToList())
            Finish(fates[id], context, ObservationKind.FateDisappeared);
    }

    private void Finish(TrackedFate fate, Observation context, ObservationKind kind)
    {
        fates.Remove(fate.Legacy.FateId);
        Record(fate, context, kind);
        fate.Legacy.EndedAt = context.ObservedAtMs / 1000;
        if (queue.Count >= MaxQueuedFates)
            queue.TryDequeue(out _);
        queue.Enqueue(fate.Legacy);
    }

    private void Record(TrackedFate fate, Observation context, ObservationKind kind) =>
        observations.Record(context with
        {
            Kind = kind,
            GameId = fate.Legacy.FateId,
            X = fate.Position.X,
            Y = fate.Position.Y,
            Z = fate.Position.Z,
            Radius = fate.Radius,
            StartedAt = fate.Legacy.StartedAt,
            Progress = fate.Progress,
            State = (byte)fate.State
        });

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

    private sealed record TrackedFate(Models.Fate Legacy, FateState State, byte Progress, Vector3 Position, float Radius);
}

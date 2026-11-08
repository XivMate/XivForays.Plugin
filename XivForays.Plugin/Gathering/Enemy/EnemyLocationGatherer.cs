using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin.Services;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Gathering.Enemy;

internal sealed class EnemyLocationGatherer(
    EnemyTrackingService enemies,
    PoseCaptureService poseCapture,
    ObservationService observations,
    SchedulerService scheduler,
    IPluginLog log,
    ApiService api) : IModule
{
    private const int MaxQueuedSnapshots = 10;
    private readonly UploadPolicy policy = new();
    private ConcurrentQueue<List<EnemyPosition>> queue = new();
    private long lastSnapshot;
    private long lastPeriodicSample;
    private volatile bool enabled;

    public bool Enabled => enabled;

    public void LoadConfig(Configuration.Configuration configuration)
    {
        poseCapture.CapturePass = configuration.CapturePass;
        if (configuration.CanCrowdsourceData == enabled)
            return;

        enabled = configuration.CanCrowdsourceData;
        if (enabled)
        {
            lastSnapshot = 0;
            lastPeriodicSample = 0;
            scheduler.ScheduleOnFrameworkThread(EnemyTick, StreamRates.EnemyScanMs);
            scheduler.ScheduleOnNewThread(EnemyUpload, 3000);
        }
        else
        {
            scheduler.CancelScheduledTask(EnemyTick);
            scheduler.CancelScheduledTask(EnemyUpload);
            enemies.Reset();
            queue = new();
        }
    }

    private void EnemyTick()
    {
        if (!enabled || !enemies.IsInRecordableTerritory())
            return;

        try
        {
            var now = Environment.TickCount64;
            var snapshotDue = now - lastSnapshot >= StreamRates.LegacySnapshotMs || lastSnapshot == 0;
            var periodicDue = now - lastPeriodicSample >= StreamRates.EnemyPeriodicSampleMs || lastPeriodicSample == 0;
            if (periodicDue)
                lastPeriodicSample = now;
            var snapshot = enemies.Sample(observations.GetContext(), snapshotDue, periodicDue, observations.Record);
            if (!snapshotDue)
                return;

            lastSnapshot = now;
            if (snapshot.Count > 0)
            {
                if (queue.Count >= MaxQueuedSnapshots)
                    queue.TryDequeue(out _);
                queue.Enqueue(snapshot);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Enemy sampling failed ({ex.GetType().Name}).");
        }
    }

    private void EnemyUpload(CancellationToken cancellationToken)
    {
        var pending = queue;
        if (cancellationToken.IsCancellationRequested || !enabled || !policy.CanAttempt(Environment.TickCount64) ||
            !pending.TryDequeue(out var snapshot))
            return;

        try
        {
            var outcome = api.UploadEnemyPosition(snapshot, cancellationToken).GetAwaiter().GetResult();
            var failuresBefore = policy.ConsecutiveFailures;
            // Retry only transient failures (5xx/timeout), with backoff; 4xx batches are dropped.
            if (policy.Record(outcome, Environment.TickCount64) && enabled && pending.Count < MaxQueuedSnapshots)
                pending.Enqueue(snapshot);
            if (outcome != UploadOutcome.Accepted && failuresBefore == 0)
                log.Warning($"Enemy position upload failed ({outcome}).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disabling sharing or unloading cancels this worker.
        }
        catch (Exception ex)
        {
            log.Warning($"Enemy position upload failed ({ex.GetType().Name}).");
        }
    }

    public void Dispose() => LoadConfig(new Configuration.Configuration());
}

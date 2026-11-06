using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin.Services;
using XivMate.DataGathering.Forays.Dalamud.Models;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Gathering.Enemy;

internal sealed class EnemyLocationGatherer(
    EnemyTrackingService enemies,
    ObservationService observations,
    SchedulerService scheduler,
    IPluginLog log,
    ApiService api) : IModule
{
    private ConcurrentQueue<List<EnemyPosition>> queue = new();
    private long lastSnapshot;
    private volatile bool enabled;

    public bool Enabled => enabled;

    public void LoadConfig(Configuration.Configuration configuration)
    {
        if (configuration.CanCrowdsourceData == enabled)
            return;

        enabled = configuration.CanCrowdsourceData;
        if (enabled)
        {
            lastSnapshot = 0;
            scheduler.ScheduleOnFrameworkThread(EnemyTick, 500);
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
            var snapshotDue = now - lastSnapshot >= 5000 || lastSnapshot == 0;
            var snapshot = enemies.Sample(observations.GetContext(), snapshotDue, observations.Record);
            if (!snapshotDue)
                return;

            lastSnapshot = now;
            if (snapshot.Count > 0)
            {
                if (queue.Count >= 10)
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
        if (cancellationToken.IsCancellationRequested || !enabled || !pending.TryDequeue(out var snapshot))
            return;

        try
        {
            api.UploadEnemyPosition(snapshot, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disabling sharing or unloading cancels this worker.
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested && enabled && pending.Count < 10)
                pending.Enqueue(snapshot);
            log.Warning($"Enemy position upload failed ({ex.GetType().Name}).");
        }
    }

    public void Dispose() => LoadConfig(new Configuration.Configuration());
}

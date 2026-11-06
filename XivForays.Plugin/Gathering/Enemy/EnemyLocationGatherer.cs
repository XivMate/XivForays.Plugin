using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private readonly ConcurrentQueue<List<EnemyPosition>> queue = new();
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
            queue.Clear();
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

    private void EnemyUpload()
    {
        if (!enabled || !queue.TryDequeue(out var snapshot))
            return;

        try
        {
            api.UploadEnemyPosition(snapshot).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            if (enabled && queue.Count < 10)
                queue.Enqueue(snapshot);
            log.Warning($"Enemy position upload failed ({ex.GetType().Name}).");
        }
    }

    public void Dispose() => LoadConfig(new Configuration.Configuration());
}

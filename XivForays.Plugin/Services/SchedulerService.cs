using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

public sealed class SchedulerService(IFramework framework, IPluginLog log) : IDisposable
{
    private readonly Dictionary<Delegate, Action> stopActions = new();

    public void ScheduleOnFrameworkThread(Action action, int intervalMs)
    {
        long nextTick = 0;
        void Tick(IFramework _)
        {
            var now = Environment.TickCount64;
            if (now < nextTick)
                return;

            nextTick = now + intervalMs;
            action();
        }

        stopActions.Add(action, () => framework.Update -= Tick);
        framework.Update += Tick;
    }

    public void ScheduleOnNewThread(Action<CancellationToken> action, int intervalMs)
    {
        if (stopActions.ContainsKey(action))
            throw new InvalidOperationException("This worker is already scheduled.");

        var source = new CancellationTokenSource();
        var worker = Task.Run(() => RunWorker(action, intervalMs, source.Token));
        stopActions.Add(action, () =>
        {
            source.Cancel();
            _ = worker.ContinueWith(_ => source.Dispose(), TaskScheduler.Default);
        });
    }

    private async Task RunWorker(Action<CancellationToken> action, int intervalMs, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                action(token);
                await Task.Delay(intervalMs, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The module was stopped.
        }
        catch (Exception ex)
        {
            log.Warning($"A Foray upload worker stopped ({ex.GetType().Name}).");
        }
    }

    public void CancelScheduledTask(Action action) => Cancel(action);

    public void CancelScheduledTask(Action<CancellationToken> action) => Cancel(action);

    private void Cancel(Delegate action)
    {
        if (stopActions.Remove(action, out var stop))
            stop();
    }

    public void Dispose()
    {
        foreach (var stop in stopActions.Values)
            stop();
        stopActions.Clear();
    }
}

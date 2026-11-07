using System;
using System.Collections.Generic;
using System.Linq;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>One fate-table entry as read on the framework thread (no Dalamud types, so it is unit-testable).</summary>
internal readonly record struct FateSnapshot(
    uint FateId, byte State, byte Progress, int StartedAt, float X, float Y, float Z, float Radius);

/// <summary>
/// FATE lifecycle for the observations stream. FateEnded is emitted when an entry reaches an end state
/// (Ending, Ended, Failed) or when a tracked FATE vanishes from the fate table while we are still in the zone
/// (outcome then inferred from the last progress). FateDisappeared is kept for leaving the zone/instance
/// (visit change, or a vanish right after a non-recordable gap such as BetweenAreas).
/// </summary>
internal sealed class FateLifecycle
{
    // Dalamud.Game.ClientState.Fates.FateState values (API 15).
    public const byte Preparing = 3, Running = 4, Ending = 5, Ended = 7, Failed = 8;

    private readonly Dictionary<uint, FateSnapshot> fates = new();
    private Observation? lastContext;
    private bool interrupted;

    public IReadOnlyCollection<uint> TrackedIds => fates.Keys;

    public static bool IsEndState(byte state) => state is Ending or Ended or Failed;

    /// <summary>One scan of the fate table for the current zone. <paramref name="emit"/> gets each observation.</summary>
    public void Sample(Observation context, IEnumerable<FateSnapshot> table, Action<Observation> emit)
    {
        var resumedAfterGap = interrupted;
        interrupted = false;
        lastContext = context;
        var visible = new HashSet<uint>();
        foreach (var fate in table)
        {
            visible.Add(fate.FateId);
            fates.TryGetValue(fate.FateId, out var tracked);
            var isTracked = fates.ContainsKey(fate.FateId);

            if (isTracked && IsEndState(fate.State))
            {
                var last = tracked with { State = fate.State, Progress = fate.Progress };
                fates.Remove(fate.FateId);
                emit(EndOf(last, context, inferred: fate.State == Ending));
                continue;
            }

            if (fate.State != Running || fate.StartedAt <= 0 || (fate.X == 0 && fate.Y == 0 && fate.Z == 0))
                continue;

            if (isTracked && tracked.StartedAt != fate.StartedAt)
            {
                // Same FATE id started again: the previous run ended without us seeing an end state.
                fates.Remove(fate.FateId);
                emit(EndOf(tracked, context, inferred: true));
                isTracked = false;
            }

            if (!isTracked)
            {
                fates[fate.FateId] = fate;
                emit(ToObservation(fate, context, ObservationKind.FateStarted));
            }
            else if (tracked.Progress != fate.Progress || tracked.X != fate.X || tracked.Y != fate.Y ||
                     tracked.Z != fate.Z || tracked.Radius != fate.Radius)
            {
                fates[fate.FateId] = fate;
                emit(ToObservation(fate, context, ObservationKind.FateUpdated));
            }
        }

        foreach (var id in fates.Keys.Where(id => !visible.Contains(id)).ToList())
        {
            var gone = fates[id];
            fates.Remove(id);
            emit(resumedAfterGap
                ? ToObservation(gone, context, ObservationKind.FateDisappeared)
                : EndOf(gone, context, inferred: true));
        }
    }

    /// <summary>A tick where the zone was not recordable (BetweenAreas, no player): a vanish seen next is not an end.</summary>
    public void Interrupt()
    {
        if (fates.Count > 0)
            interrupted = true;
    }

    /// <summary>Left the zone/instance (visit changed): FateDisappeared for every tracked FATE, in the old context.</summary>
    public void LeaveVisit(long nowMs, Action<Observation> emit)
    {
        if (lastContext != null)
        {
            var context = lastContext with { ObservedAtMs = nowMs };
            foreach (var fate in fates.Values)
                emit(ToObservation(fate, context, ObservationKind.FateDisappeared));
        }

        Clear();
    }

    public void Clear()
    {
        fates.Clear();
        lastContext = null;
        interrupted = false;
    }

    public static FateOutcome OutcomeOf(byte state, byte progress) => state switch
    {
        Ended => FateOutcome.Success,
        Failed => FateOutcome.Failed,
        _ => progress >= 100 ? FateOutcome.Success : FateOutcome.Unknown
    };

    private static Observation EndOf(FateSnapshot fate, Observation context, bool inferred)
    {
        var duration = context.ObservedAtMs / 1000 - fate.StartedAt;
        return ToObservation(fate, context, ObservationKind.FateEnded) with
        {
            Outcome = OutcomeOf(fate.State, fate.Progress),
            OutcomeInferred = inferred,
            DurationSeconds = duration > 0 ? (uint)Math.Min(duration, uint.MaxValue) : 0
        };
    }

    private static Observation ToObservation(FateSnapshot fate, Observation context, ObservationKind kind) =>
        context with
        {
            Kind = kind,
            GameId = fate.FateId,
            X = fate.X,
            Y = fate.Y,
            Z = fate.Z,
            Radius = fate.Radius,
            StartedAt = fate.StartedAt,
            Progress = fate.Progress,
            State = fate.State
        };
}

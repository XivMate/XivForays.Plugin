using System;
using System.Collections.Generic;
using System.Linq;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>How a defeat notice found its enemy (kind 13 <c>enemyMatch</c>).</summary>
internal enum EnemyMatch : byte
{
    None = 0,
    NameUnique = 1,
    NameNearest = 2,
    FreshUnique = 3,
    FreshNearest = 4
}

/// <summary>One enemy that could be the target of a defeat notice: HP 0 now, not yet linked to another notice.</summary>
internal readonly record struct DefeatCandidate(int Index, bool NameMatches, bool DiedSinceLastScan, long? DiedAtMs, float Distance);

/// <summary>
/// Picks the enemy of a defeat notice. The notice names its target only by its name row, so a name match is preferred;
/// without one (the English chat fallback, or a name row that is not a BNpcName) only the final attempt may take any
/// fresh death. Among several, the freshest death wins (died since the last scan, then the latest death), then the
/// nearest to the player.
/// </summary>
internal static class DefeatMatch
{
    /// <summary>Deaths older than this are not candidates.</summary>
    public const long FreshMs = 3000;

    public static (int Index, EnemyMatch Match, byte Candidates)? Choose(IReadOnlyList<DefeatCandidate> candidates,
        bool hasName, bool finalAttempt, long nowMs)
    {
        var fresh = candidates.Where(c => c.DiedSinceLastScan || c.DiedAtMs is { } at && nowMs - at <= FreshMs).ToList();
        var named = hasName ? fresh.Where(c => c.NameMatches).ToList() : [];
        bool byName;
        List<DefeatCandidate> pool;
        if (named.Count > 0)
        {
            byName = true;
            pool = named;
        }
        else if (finalAttempt || !hasName)
        {
            byName = false;
            pool = fresh;
        }
        else
            return null;

        if (pool.Count == 0)
            return null;
        var best = pool.OrderByDescending(c => c.DiedSinceLastScan).ThenByDescending(c => c.DiedAtMs ?? long.MinValue)
            .ThenBy(c => c.Distance).First();
        var match = byName
            ? pool.Count == 1 ? EnemyMatch.NameUnique : EnemyMatch.NameNearest
            : pool.Count == 1 ? EnemyMatch.FreshUnique : EnemyMatch.FreshNearest;
        return (best.Index, match, (byte)Math.Min(pool.Count, byte.MaxValue));
    }
}

/// <summary>
/// Links loot (kind 9) and chain (kind 10) notices to the latest defeat notice (kind 13, own or party). A defeat notice
/// can come before the enemy scan sees HP 0, so its enemy is looked up again until it is found or
/// <see cref="ResolveMs"/> has passed. Loot and chain records that follow an unresolved defeat are held until it
/// resolves and are then emitted with their original time. FATE guard: a loot record in an open FATE reward window
/// is never linked to a kill. Pure (no Dalamud types) so it can be tested.
/// </summary>
internal sealed class DropLinker
{
    /// <summary>How long a defeat notice keeps looking for its enemy.</summary>
    public const long ResolveMs = 1500;

    /// <summary>Loot or chain notices later than this after the defeat are not linked to it.</summary>
    public const long MaxDefeatAgeMs = 5000;

    /// <summary>
    /// Looks up the enemy for a defeat record: (nameId, finalAttempt, nowMs) to the record with its enemy fields
    /// filled, or null when not found (yet).
    /// </summary>
    public delegate Observation? Resolver(Observation defeat, uint? nameId, bool finalAttempt, long nowMs);

    private sealed class Entry(Observation record, uint? nameId)
    {
        public Observation Record { get; set; } = record;
        public uint? NameId { get; } = nameId;
        public bool Resolved { get; set; }
        public List<Observation> Held { get; } = new();
    }

    private readonly List<Entry> pending = new();
    private Entry? last;

    public int PendingCount => pending.Count;

    /// <summary>A defeat notice. Emitted at once when its enemy is found, else on a later <see cref="Tick"/>.</summary>
    public void Defeat(Observation record, uint? nameId, Resolver resolve, Action<Observation> emit)
    {
        if (last != null && last.Record.VisitId != record.VisitId)
            Flush(emit);
        var entry = new Entry(record, nameId);
        last = entry;
        if (resolve(record, nameId, false, record.ObservedAtMs) is { } found)
        {
            entry.Record = found;
            entry.Resolved = true;
            emit(found);
            return;
        }

        pending.Add(entry);
    }

    /// <summary>A loot notice, with the FATE id when a FATE reward window is open.</summary>
    public void Loot(Observation record, uint? fateRewardId, Action<Observation> emit)
    {
        if (fateRewardId != null)
        {
            emit(record with { FateRewardId = fateRewardId });
            return;
        }

        Linked(record, emit);
    }

    /// <summary>A chain notice.</summary>
    public void Chain(Observation record, Action<Observation> emit) => Linked(record, emit);

    /// <summary>Retries the unresolved defeats; one past <see cref="ResolveMs"/> is emitted as it is.</summary>
    public void Tick(long nowMs, Resolver resolve, Action<Observation> emit)
    {
        for (var i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];
            var final = nowMs - entry.Record.ObservedAtMs >= ResolveMs;
            var found = resolve(entry.Record, entry.NameId, final, nowMs);
            if (found == null && !final)
                continue;
            if (found != null)
                entry.Record = found;
            Complete(entry, emit);
            pending.RemoveAt(i--);
        }
    }

    /// <summary>Emits everything still held (unresolved defeats as they are) and forgets the last defeat.</summary>
    public void Flush(Action<Observation> emit)
    {
        foreach (var entry in pending)
            Complete(entry, emit);
        pending.Clear();
        last = null;
    }

    /// <summary>Drops everything (sharing turned off).</summary>
    public void Clear()
    {
        pending.Clear();
        last = null;
    }

    private void Linked(Observation record, Action<Observation> emit)
    {
        var defeat = last;
        if (defeat == null || defeat.Record.VisitId != record.VisitId ||
            record.ObservedAtMs - defeat.Record.ObservedAtMs is < 0 or > MaxDefeatAgeMs)
        {
            emit(record);
            return;
        }

        if (!defeat.Resolved)
        {
            defeat.Held.Add(record);
            return;
        }

        emit(Link(record, defeat.Record));
    }

    private static void Complete(Entry entry, Action<Observation> emit)
    {
        entry.Resolved = true;
        emit(entry.Record);
        foreach (var held in entry.Held)
            emit(Link(held, entry.Record));
        entry.Held.Clear();
    }

    private static Observation Link(Observation record, Observation defeat) => record with
    {
        DefeatTrackId = defeat.EnemyTrackId,
        DefeatAgeMs = (uint)Math.Max(0, record.ObservedAtMs - defeat.ObservedAtMs),
        DefeatBy = defeat.DefeatBy
    };
}

/// <summary>The player's chain at a moment (kinds 3 and 9): the last chain notice while the chain timer runs.</summary>
internal static class ChainNow
{
    /// <summary>
    /// (chainNumber, chainAgeMs): the last notice number while the timer runs, 0 when no chain runs, null number when
    /// a chain runs but no notice was seen this visit.
    /// </summary>
    public static (uint? Number, uint? AgeMs) At(uint? lastNumber, long? lastAtMs, bool chainRunning, long nowMs)
    {
        if (!chainRunning)
            return (0, null);
        if (lastNumber is not { } number || lastAtMs is not { } at)
            return (null, null);
        return (number, (uint)Math.Clamp(nowMs - at, 0, uint.MaxValue));
    }
}

/// <summary>
/// FATE reward window: a FATE in the zone that reached 100 % or the Ended state, seen at most <see cref="HoldMs"/>
/// ago. Its rewards ("You obtain" crystals, lockboxes, ...) arrive around then and must not be linked to a kill.
/// Fed from the FATE scan and again at each loot notice.
/// </summary>
internal sealed class FateRewardWindow
{
    public const long HoldMs = 5000;

    private readonly Dictionary<uint, (long LastMs, float X, float Z)> finishing = new();
    private Guid visitId;

    public static bool IsFinishing(byte state, byte progress) =>
        state != FateLifecycle.Failed && (state == FateLifecycle.Ended || progress >= 100);

    public void Observe(Guid visit, IEnumerable<FateSnapshot> fates, long nowMs)
    {
        if (visit != visitId)
        {
            finishing.Clear();
            visitId = visit;
        }

        foreach (var fate in fates)
            if (IsFinishing(fate.State, fate.Progress))
                finishing[fate.FateId] = (nowMs, fate.X, fate.Z);
    }

    /// <summary>The open window's FATE id (nearest to the player when several), else null.</summary>
    public uint? Open(Guid visit, long nowMs, float playerX, float playerZ)
    {
        if (visit != visitId)
            return null;
        uint? best = null;
        var bestDistance = float.MaxValue;
        foreach (var (id, (lastMs, x, z)) in finishing)
        {
            if (nowMs - lastMs > HoldMs)
                continue;
            var distance = (x - playerX) * (x - playerX) + (z - playerZ) * (z - playerZ);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = id;
            }
        }

        return best;
    }

    public void Clear()
    {
        finishing.Clear();
        visitId = Guid.Empty;
    }
}

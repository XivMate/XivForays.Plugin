using System;
using System.Collections.Generic;
using System.Linq;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>
/// The Eurekan kettle notices (Pagos, Pyros) by LogMessage row id. The rows are the same in every client language,
/// so no text is read. Rows from the LogMessage sheet: 9020 to 9026 "Your Eurekan kettle is suffused with a feeble /
/// gentle / bright / brilliant / blinding light" and "the light of a newborn / an exploding star", 9027 "cannot
/// accumulate any more vitiated aether", 3743 forge conversion, 3741 forge refused (not enough aether).
/// </summary>
internal static class KettleRows
{
    public const uint Feeble = 9020;
    public const uint ExplodingStar = 9026;
    public const uint Full = 9027;
    public const uint ForgeConverted = 3743;
    public const uint ForgeRefused = 3741;

    public static bool IsKettleRow(uint row) => row is >= Feeble and <= Full or ForgeConverted or ForgeRefused;

    /// <summary>1 feeble, 2 gentle, 3 bright, 4 brilliant, 5 blinding, 6 newborn star, 7 exploding star; null for other rows.</summary>
    public static byte? Tier(uint row) => row is >= Feeble and <= ExplodingStar ? (byte)(row - Feeble + 1) : null;

    /// <summary>The kettle exists in Pagos and Pyros only (Anemos and Hydatos have none).</summary>
    public static bool InKettleZone(uint territory) => territory is ForayZones.EurekaPagos or ForayZones.EurekaPyros;
}

/// <summary>One kill (own or party) as the linker sees it. Built from a resolved or unresolved kind 13 record.</summary>
internal readonly record struct LightKill(
    long AtMs, Guid? TrackId, uint? BnpcId, byte? CombatLevel, byte? ElementalLevel, byte? Form, byte? By,
    uint? Chain, uint? ChainAgeMs, ushort? FateId)
{
    public static LightKill From(Observation defeat) => new(defeat.ObservedAtMs, defeat.EnemyTrackId, defeat.GameId,
        defeat.EnemyCombatLevel, defeat.EnemyElementalLevel, defeat.Form, defeat.DefeatBy, defeat.ChainNumber,
        defeat.ChainAgeMs, defeat.FateId);
}

/// <summary>A FATE that reached 100 % or an end state, first seen at <see cref="AtMs"/>.</summary>
internal readonly record struct LightFate(long AtMs, uint FateId, byte Credit);

internal enum LightLinkKind : byte
{
    None = 0,
    Kill = 1,
    Fate = 2,
    Ambiguous = 3
}

internal readonly record struct LightLinkResult(
    LightLinkKind Kind, int Candidates, LightKill? Kill, LightFate? Fate, uint? AgeMs, Guid[]? CandidateTracks);

/// <summary>
/// Links a light change to its cause. Candidates are the kills and FATE completions in the <see cref="WindowMs"/>
/// before the change. Exactly one candidate is linked; several are reported as ambiguous with their count (and the kill
/// track ids), none is guessed. Pure so it can be tested.
/// </summary>
internal static class LightLinker
{
    /// <summary>The window used by the log study (3.5 s from the kill to the light message).</summary>
    public const long WindowMs = 3500;

    public static LightLinkResult Link(long atMs, IReadOnlyList<LightKill> kills, IReadOnlyList<LightFate> fates)
    {
        var k = kills.Where(x => atMs - x.AtMs is >= 0 and <= WindowMs).OrderByDescending(x => x.AtMs).ToList();
        var f = fates.Where(x => atMs - x.AtMs is >= 0 and <= WindowMs).OrderByDescending(x => x.AtMs).ToList();
        var count = k.Count + f.Count;
        if (count == 0)
            return new LightLinkResult(LightLinkKind.None, 0, null, null, null, null);
        if (k.Count == 1 && f.Count == 0)
            return new LightLinkResult(LightLinkKind.Kill, 1, k[0], null, Age(atMs, k[0].AtMs), null);
        if (k.Count == 0 && f.Count == 1)
            return new LightLinkResult(LightLinkKind.Fate, 1, null, f[0], Age(atMs, f[0].AtMs), null);
        var tracks = k.Where(x => x.TrackId != null).Select(x => x.TrackId!.Value).Take(8).ToArray();
        return new LightLinkResult(LightLinkKind.Ambiguous, count, null, null, null, tracks.Length == 0 ? null : tracks);
    }

    private static uint Age(long atMs, long thenMs) => (uint)Math.Max(0, atMs - thenMs);
}

/// <summary>The player's state when a notice or value change came (numbers only).</summary>
internal readonly record struct LightSnapshot(uint? Chain, uint? ChainAgeMs, byte GroupSize);

/// <summary>A finished light event, ready to become a kind 20 record.</summary>
internal sealed record LightEvent(
    long AtMs, Guid VisitId, uint TerritoryId, uint? Row, byte? Tier, bool Full, bool Forge, uint? Before,
    uint? After, LightSnapshot Snapshot, LightLinkResult Link)
{
    public byte Source => (byte)((Row != null ? 1 : 0) | (Before != null || After != null ? 2 : 0));
    public int? Delta => Before is { } b && After is { } a ? (int)a - (int)b : null;

    /// <summary>The kind 20 record on top of <paramref name="context"/> (zone, map, weather, levels).</summary>
    public Observation ToObservation(Observation context)
    {
        var record = context with
        {
            Kind = ObservationKind.KettleLight,
            ObservedAtMs = AtMs,
            VisitId = VisitId,
            TerritoryId = TerritoryId,
            NoticeRow = Row,
            LightTier = Tier,
            LightFull = Full ? true : null,
            ForgeEmptied = Forge ? true : null,
            LightBefore = Before,
            LightAfter = After,
            LightDelta = Delta,
            LightSource = Source,
            LightLink = (byte)Link.Kind,
            LightCandidates = (byte)Math.Min(Link.Candidates, byte.MaxValue),
            LightCandidateTracks = Link.CandidateTracks,
            LightLinkAgeMs = Link.AgeMs,
            GroupSize = Snapshot.GroupSize,
            ChainNumber = Snapshot.Chain,
            ChainAgeMs = Snapshot.ChainAgeMs
        };
        if (Link.Kill is { } kill)
            record = record with
            {
                GameId = kill.BnpcId,
                EnemyCombatLevel = kill.CombatLevel,
                EnemyElementalLevel = kill.ElementalLevel,
                Form = kill.Form,
                IsMutated = kill.Form is { } m ? (m & 1) != 0 : null,
                IsAdapted = kill.Form is { } a ? (a & 2) != 0 : null,
                DefeatBy = kill.By,
                DefeatTrackId = kill.TrackId,
                // The chain at the kill (the notice before it), not at the light message.
                ChainNumber = kill.Chain,
                ChainAgeMs = kill.ChainAgeMs,
                FateId = kill.FateId
            };
        else if (Link.Fate is { } fate)
            record = record with { FateRewardId = fate.FateId, LightFateCredit = fate.Credit };
        return record;
    }
}

/// <summary>
/// Turns kettle notices (row ids) and kettle value changes into <see cref="LightEvent"/>s. A notice and a value change
/// within <see cref="MergeMs"/> are one event. An event is finished <see cref="HoldMs"/> after it began, so a kill whose
/// enemy is still being looked up (up to 1.5 s) can be linked. No game access: callers feed it. A null value (the
/// source cannot be read) still gives notice-only events.
/// </summary>
internal sealed class LightTracker
{
    public const long MergeMs = 1000;
    public const long HoldMs = 1600;
    private const long KeepMs = 10_000;

    private sealed class Pending
    {
        public long AtMs;
        public uint? Row;
        public uint? Before;
        public uint? After;
        public bool HasValue;
        public bool Full;
        public bool Forge;
        public LightSnapshot Snapshot;
    }

    private readonly List<LightKill> kills = new();
    private readonly List<LightFate> fates = new();
    private readonly List<Pending> pending = new();
    private uint? lastValue;
    private Guid visitId;
    private uint territory;

    public int PendingCount => pending.Count;

    /// <summary>A kill (own or party). Kept for 10 s.</summary>
    public void NoteKill(Guid visit, uint territoryId, LightKill kill)
    {
        Visit(visit, territoryId);
        kills.Add(kill);
        Trim(kill.AtMs);
    }

    /// <summary>A FATE that just reached 100 % or an end state. Call once per FATE.</summary>
    public void NoteFate(Guid visit, uint territoryId, LightFate fate)
    {
        Visit(visit, territoryId);
        fates.Add(fate);
        Trim(fate.AtMs);
    }

    /// <summary>The kettle value read now. The first value of a visit is a baseline, not a change.</summary>
    public void Value(long nowMs, Guid visit, uint territoryId, uint? value, LightSnapshot snapshot)
    {
        Visit(visit, territoryId);
        if (value is not { } v)
            return;
        if (lastValue is not { } before)
        {
            lastValue = v;
            return;
        }

        if (v == before)
            return;
        lastValue = v;
        var open = Open(nowMs, p => !p.HasValue);
        if (open == null)
        {
            open = new Pending { AtMs = nowMs, Snapshot = snapshot };
            pending.Add(open);
        }

        open.HasValue = true;
        open.Before = before;
        open.After = v;
    }

    /// <summary>A kettle LogMessage row.</summary>
    public void Notice(long nowMs, Guid visit, uint territoryId, uint row, LightSnapshot snapshot)
    {
        Visit(visit, territoryId);
        if (!KettleRows.IsKettleRow(row) || row == KettleRows.ForgeRefused)
            return;
        var open = Open(nowMs, p => p.Row == null);
        if (open == null)
        {
            open = new Pending { AtMs = nowMs, Snapshot = snapshot };
            pending.Add(open);
        }

        open.Row = row;
        open.Full = row == KettleRows.Full;
        open.Forge = row == KettleRows.ForgeConverted;
        // The notice time wins: it is what the kill link is measured against.
        open.AtMs = Math.Min(open.AtMs, nowMs);
        if (open.Forge && !open.HasValue && lastValue is { } known)
        {
            // The forge empties the kettle; the value change (if readable) merges in on its own.
            open.Before = known;
        }
    }

    /// <summary>Finished events (older than <see cref="HoldMs"/>), oldest first.</summary>
    public List<LightEvent> Tick(long nowMs)
    {
        var done = new List<LightEvent>();
        for (var i = 0; i < pending.Count; i++)
        {
            if (nowMs - pending[i].AtMs < HoldMs)
                continue;
            done.Add(Finish(pending[i]));
            pending.RemoveAt(i--);
        }

        return done;
    }

    /// <summary>Finishes everything now (leaving the zone or turning recording off).</summary>
    public List<LightEvent> Flush()
    {
        var done = pending.Select(Finish).ToList();
        pending.Clear();
        return done;
    }

    public void Clear()
    {
        pending.Clear();
        kills.Clear();
        fates.Clear();
        lastValue = null;
        visitId = Guid.Empty;
    }

    private void Visit(Guid visit, uint territoryId)
    {
        if (visit == visitId)
            return;
        visitId = visit;
        territory = territoryId;
        kills.Clear();
        fates.Clear();
        lastValue = null;
    }

    private Pending? Open(long nowMs, Func<Pending, bool> wants) =>
        pending.FirstOrDefault(p => nowMs - p.AtMs is >= 0 and <= MergeMs && wants(p));

    private LightEvent Finish(Pending p)
    {
        var link = LightLinker.Link(p.AtMs, kills, fates);
        return new LightEvent(p.AtMs, visitId, territory, p.Row, p.Row is { } r ? KettleRows.Tier(r) : null, p.Full,
            p.Forge, p.Before, p.After, p.Snapshot, link);
    }

    private void Trim(long nowMs)
    {
        kills.RemoveAll(k => nowMs - k.AtMs > KeepMs);
        fates.RemoveAll(f => nowMs - f.AtMs > KeepMs);
    }
}

/// <summary>Detects the first time a FATE is seen finishing, so one completion is one candidate.</summary>
internal sealed class FateCompletionWatch
{
    private readonly HashSet<uint> seen = new();
    private Guid visit;

    /// <summary>The FATEs that finished since the last call; credit is 1 when the player is inside the radius.</summary>
    public List<LightFate> Observe(Guid visitId, IEnumerable<FateSnapshot> fates, long nowMs, float playerX, float playerZ)
    {
        if (visit != visitId)
        {
            seen.Clear();
            visit = visitId;
        }

        var result = new List<LightFate>();
        foreach (var fate in fates)
        {
            if (!FateRewardWindow.IsFinishing(fate.State, fate.Progress))
            {
                seen.Remove(fate.FateId);
                continue;
            }

            if (!seen.Add(fate.FateId))
                continue;
            var dx = fate.X - playerX;
            var dz = fate.Z - playerZ;
            result.Add(new LightFate(nowMs, fate.FateId, (byte)(dx * dx + dz * dz <= fate.Radius * fate.Radius ? 1 : 0)));
        }

        return result;
    }
}

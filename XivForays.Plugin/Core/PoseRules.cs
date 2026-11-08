using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

// Pose capture v2 rules (docs/CAPTURE_DESIGN.md §3), dependency-free so they can be unit tested. The game reads live
// in Services/PoseCaptureService.cs; everything here works on plain numbers.

/// <summary>Constants of the pose capture (design §3/§4).</summary>
internal static class PoseRates
{
    /// <summary>At most this many full reads per 500 ms enemy scan (priority form-after > spawn > refresh).</summary>
    public const int MaxFullReadsPerScan = 6;

    public const long MinSeenMs = 1500;
    public const long StillMs = 1000;
    public const float StillYalms = 0.05f;
    public const float StillDegrees = 1f;

    /// <summary>
    /// Within this distance of the player the skeleton tilt is trusted (collisionLoaded / tiltTrusted). Trial
    /// 2026-11-08 11:20 (Squib 8174, Anemos): tilts at 68-76 y were real (12.7, 13.5 degrees on a slope) but poses at
    /// 101-117 y on slopes read 0, so the game does not settle at 120 y. 90 y keeps a margin under 101.
    /// </summary>
    public const float NearYalms = 90f;

    public const long FormDebounceMs = 1500;
    public const long FormMaxWaitMs = 5000;

    /// <summary>A cached full read older than this is refreshed round-robin (it is the "before" of a form change).</summary>
    public const long RefreshMs = 3000;

    public const long StartWindowMs = 5000;
    public const float SpawningYalms = 80f;

    public const long IdleDurationMs = 60_000;
    public const int IdleStepMs = 2000;
    public const int IdleMaxSamples = 64;

    public const long AggroLinkMs = 1500;
    public const float AggroLinkYalms = 40f;

    public const float RespawnAnchorYalms = 1.5f;
    public const long RespawnMaxWaitMs = 30 * 60 * 1000;
    public const int RespawnMaxPending = 512;

    public const int DedupeMax = 50_000;
}

/// <summary>Who an object is, relative to the local player. One byte on the wire: never a name or an id.</summary>
internal enum ObjectCategory : byte
{
    None = 0,
    Self = 1,

    /// <summary>Own party or alliance member.</summary>
    Party = 2,

    OtherPlayer = 3,

    /// <summary>Own pet, companion or chocobo.</summary>
    OwnPet = 4,

    OtherPet = 5,

    /// <summary>NPC or enemy.</summary>
    Npc = 6,

    /// <summary>Set but not in the object table and not in a known range (e.g. out of view).</summary>
    Unknown = 7
}

internal enum ObjectClass : byte
{
    Player,

    /// <summary>A pet, companion or chocobo (has an owner).</summary>
    Owned,

    Npc
}

/// <summary>
/// Maps game object ids and entity ids to <see cref="ObjectCategory"/>, filled from the object table once per scan.
/// The ids stay in this object (memory only); only categories come out.
/// </summary>
internal sealed class CategoryResolver
{
    private const uint InvalidEntity = 0xE0000000;
    private readonly Dictionary<uint, (ObjectClass Class, uint Owner, bool PartyFlag)> byEntity = new();
    private readonly HashSet<uint> party = new();
    private uint localEntityId = InvalidEntity;

    public bool Filled { get; private set; }

    public void Clear()
    {
        byEntity.Clear();
        party.Clear();
        localEntityId = InvalidEntity;
        Filled = false;
    }

    public void SetLocal(uint entityId)
    {
        localEntityId = entityId;
        Filled = true;
    }

    public void AddPartyMember(uint entityId)
    {
        if (entityId != 0 && entityId != InvalidEntity)
            party.Add(entityId);
    }

    public void Add(uint entityId, ObjectClass objectClass, uint ownerEntityId, bool partyFlag)
    {
        if (entityId != 0 && entityId != InvalidEntity)
            byEntity[entityId] = (objectClass, ownerEntityId, partyFlag);
    }

    /// <summary>A GameObjectId (TargetId, CombatTaggerId): its low 32 bits are the entity id for networked objects.</summary>
    public ObjectCategory OfObjectId(ulong gameObjectId) => OfEntityId((uint)gameObjectId);

    public ObjectCategory OfEntityId(uint entityId)
    {
        if (entityId == 0 || entityId == InvalidEntity)
            return ObjectCategory.None;
        if (entityId == localEntityId)
            return ObjectCategory.Self;
        if (byEntity.TryGetValue(entityId, out var info))
        {
            return info.Class switch
            {
                ObjectClass.Player => info.PartyFlag || party.Contains(entityId) ? ObjectCategory.Party : ObjectCategory.OtherPlayer,
                ObjectClass.Owned when info.Owner == localEntityId => ObjectCategory.OwnPet,
                ObjectClass.Owned when IsPlayer(info.Owner) => ObjectCategory.OtherPet,
                _ => ObjectCategory.Npc
            };
        }

        if (party.Contains(entityId))
            return ObjectCategory.Party;
        // Not in view: player entity ids are in the 0x10000000 range, BNpcs in 0x40000000.
        return (entityId & 0xF0000000) == 0x10000000 ? ObjectCategory.OtherPlayer : ObjectCategory.Unknown;
    }

    private bool IsPlayer(uint entityId) =>
        entityId != 0 && entityId != InvalidEntity &&
        (byEntity.TryGetValue(entityId, out var owner) ? owner.Class == ObjectClass.Player : (entityId & 0xF0000000) == 0x10000000);
}

/// <summary>Settle state of one track (design §3 "Settle"). Fed once per 500 ms scan.</summary>
internal sealed class SettleState
{
    private Vector3 lastPosition;
    private float lastHeading;
    private bool hasLast;

    public SettleState(long firstSeenMs) => FirstSeenMs = firstSeenMs;

    public long FirstSeenMs { get; }
    public long StillSinceMs { get; private set; }

    /// <summary>Moved more than 0.05 y or turned more than 1 degree since the previous scan.</summary>
    public bool Moving { get; private set; }

    /// <param name="calm">Out of combat: no target, HP = max, no InCombat flag.</param>
    public void Update(long nowMs, Vector3 position, float heading, bool calm)
    {
        Moving = hasLast && (Vector3.Distance(position, lastPosition) >= PoseRates.StillYalms ||
                             PoseMath.HeadingDeltaDeg(heading, lastHeading) >= PoseRates.StillDegrees);
        if (!hasLast || Moving || !calm)
            StillSinceMs = nowMs;
        lastPosition = position;
        lastHeading = heading;
        hasLast = true;
    }

    public bool IsStill(long nowMs) => hasLast && nowMs - StillSinceMs >= PoseRates.StillMs;

    public bool IsSettled(long nowMs, bool calm, bool drawReady, bool formPending) =>
        nowMs - FirstSeenMs >= PoseRates.MinSeenMs && calm && drawReady && !formPending && IsStill(nowMs);
}

internal enum PoseTake
{
    None,
    First,
    NearRetake
}

/// <summary>Spawn pose decisions per track: one pose, plus at most one near retake (design §3 "Near retake").</summary>
internal sealed class SpawnPoseState
{
    public int Attempts { get; private set; }
    public bool FirstWasFar { get; private set; }
    public bool RetakeDone { get; private set; }

    /// <summary>The position of the first accepted (non-duplicate) pose, for "moved since settled".</summary>
    public Vector3? SettledPosition { get; set; }

    public PoseTake Decide(bool settled, float playerDistance)
    {
        if (!settled)
            return PoseTake.None;
        if (Attempts == 0)
            return PoseTake.First;
        return FirstWasFar && !RetakeDone && playerDistance <= PoseRates.NearYalms ? PoseTake.NearRetake : PoseTake.None;
    }

    /// <summary>Marks the decision as used, whether or not the content dedupe let the record through.</summary>
    public void Taken(PoseTake take, float playerDistance)
    {
        if (take == PoseTake.First)
        {
            Attempts = 1;
            FirstWasFar = playerDistance > PoseRates.NearYalms;
        }
        else if (take == PoseTake.NearRetake)
        {
            Attempts++;
            RetakeDone = true;
        }
    }
}

internal enum LayoutDecision
{
    /// <summary>No pose yet for this spawn instance: record it.</summary>
    Record,

    /// <summary>Only an untrusted (far) pose so far and this one is trusted: record it once as the upgrade re-take.</summary>
    Upgrade,

    /// <summary>Already covered: a trusted pose, or an untrusted one and this is untrusted too (or the upgrade was used).</summary>
    Skip
}

/// <summary>
/// Trial fix 2026-11-08-1126 item 2: ONE spawn pose per (layoutId, spawn instance) per visit, whatever the tracks do
/// (walking out of range and back makes new tracks). At most one upgrade re-take when the first was untrusted. A
/// death of that layout ends the instance, so its respawn gets a new pose. Layout 0 falls back to the content key.
/// </summary>
internal sealed class LayoutPoseRegistry
{
    private readonly Dictionary<uint, (bool Trusted, bool Upgraded)> layouts = new();
    private readonly int max;

    public LayoutPoseRegistry(int max = PoseRates.DedupeMax) => this.max = max;

    public int Count => layouts.Count;

    public void Clear() => layouts.Clear();

    /// <summary>Decides and, unless skipped, records the pose for this layout.</summary>
    public LayoutDecision TryRecord(uint layoutId, bool trusted)
    {
        if (layoutId == 0)
            return LayoutDecision.Record;
        if (!layouts.TryGetValue(layoutId, out var state))
        {
            if (layouts.Count >= max)
                layouts.Clear();
            layouts[layoutId] = (trusted, false);
            return LayoutDecision.Record;
        }

        if (state.Trusted || state.Upgraded || !trusted)
            return LayoutDecision.Skip;
        layouts[layoutId] = (true, true);
        return LayoutDecision.Upgrade;
    }

    /// <summary>The instance at this layout died: its respawn is a new instance.</summary>
    public void Died(uint layoutId) => layouts.Remove(layoutId);
}

/// <summary>Content key per visit (design §3 "Dedupe" b): the same idle mob re-entering view is not recorded again.</summary>
internal readonly record struct PoseKey(uint BnpcId, int ModelCharaId, byte Form, int X2, int Z2, int Heading3, bool CollisionLoaded)
{
    public static PoseKey Of(uint bnpcId, int modelCharaId, byte form, float x, float z, float headingRadians, bool collisionLoaded) =>
        new(bnpcId, modelCharaId, form, (int)MathF.Round(x * 2), (int)MathF.Round(z * 2),
            (int)MathF.Round(PoseMath.HeadingDeg(headingRadians) / 3) % 120, collisionLoaded);
}

internal sealed class PoseDedupe
{
    private readonly HashSet<PoseKey> seen = new();
    private readonly int max;

    public PoseDedupe(int max = PoseRates.DedupeMax) => this.max = max;

    public int Count => seen.Count;

    public void Clear() => seen.Clear();

    /// <summary>True the first time a key is seen this visit. A full set is cleared (bounded memory).</summary>
    public bool TryAdd(PoseKey key)
    {
        if (seen.Count >= max)
            seen.Clear();
        return seen.Add(key);
    }
}

/// <summary>The cheap per-scan form key (design §3 "Form change"). NaN draw scale / 0 skeleton = unknown (not loaded).</summary>
internal readonly record struct FormKey(
    int ModelCharaId, float Scale, float DrawScale, uint NameId, uint BnpcId, bool Mutated, bool Adapted, nint Skeleton)
{
    public const int ModelBit = 1, ScaleBit = 2, DrawScaleBit = 4, NameBit = 8, BnpcBit = 16, MutatedBit = 32,
        AdaptedBit = 64, SkeletonBit = 128;

    /// <summary>Changed bits between two keys. A part unknown on either side (not loaded yet) is not a change.</summary>
    public static int Diff(FormKey a, FormKey b)
    {
        var bits = 0;
        if (a.ModelCharaId != b.ModelCharaId)
            bits |= ModelBit;
        if (MathF.Abs(a.Scale - b.Scale) > 1e-4f)
            bits |= ScaleBit;
        if (float.IsFinite(a.DrawScale) && float.IsFinite(b.DrawScale) && MathF.Abs(a.DrawScale - b.DrawScale) > 1e-4f)
            bits |= DrawScaleBit;
        if (a.NameId != b.NameId)
            bits |= NameBit;
        if (a.BnpcId != b.BnpcId)
            bits |= BnpcBit;
        if (a.Mutated != b.Mutated)
            bits |= MutatedBit;
        if (a.Adapted != b.Adapted)
            bits |= AdaptedBit;
        if (a.Skeleton != 0 && b.Skeleton != 0 && a.Skeleton != b.Skeleton)
            bits |= SkeletonBit;
        return bits;
    }

    /// <summary>Fills this key's unknown parts from <paramref name="known"/>.</summary>
    public FormKey FillFrom(FormKey known) => this with
    {
        DrawScale = float.IsFinite(DrawScale) ? DrawScale : known.DrawScale,
        Skeleton = Skeleton != 0 ? Skeleton : known.Skeleton
    };
}

/// <summary>
/// Debounces form-key changes: the after read is due 1.5 s after the LAST key change, at most 5 s after the first.
/// One record per debounced change; a change that reverted (no bits left) is dropped.
/// </summary>
internal sealed class FormWatch
{
    private FormKey last;
    private bool hasLast;

    public bool Pending { get; private set; }
    public FormKey Before { get; private set; }
    public long FirstChangeMs { get; private set; }
    public long LastChangeMs { get; private set; }

    /// <summary>Feeds this scan's key. Returns true when a new change starts (the caller snapshots "before").</summary>
    public bool Update(long nowMs, FormKey key)
    {
        if (!hasLast)
        {
            last = key;
            hasLast = true;
            return false;
        }

        var merged = key.FillFrom(last);
        var started = false;
        if (FormKey.Diff(last, merged) != 0)
        {
            if (!Pending)
            {
                Pending = true;
                Before = last;
                FirstChangeMs = nowMs;
                started = true;
            }

            LastChangeMs = nowMs;
        }

        last = merged;
        return started;
    }

    /// <summary>Starts a change from outside (a bnpcId change made the tracker start a new track).</summary>
    public void Begin(long nowMs, FormKey before, FormKey current)
    {
        Pending = true;
        Before = before;
        FirstChangeMs = nowMs;
        LastChangeMs = nowMs;
        last = current;
        hasLast = true;
    }

    public bool AfterDue(long nowMs) =>
        Pending && (nowMs - LastChangeMs >= PoseRates.FormDebounceMs || nowMs - FirstChangeMs >= PoseRates.FormMaxWaitMs);

    /// <summary>Ends the pending change with the after key; returns the changed bits (0 = reverted, drop it).</summary>
    public int Complete(FormKey after)
    {
        var bits = FormKey.Diff(Before, after.FillFrom(last));
        Pending = false;
        last = after.FillFrom(last);
        return bits;
    }
}

/// <summary>Picks the full reads of one scan: at most <see cref="PoseRates.MaxFullReadsPerScan"/>, by priority then age.</summary>
internal static class ReadBudget
{
    public const int FormAfter = 0, Spawn = 1, Refresh = 2;

    public static List<T> Pick<T>(IEnumerable<(int Priority, long Order, T Item)> candidates,
        int max = PoseRates.MaxFullReadsPerScan) =>
        candidates.OrderBy(c => c.Priority).ThenBy(c => c.Order).Take(max).Select(c => c.Item).ToList();
}

/// <summary>"presentAtStart" and "seenSpawning" (design §3), from the player's own positions of the last scans.</summary>
internal sealed class PlayerHistory
{
    private readonly Queue<(long Ms, Vector3 Position)> samples = new();

    public void Clear() => samples.Clear();

    public void Add(long nowMs, Vector3 position)
    {
        samples.Enqueue((nowMs, position));
        while (samples.Count > 0 && nowMs - samples.Peek().Ms > 6000)
            samples.Dequeue();
    }

    /// <summary>Yalms per second between the last two samples.</summary>
    public float Speed()
    {
        if (samples.Count < 2)
            return 0;
        var (prevMs, prev) = samples.ElementAt(samples.Count - 2);
        var (lastMs, last) = samples.Last();
        return lastMs > prevMs ? Vector3.Distance(prev, last) * 1000f / (lastMs - prevMs) : 0;
    }

    /// <summary>True when the history covers the last 5 s and every sample in it was within <paramref name="radius"/>.</summary>
    public bool StoodWithin(Vector3 point, float radius, long nowMs)
    {
        if (samples.Count == 0 || nowMs - samples.Peek().Ms < PoseRates.StartWindowMs - 600)
            return false;
        foreach (var (ms, position) in samples)
            if (nowMs - ms <= PoseRates.StartWindowMs && Vector3.Distance(point, position) > radius)
                return false;
        return true;
    }

    public static bool PresentAtStart(long firstSeenMs, long visitScanStartMs) =>
        firstSeenMs - visitScanStartMs <= PoseRates.StartWindowMs;

    public static bool SeenSpawning(bool presentAtStart, float firstSeenDistance, bool stoodWithin) =>
        !presentAtStart && firstSeenDistance <= PoseRates.SpawningYalms && stoodWithin;
}

/// <summary>Builds the 60 s idle track after a spawn pose (capture-pass mode).</summary>
internal sealed class IdleTrackBuilder
{
    private readonly List<float[]> samples = new();
    private readonly List<long[]> timelines = new();
    private long lastStepMs;
    private float[] lastKept;
    private float[]? pendingLast;
    private (int Timeline, int BaseOverride) lastTimeline;

    public IdleTrackBuilder(long startMs, Vector3 position, float heading, int timeline, int baseOverride)
    {
        StartMs = startMs;
        lastStepMs = startMs;
        lastKept = Sample(0, position, heading);
        samples.Add(lastKept);
        lastTimeline = (timeline, baseOverride);
        timelines.Add([0, timeline, baseOverride]);
    }

    public long StartMs { get; }
    public int SampleCount => samples.Count + (pendingLast == null ? 0 : 1);

    public bool Complete(long nowMs) => nowMs - StartMs >= PoseRates.IdleDurationMs;

    public void Step(long nowMs, Vector3 position, float heading, int timeline, int baseOverride)
    {
        var dt = nowMs - StartMs;
        if ((timeline, baseOverride) != lastTimeline && timelines.Count < PoseRates.IdleMaxSamples)
        {
            lastTimeline = (timeline, baseOverride);
            timelines.Add([dt, timeline, baseOverride]);
        }

        if (nowMs - lastStepMs < PoseRates.IdleStepMs)
            return;
        lastStepMs = nowMs;
        var sample = Sample(dt, position, heading);
        var moved = new Vector3(sample[1] - lastKept[1], sample[2] - lastKept[2], sample[3] - lastKept[3]).Length() >
                    PoseRates.StillYalms;
        var turned = PoseMath.HeadingDeltaDeg(sample[4], lastKept[4]) > PoseRates.StillDegrees;
        if ((moved || turned) && samples.Count < PoseRates.IdleMaxSamples - 1)
        {
            samples.Add(sample);
            lastKept = sample;
            pendingLast = null;
        }
        else
        {
            pendingLast = sample;
        }
    }

    public EnemyIdleTrack Finish(byte endReason)
    {
        var all = new List<float[]>(samples);
        if (pendingLast != null)
            all.Add(pendingLast);
        return new EnemyIdleTrack
        {
            StartMs = StartMs,
            StepMs = PoseRates.IdleStepMs,
            Samples = all.ToArray(),
            Timelines = timelines.ToArray(),
            EndReason = endReason
        };
    }

    // Heading is kept in radians (4 dp) like GameObject.Rotation.
    private static float[] Sample(long dt, Vector3 p, float heading) =>
        [dt, PoseMath.R3(p.X), PoseMath.R3(p.Y), PoseMath.R3(p.Z), PoseMath.R4(heading)];
}

/// <summary>
/// Holds aggro edges for 1.5 s so each record can list the other enemies that entered combat within ±1.5 s and 40 y
/// (bnpcId + the random enemyTrackId only).
/// </summary>
internal sealed class AggroCorrelator
{
    private readonly List<Edge> edges = new();

    public int PendingCount => edges.Count(e => !e.Emitted);

    public void Clear() => edges.Clear();

    public void Add(long atMs, uint bnpcId, Guid trackId, Vector3 position, byte targetCategory, Observation record) =>
        edges.Add(new Edge(atMs, bnpcId, trackId, position, targetCategory, record));

    /// <summary>Emits every edge at least 1.5 s old, with its links. <paramref name="force"/> emits all (visit change).</summary>
    public List<Observation> Flush(long nowMs, bool force = false)
    {
        var result = new List<Observation>();
        foreach (var edge in edges)
        {
            if (edge.Emitted || (!force && nowMs - edge.AtMs < PoseRates.AggroLinkMs))
                continue;
            edge.Emitted = true;
            var links = edges
                .Where(o => !ReferenceEquals(o, edge) && o.TrackId != edge.TrackId &&
                            Math.Abs(o.AtMs - edge.AtMs) <= PoseRates.AggroLinkMs &&
                            Vector3.Distance(o.Position, edge.Position) <= PoseRates.AggroLinkYalms)
                .Select(o => new AggroLink
                {
                    BnpcId = o.BnpcId,
                    EnemyTrackId = o.TrackId,
                    DtMs = o.AtMs - edge.AtMs,
                    Distance = PoseMath.R2(Vector3.Distance(o.Position, edge.Position)),
                    TargetCategory = o.TargetCategory
                }).ToArray();
            var record = edge.Record;
            if (record.Aggro != null)
                record = record with { Aggro = record.Aggro with { Linked = links } };
            result.Add(record);
        }

        edges.RemoveAll(e => e.Emitted && nowMs - e.AtMs > 2 * PoseRates.AggroLinkMs);
        return result;
    }

    private sealed class Edge(long atMs, uint bnpcId, Guid trackId, Vector3 position, byte targetCategory, Observation record)
    {
        public long AtMs { get; } = atMs;
        public uint BnpcId { get; } = bnpcId;
        public Guid TrackId { get; } = trackId;
        public Vector3 Position { get; } = position;
        public byte TargetCategory { get; } = targetCategory;
        public Observation Record { get; } = record;
        public bool Emitted { get; set; }
    }
}

/// <summary>
/// Death -> respawn (task 2026-11-08): remembers deaths per visit and matches a later appearance of the same bnpcId
/// at the same spawn point (non-zero layoutId and/or a spawn anchor within 1.5 y).
/// </summary>
internal sealed class RespawnMatcher
{
    private readonly List<Death> deaths = new();

    public int PendingCount => deaths.Count;

    public void Clear() => deaths.Clear();

    public void Died(Guid trackId, uint bnpcId, uint layoutId, Vector3 anchor, byte form, long diedAtMs, Vector3 player)
    {
        if (deaths.Count >= PoseRates.RespawnMaxPending)
            deaths.RemoveAt(0);
        deaths.Add(new Death(trackId, bnpcId, layoutId, anchor, form, diedAtMs) { MaxPlayerDistance = Vector3.Distance(anchor, player) });
    }

    /// <summary>Per scan: the player's farthest distance from each pending death's anchor; expires old deaths.</summary>
    public void Tick(long nowMs, Vector3 player)
    {
        deaths.RemoveAll(d => nowMs - d.DiedAtMs > PoseRates.RespawnMaxWaitMs);
        foreach (var death in deaths)
            death.MaxPlayerDistance = MathF.Max(death.MaxPlayerDistance, Vector3.Distance(death.Anchor, player));
    }

    public EnemyRespawn? Appeared(uint bnpcId, uint layoutId, Vector3 anchor, byte form, long nowMs)
    {
        Death? best = null;
        byte bestBy = 0;
        var bestDistance = float.MaxValue;
        foreach (var death in deaths)
        {
            if (death.BnpcId != bnpcId || death.DiedAtMs > nowMs)
                continue;
            var distance = Vector3.Distance(death.Anchor, anchor);
            byte by = 0;
            if (layoutId != 0 && death.LayoutId == layoutId)
                by |= 1;
            if (distance <= PoseRates.RespawnAnchorYalms)
                by |= 2;
            if (by == 0)
                continue;
            // Prefer both, then the anchor, then the layout alone; then the nearest anchor.
            if (Rank(by) > Rank(bestBy) || (Rank(by) == Rank(bestBy) && distance < bestDistance))
            {
                best = death;
                bestBy = by;
                bestDistance = distance;
            }
        }

        if (best == null)
            return null;
        deaths.Remove(best);
        return new EnemyRespawn
        {
            PreviousTrackId = best.TrackId,
            DiedAtMs = best.DiedAtMs,
            IntervalMs = nowMs - best.DiedAtMs,
            MatchedBy = bestBy,
            AnchorDistance = PoseMath.R3(bestDistance),
            SameForm = best.Form == form,
            MaxPlayerDistance = PoseMath.R2(best.MaxPlayerDistance)
        };
    }

    private static int Rank(byte by) => by switch { 3 => 3, 2 => 2, 1 => 1, _ => 0 };

    private sealed record Death(Guid TrackId, uint BnpcId, uint LayoutId, Vector3 Anchor, byte Form, long DiedAtMs)
    {
        public float MaxPlayerDistance { get; set; }
    }
}

/// <summary>Name-free one-line summaries for dalamud.log and the Debug tab (numbers only).</summary>
internal static class PoseLog
{
    public static string Line(Observation o)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var layout = o.LayoutId ?? 0;
        switch (o.Kind)
        {
            case ObservationKind.EnemySpawnPose when o.Pose is { } p:
                var dy = p.DrawPosition is { Length: 3 } d && p.Position.Length == 3 ? d[1] - p.Position[1] : 0;
                return string.Format(inv,
                    "pose 15 bnpc {0} layout {1} near {2} tilt {3:0.0} dy {4:0.000} form {5} start {6} spawning {7} home {8} fate {9} dist {10:0.0} retake {11} homeDist {12:0.00} firstHome {13:0.00} firstMoved {14:0.00} seenFor {15:0.0}",
                    p.BnpcId, layout, p.CollisionLoaded ? 1 : 0, p.SkeletonTilt ?? 0, dy, p.Form, p.PresentAtStart ? 1 : 0,
                    p.SeenSpawning ? 1 : 0, p.HomePosition == null ? 0 : 1, p.FateId ?? 0, p.PlayerDistance, p.Retake ? 1 : 0,
                    p.HomeDistance ?? -1, p.FirstSeenHomeDistance ?? -1, p.FirstSeenMoved ?? -1, p.SeenForMs / 1000.0);
            case ObservationKind.EnemyFormChange when o.FormChange is { } f:
                return string.Format(inv, "pose 16 bnpc {0} layout {1} changed 0x{2:X2} dy {3:0.000} offset {4:0.000} settled {5} before {6}",
                    o.GameId ?? 0, layout, f.Changed, f.DrawHeightDelta ?? 0, f.DrawOffsetDelta ?? 0, f.AfterSettled ? 1 : 0,
                    f.Before == null ? 0 : 1);
            case ObservationKind.EnemyIdleTrack when o.Idle is { } i:
                return string.Format(inv, "pose 17 bnpc {0} samples {1} timelines {2} end {3}",
                    o.GameId ?? 0, i.Samples.Length, i.Timelines.Length, i.EndReason);
            case ObservationKind.EnemyAggro when o.Aggro is { } a:
                return string.Format(inv,
                    "pose 18 bnpc {0} layout {1} target {2} dist {3:0.0} facing {4:0} playerFacing {5:0} dy {6:0.0} moving {7} casting {8} linked {9}",
                    o.GameId ?? 0, layout, a.TargetCategory, a.DistanceToPlayer, a.EnemyFacingToPlayerDeg, a.PlayerFacingToEnemyDeg,
                    a.Dy, a.Player.Moving ? 1 : 0, a.Player.Casting == null ? 0 : 1, a.Linked.Length);
            case ObservationKind.EnemyAppeared when o.Respawn is { } r:
                return string.Format(inv, "pose respawn bnpc {0} layout {1} after {2:0.0} s by {3} anchor {4:0.00} sameForm {5} maxPlayer {6:0}",
                    o.GameId ?? 0, layout, r.IntervalMs / 1000.0, r.MatchedBy, r.AnchorDistance, r.SameForm ? 1 : 0, r.MaxPlayerDistance);
            case ObservationKind.EnemyDied:
                return string.Format(inv, "pose died bnpc {0} layout {1} claim {2} type {3} lastTarget {4} fate {5}",
                    o.GameId ?? 0, layout, o.Claim ?? 0, o.ClaimType ?? 0, o.LastTargetCategory ?? 0, o.FateId ?? 0);
            default:
                return string.Format(inv, "pose {0} bnpc {1}", (int)o.Kind, o.GameId ?? 0);
        }
    }
}

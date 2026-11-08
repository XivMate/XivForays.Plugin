using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Lumina.Excel.Sheets;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

/// <summary>
/// Pose capture v2 (docs/CAPTURE_DESIGN.md): spawn poses (15), form changes (16), idle tracks (17, capture pass only),
/// aggro edges (18) and death -> respawn matches (on kind 1). Runs inside the 500 ms enemy scan on the framework
/// thread. READ-ONLY: plain memory reads of the object table and draw objects; no writes, no hooks, no game function
/// calls (no virtual calls, no BGCollision raycast: line of sight stays off). At most
/// <see cref="PoseRates.MaxFullReadsPerScan"/> full reads per scan. Names, content ids and object ids never leave this
/// class: other objects are categories, enemies are bnpcId + the random enemyTrackId.
/// </summary>
public sealed class PoseCaptureService(IObjectTable objectTable, IPartyList partyList, IDataManager dataManager, IPluginLog log)
{
    private const int RecentMax = 40;

    private readonly Lazy<HashSet<uint>> sprintIds = new(() => FindStatuses(dataManager, "Sprint"));
    private readonly Lazy<HashSet<uint>> stealthIds = new(() =>
        FindStatuses(dataManager, "Sneak", "Stealth", "Hide", "Hidden", "Invisible", "Lost Stealth"));

    private readonly Dictionary<ulong, PoseTrack> tracks = new();
    private readonly HashSet<ulong> seenThisScan = new();
    private readonly CategoryResolver categories = new();
    private readonly PoseDedupe dedupe = new();
    private readonly AggroCorrelator aggro = new();
    private readonly RespawnMatcher respawns = new();
    private readonly PlayerHistory playerHistory = new();
    private readonly Queue<string> recent = new();
    private readonly int[] countsByKind = new int[20];
    private Guid visitId;
    private long visitScanStartMs;
    private long nowMs;
    private Vector3 player;
    private bool playerInCombatLastScan;
    private bool playerInCombat;

    /// <summary>Capture-pass mode (Debug tab): 60 s idle tracks after each spawn pose. Off by default.</summary>
    public bool CapturePass { get; set; }

    internal int TrackedCount => tracks.Count;
    internal int DedupeCount => dedupe.Count;
    internal int PendingDeaths => respawns.PendingCount;
    internal int CountOf(ObservationKind kind) => (int)kind < countsByKind.Length ? countsByKind[(int)kind] : 0;
    internal int RespawnCount { get; private set; }
    internal int DroppedNonFinite { get; private set; }
    internal int ReadFailures { get; private set; }
    internal int LastScanFullReads { get; private set; }

    /// <summary>The last records of this visit as name-free summary lines (newest first), for the Debug tab.</summary>
    internal IReadOnlyList<string> Recent
    {
        get
        {
            lock (recent)
                return recent.Reverse().ToArray();
        }
    }

    internal void Reset()
    {
        foreach (var track in tracks.Values)
            track.Npc = null;
        tracks.Clear();
        dedupe.Clear();
        aggro.Clear();
        respawns.Clear();
        playerHistory.Clear();
        categories.Clear();
        Array.Clear(countsByKind);
        RespawnCount = 0;
        lock (recent)
            recent.Clear();
        visitId = Guid.Empty;
    }

    internal void BeginScan(Observation context)
    {
        if (visitId != context.VisitId)
        {
            Reset();
            visitId = context.VisitId;
            visitScanStartMs = context.ObservedAtMs;
        }

        nowMs = context.ObservedAtMs;
        seenThisScan.Clear();
        categories.Clear();
        var local = objectTable.LocalPlayer;
        playerInCombatLastScan = playerInCombat;
        if (local == null)
            return;
        player = local.Position;
        playerInCombat = (local.StatusFlags & StatusFlags.InCombat) != 0;
        playerHistory.Add(nowMs, player);
        respawns.Tick(nowMs, player);
    }

    /// <summary>Kind 1: the respawn match for a new appearance, if any (consumes the matched death).</summary>
    internal EnemyRespawn? Appeared(uint bnpcId, uint layoutId, Vector3 anchor, byte form)
    {
        var match = respawns.Appeared(bnpcId, layoutId, anchor, form, nowMs);
        if (match != null)
            RespawnCount++;
        return match;
    }

    /// <summary>Kind 3: the claimer category and type, and the last target category; registers the death for respawn.</summary>
    internal unsafe (byte Claim, byte ClaimType, byte LastTarget) Died(IBattleNpc enemy, Guid trackId, uint layoutId,
        Vector3 anchor, byte form)
    {
        tracks.TryGetValue(enemy.GameObjectId, out var track);
        var anchorPoint = track?.Spawn.SettledPosition is { } settled && track.Home == null ? settled : anchor;
        respawns.Died(trackId, enemy.BaseId, layoutId, anchorPoint, form, nowMs, player);
        var native = (NativeCharacter*)enemy.Address;
        return ((byte)Category(native->CombatTaggerId), native->CombatTagType, track?.LastTargetCategory ?? 0);
    }

    /// <summary>The cheap per-scan read of one live enemy (form key, settle, aggro edge, idle track).</summary>
    internal unsafe void Observe(IBattleNpc enemy, Guid trackId, Guid? replacedTrackId, bool mutated, bool adapted,
        uint layoutId, Observation context, Action<Observation> record)
    {
        var id = enemy.GameObjectId;
        seenThisScan.Add(id);
        tracks.TryGetValue(id, out var track);
        var native = (NativeCharacter*)enemy.Address;
        var key = ReadFormKey(native, enemy, mutated, adapted);
        var position = enemy.Position;
        var heading = enemy.Rotation;
        if (track == null || track.TrackId != trackId)
        {
            var old = track;
            track = new PoseTrack(trackId, nowMs, Vector3.Distance(position, player));
            track.PresentAtStart = PlayerHistory.PresentAtStart(nowMs, visitScanStartMs);
            track.SeenSpawning = PlayerHistory.SeenSpawning(track.PresentAtStart, track.FirstSeenDistance,
                playerHistory.StoodWithin(position, PoseRates.SpawningYalms, nowMs));
            if (old != null)
            {
                if (old.Idle != null)
                    FinishIdle(old, context, replacedTrackId == old.TrackId ? (byte)3 : (byte)2, record);
                if (replacedTrackId == old.TrackId)
                {
                    // A bnpcId change: the tracker started a new track for the same object. It is a form change.
                    track.Form.Begin(nowMs, old.Key, key);
                    track.FormBefore = old.Cached;
                    track.FormBeforeAtMs = old.CachedAtMs;
                    track.BeforeTrackId = old.TrackId;
                    track.Cached = old.Cached;
                    track.CachedAtMs = old.CachedAtMs;
                    track.HasCombatState = old.HasCombatState;
                    track.WasInCombat = old.WasInCombat;
                }
            }

            tracks[id] = track;
        }

        track.Npc = enemy;
        track.Key = key;
        track.Mutated = mutated;
        track.Adapted = adapted;
        track.LayoutId = layoutId;
        var home = (Vector3)native->DefaultPosition;
        track.Home = home == Vector3.Zero ? null : home;
        track.Position = position;
        track.Heading = heading;
        track.PlayerDistance = Vector3.Distance(position, player);
        var targetId = enemy.TargetObjectId;
        var hasTarget = (uint)targetId != 0 && (uint)targetId != 0xE0000000;
        var inCombatFlag = (enemy.StatusFlags & StatusFlags.InCombat) != 0;
        track.Calm = !hasTarget && enemy.CurrentHp == enemy.MaxHp && !inCombatFlag;
        track.DrawReady = DrawReady(native);
        track.Settle.Update(nowMs, position, heading, track.Calm);
        if (hasTarget)
            track.LastTargetCategory = (byte)Category(targetId);

        if (track.Form.Update(nowMs, key))
        {
            track.FormBefore = track.Cached;
            track.FormBeforeAtMs = track.CachedAtMs;
            if (track.Idle != null)
                FinishIdle(track, context, 3, record);
        }

        var inCombat = inCombatFlag || hasTarget;
        if (track.HasCombatState && !track.WasInCombat && inCombat)
            AddAggroEdge(track, enemy, native, context, hasTarget ? Category(targetId) : ObjectCategory.None);
        track.WasInCombat = inCombat;
        track.HasCombatState = true;

        if (track.Idle != null)
        {
            if (inCombat)
                FinishIdle(track, context, 1, record);
            else if (track.Idle.Complete(nowMs))
                FinishIdle(track, context, 0, record);
            else
                track.Idle.Step(nowMs, position, heading, native->Timeline.TimelineSequencer.TimelineIds[0],
                    native->Timeline.BaseOverride);
        }
    }

    /// <summary>Ends the scan: lost tracks, then at most 6 full reads (form-after > spawn > refresh), then aggro flush.</summary>
    internal void EndScan(Observation context, Action<Observation> record)
    {
        foreach (var (id, track) in tracks.Where(t => !seenThisScan.Contains(t.Key)).ToList())
        {
            if (track.Idle != null)
                FinishIdle(track, context, 2, record);
            track.Npc = null;
            tracks.Remove(id);
        }

        var candidates = new List<(int, long, (PoseTrack Track, int Why, PoseTake Take))>();
        foreach (var track in tracks.Values)
        {
            if (track.Npc == null)
                continue;
            if (track.Form.AfterDue(nowMs))
            {
                candidates.Add((ReadBudget.FormAfter, track.Form.FirstChangeMs, (track, ReadBudget.FormAfter, PoseTake.None)));
                continue;
            }

            var settled = track.Settle.IsSettled(nowMs, track.Calm, track.DrawReady, track.Form.Pending);
            var take = track.Spawn.Decide(settled, track.PlayerDistance);
            if (take != PoseTake.None)
                candidates.Add((ReadBudget.Spawn, track.Settle.FirstSeenMs, (track, ReadBudget.Spawn, take)));
            else if (track.Cached == null || nowMs - track.CachedAtMs >= PoseRates.RefreshMs)
                candidates.Add((ReadBudget.Refresh, track.Cached == null ? 0 : track.CachedAtMs, (track, ReadBudget.Refresh, PoseTake.None)));
        }

        var picked = ReadBudget.Pick(candidates);
        LastScanFullReads = picked.Count;
        foreach (var (track, why, take) in picked)
        {
            EnemyPose? pose;
            try
            {
                pose = FullRead(track);
            }
            catch (Exception)
            {
                // Skip this object for this scan (pointers are null-checked; this is the second guard).
                ReadFailures++;
                continue;
            }

            if (pose == null)
                continue;
            track.Cached = pose;
            track.CachedAtMs = nowMs;
            if (why == ReadBudget.FormAfter)
                CompleteFormChange(track, pose, context, record);
            else if (why == ReadBudget.Spawn)
                TakeSpawnPose(track, pose, take, context, record);
        }

        foreach (var observation in aggro.Flush(nowMs))
            Emit(observation, record);

        foreach (var track in tracks.Values)
            track.Npc = null;
    }

    private void TakeSpawnPose(PoseTrack track, EnemyPose pose, PoseTake take, Observation context, Action<Observation> record)
    {
        track.Spawn.Taken(take, track.PlayerDistance);
        var key = PoseKey.Of(pose.BnpcId, pose.ModelCharaId, pose.Form, track.Position.X, track.Position.Z, track.Heading,
            pose.CollisionLoaded);
        if (!dedupe.TryAdd(key))
            return;
        track.Spawn.SettledPosition ??= track.Position;
        Emit(Enemy(context, track, ObservationKind.EnemySpawnPose) with { Pose = pose, FateId = pose.FateId }, record);
        if (CapturePass && take == PoseTake.First && track.Idle == null && !track.IdleDone && track.Npc != null)
        {
            track.Idle = new IdleTrackBuilder(nowMs, track.Position, track.Heading, pose.BaseTimeline, pose.BaseOverride);
            track.IdleStartPosition = track.Position;
        }
    }

    private void CompleteFormChange(PoseTrack track, EnemyPose after, Observation context, Action<Observation> record)
    {
        var bits = track.Form.Complete(track.Key);
        var before = track.FormBefore;
        // The skeleton bit from the parsed skeleton when both reads have it (a pointer change alone is not a change).
        if (before?.Skeleton is { } bs && after.Skeleton is { } asl)
            bits = bs.SequenceEqual(asl) ? bits & ~FormKey.SkeletonBit : bits | FormKey.SkeletonBit;
        var beforeTrack = track.BeforeTrackId;
        track.BeforeTrackId = null;
        track.FormBefore = null;
        if (bits == 0)
            return;

        float? heightDelta = null, offsetDelta = null;
        if (before?.DrawPosition is { Length: 3 } bd && after.DrawPosition is { Length: 3 } ad)
        {
            heightDelta = PoseMath.R3(ad[1] - bd[1]);
            offsetDelta = PoseMath.R3(ad[1] - after.Position[1] - (bd[1] - before.Position[1]));
        }

        var change = new EnemyFormChange
        {
            Changed = bits,
            Before = before == null ? null : before with { ReadAgeMs = nowMs - track.FormBeforeAtMs },
            After = after,
            BeforeAgeMs = before == null ? null : track.Form.FirstChangeMs - track.FormBeforeAtMs,
            ChangeMs = track.Form.FirstChangeMs,
            DrawHeightDelta = heightDelta,
            DrawOffsetDelta = offsetDelta,
            AfterSettled = track.Calm && track.Settle.IsStill(nowMs)
        };
        Emit(Enemy(context, track, ObservationKind.EnemyFormChange) with { FormChange = change, BeforeTrackId = beforeTrack }, record);
    }

    private void FinishIdle(PoseTrack track, Observation context, byte reason, Action<Observation> record)
    {
        var idle = track.Idle;
        track.Idle = null;
        track.IdleDone = true;
        if (idle == null)
            return;
        var start = track.IdleStartPosition;
        Emit(context with
        {
            Kind = ObservationKind.EnemyIdleTrack,
            GameId = track.Key.BnpcId,
            EnemyTrackId = track.TrackId,
            LayoutId = track.LayoutId == 0 ? null : track.LayoutId,
            X = start.X,
            Y = start.Y,
            Z = start.Z,
            Idle = idle.Finish(reason)
        }, record);
    }

    private unsafe void AddAggroEdge(PoseTrack track, IBattleNpc enemy, NativeCharacter* native, Observation context,
        ObjectCategory target)
    {
        var local = objectTable.LocalPlayer;
        if (local == null)
            return;
        var localNative = (NativeCharacter*)local.Address;
        float[]? cast = null;
        if (enemy.IsCasting)
            cast = [enemy.CastActionId, enemy.CastActionType,
                PoseMath.R3(enemy.TotalCastTime > 0 ? enemy.CurrentCastTime / enemy.TotalCastTime : 0)];
        var sprint = false;
        var stealth = false;
        foreach (var status in local.StatusList)
        {
            if (status.StatusId == 0)
                continue;
            sprint |= sprintIds.Value.Contains(status.StatusId);
            stealth |= stealthIds.Value.Contains(status.StatusId);
        }

        var speed = playerHistory.Speed();
        var fate = native->FateId;
        var data = new EnemyAggro
        {
            TargetCategory = (byte)target,
            DistanceToPlayer = PoseMath.R2(Vector3.Distance(track.Position, player)),
            EnemyFacingToPlayerDeg = PoseMath.R2(PoseMath.FacingToDeg(track.Position, track.Heading, player)),
            PlayerFacingToEnemyDeg = PoseMath.R2(PoseMath.FacingToDeg(player, local.Rotation, track.Position)),
            Dy = PoseMath.R3(player.Y - track.Position.Y),
            EnemyHomeDistance = track.Home is { } home ? PoseMath.R2(Vector3.Distance(home, track.Position)) : null,
            EnemyMovedSinceSettledY = track.Spawn.SettledPosition is { } settled
                ? PoseMath.R2(Vector3.Distance(settled, track.Position)) : null,
            EnemyCast = cast,
            EnemyStatuses = Statuses(enemy),
            EnemyStatusFlags = (int)enemy.StatusFlags,
            EnemyHp = enemy.CurrentHp,
            EnemyMaxHp = enemy.MaxHp,
            Player = new AggroPlayer
            {
                Moving = speed > 0.1f,
                Speed = PoseMath.R2(speed),
                Sprinting = sprint,
                Mounted = localNative->Mount.MountId != 0,
                InCombatAlready = playerInCombatLastScan,
                Casting = local.IsCasting ? [local.CastActionId, local.CastActionType] : null,
                HpPct = local.MaxHp > 0 ? PoseMath.R2(100f * local.CurrentHp / local.MaxHp) : 0,
                MpPct = local.MaxMp > 0 ? PoseMath.R2(100f * local.CurrentMp / local.MaxMp) : null,
                Stealth = stealth
            }
        };
        var observation = Enemy(context, track, ObservationKind.EnemyAggro) with
        {
            Aggro = data,
            FateId = fate == 0 ? null : fate,
            IsInCombat = true
        };
        aggro.Add(nowMs, track.Key.BnpcId, track.TrackId, track.Position, (byte)target, observation);
    }

    private Observation Enemy(Observation context, PoseTrack track, ObservationKind kind) => context with
    {
        Kind = kind,
        GameId = track.Key.BnpcId,
        EnemyTrackId = track.TrackId,
        LayoutId = track.LayoutId == 0 ? null : track.LayoutId,
        X = track.Position.X,
        Y = track.Position.Y,
        Z = track.Position.Z,
        IsMutated = track.Mutated,
        IsAdapted = track.Adapted,
        ModelCharaId = track.Key.ModelCharaId,
        ModelScale = float.IsFinite(track.Key.Scale) ? track.Key.Scale : null
    };

    private void Emit(Observation observation, Action<Observation> record)
    {
        var finite = observation.Kind switch
        {
            ObservationKind.EnemySpawnPose => PoseMath.IsFinite(observation.Pose),
            ObservationKind.EnemyFormChange => observation.FormChange is { } f && PoseMath.IsFinite(f),
            ObservationKind.EnemyIdleTrack => observation.Idle is { } i && PoseMath.IsFinite(i),
            ObservationKind.EnemyAggro => observation.Aggro is { } a && PoseMath.IsFinite(a),
            _ => true
        };
        if (!finite || observation.X is { } x && !float.IsFinite(x) || observation.Y is { } y && !float.IsFinite(y) ||
            observation.Z is { } z && !float.IsFinite(z))
        {
            DroppedNonFinite++;
            return;
        }

        record(observation);
        Note(observation);
    }

    /// <summary>One name-free log line per record, also kept for the Debug tab.</summary>
    internal void Note(Observation observation)
    {
        if ((int)observation.Kind < countsByKind.Length)
            countsByKind[(int)observation.Kind]++;
        var line = PoseLog.Line(observation);
        log.Information(line);
        lock (recent)
        {
            recent.Enqueue(DateTimeOffset.FromUnixTimeMilliseconds(observation.ObservedAtMs).ToLocalTime().ToString("HH:mm:ss") + " " + line);
            while (recent.Count > RecentMax)
                recent.Dequeue();
        }
    }

    private unsafe EnemyPose? FullRead(PoseTrack track)
    {
        var enemy = track.Npc;
        if (enemy == null || enemy.Address == IntPtr.Zero)
            return null;
        var native = (NativeCharacter*)enemy.Address;
        var go = &native->GameObject;
        var draw = go->DrawObject;
        float[]? drawPosition = null, drawRotation = null, drawScale = null, skeletonPosition = null, skeletonRotation = null;
        float? pitch = null, yaw = null, roll = null, tilt = null;
        float[]? animTime = null;
        int[]? skeleton = null;
        float[][]? attached = null;
        if (draw != null)
        {
            Vector3 origin = draw->Object.Position;
            var rotation = (Quaternion)draw->Object.Rotation;
            drawPosition = PoseMath.Vec(origin);
            drawRotation = PoseMath.Quat(rotation);
            drawScale = PoseMath.Vec(draw->Object.Scale);
            // A Character's draw object is a CharacterBase (no virtual GetObjectType call: read-only rule).
            var sk = ((CharacterBase*)draw)->Skeleton;
            if (sk != null)
            {
                var root = (Quaternion)sk->Transform.Rotation;
                skeletonPosition = PoseMath.Vec(sk->Transform.Position);
                skeletonRotation = PoseMath.Quat(root);
                var (p, y, r) = PoseMath.Degrees(root);
                pitch = PoseMath.R2(p);
                yaw = PoseMath.R2(y);
                roll = PoseMath.R2(r);
                tilt = PoseMath.R2(PoseMath.TiltDegrees(root));
                if (sk->PartialSkeletons != null && sk->PartialSkeletonCount is > 0 and <= 64)
                {
                    skeleton = SkeletonOf(sk->PartialSkeletons[0].SkeletonResourceHandle);
                    animTime = AnimTime(sk);
                }
            }

            attached = Attached(draw, origin);
        }

        var customize = new ReadOnlySpan<byte>(&native->DrawData.CustomizeData, 26);
        var equipment = new int[10];
        var anyEquipment = false;
        for (var slot = 0; slot < 10; slot++)
        {
            var piece = native->DrawData.Equipment((DrawDataContainer.EquipmentSlot)slot);
            equipment[slot] = piece.Id | (piece.Variant << 16);
            anyEquipment |= equipment[slot] != 0;
        }

        var main = native->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId;
        var off = native->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).ModelId;
        var timelines = new List<float[]>();
        for (var slot = 0; slot < 14; slot++)
        {
            var timeline = native->Timeline.TimelineSequencer.TimelineIds[slot];
            if (timeline != 0)
                timelines.Add([slot, timeline, PoseMath.R3(native->Timeline.TimelineSequencer.TimelineSpeeds[slot])]);
        }

        var home = (Vector3)go->DefaultPosition;
        var fate = go->FateId;
        var eventId = go->EventId.Id;
        var position = (Vector3)go->Position;
        var distance = Vector3.Distance(position, player);
        return new EnemyPose
        {
            BnpcId = go->BaseId,
            NameId = native->NameId,
            Form = PoseMath.Form(track.Mutated, track.Adapted),
            LayoutId = go->LayoutId == 0 ? null : go->LayoutId,
            FirstSeenMs = track.Settle.FirstSeenMs,
            PresentAtStart = track.PresentAtStart,
            SeenSpawning = track.SeenSpawning,
            PlayerDistance = PoseMath.R2(distance),
            CollisionLoaded = distance <= PoseRates.NearYalms,
            HitboxRadius = PoseMath.R3(go->HitboxRadius),
            Position = PoseMath.Vec(position),
            Rotation = PoseMath.R4(go->Rotation),
            Scale = PoseMath.R4(go->Scale),
            DrawPosition = drawPosition,
            DrawRotation = drawRotation,
            DrawScale = drawScale,
            SkeletonPosition = skeletonPosition,
            SkeletonRotation = skeletonRotation,
            SkeletonPitch = pitch,
            SkeletonYaw = yaw,
            SkeletonRoll = roll,
            SkeletonTilt = tilt,
            ModelCharaId = native->ModelContainer.ModelCharaId,
            Skeleton = skeleton,
            CustomizeHash = PoseMath.CustomizeHash(customize),
            Equipment = anyEquipment ? equipment : null,
            MainHand = main.Id == 0 && main.Type == 0 && main.Variant == 0 ? null : [main.Id, main.Type, main.Variant],
            OffHand = off.Id == 0 && off.Type == 0 && off.Variant == 0 ? null : [off.Id, off.Type, off.Variant],
            Attached = attached,
            BaseTimeline = native->Timeline.TimelineSequencer.TimelineIds[0],
            BaseOverride = native->Timeline.BaseOverride,
            Mode = (byte)native->Mode,
            ModeParam = native->ModeParam,
            OverallSpeed = PoseMath.R3(native->Timeline.OverallSpeed),
            Timelines = timelines.ToArray(),
            AnimTime = animTime,
            InCombat = !track.Calm,
            Moving = track.Settle.Moving,
            ReadAgeMs = 0,
            HomePosition = home == Vector3.Zero ? null : PoseMath.Vec(home),
            HomeRotation = home == Vector3.Zero ? null : PoseMath.R4(go->DefaultRotation),
            FateId = fate == 0 ? null : fate,
            EventId = eventId == 0 ? null : eventId,
            GimmickId = go->GimmickId == 0 ? null : go->GimmickId,
            Battalion = native->CharacterData.Battalion,
            RelationFlags = native->RelationFlags,
            Flags = native->CharacterData.Flags,
            StatusFlags = (int)enemy.StatusFlags,
            ActorControlFlags = native->ActorControlFlags,
            EventState = go->EventState,
            Targetable = (int)go->TargetableStatus,
            NamePlateIconId = go->NamePlateIconId,
            Icon = native->CharacterData.Icon,
            RenderFlags = (int)go->RenderFlags,
            Visibility = go->Visibility,
            MovementState = (int)native->MoveController.MovementState,
            TransformationId = native->CharacterData.TransformationId,
            Hp = enemy.CurrentHp,
            MaxHp = enemy.MaxHp,
            Claim = (byte)Category(native->CombatTaggerId),
            ClaimType = native->CombatTagType,
            Statuses = Statuses(enemy)
        };
    }

    private static unsafe FormKey ReadFormKey(NativeCharacter* native, IBattleNpc enemy, bool mutated, bool adapted)
    {
        var draw = native->GameObject.DrawObject;
        var drawScale = float.NaN;
        nint skeleton = 0;
        if (draw != null)
        {
            drawScale = draw->Object.Scale.Y;
            var sk = ((CharacterBase*)draw)->Skeleton;
            if (sk != null && sk->PartialSkeletons != null && sk->PartialSkeletonCount is > 0 and <= 64)
                skeleton = (nint)sk->PartialSkeletons[0].SkeletonResourceHandle;
        }

        return new FormKey(native->ModelContainer.ModelCharaId, native->GameObject.Scale, drawScale, native->NameId,
            enemy.BaseId, mutated, adapted, skeleton);
    }

    private static unsafe bool DrawReady(NativeCharacter* native)
    {
        var draw = native->GameObject.DrawObject;
        if (draw == null || !draw->IsVisible)
            return false;
        var sk = ((CharacterBase*)draw)->Skeleton;
        return sk != null && sk->PartialSkeletons != null && sk->PartialSkeletonCount is > 0 and <= 64 &&
               sk->PartialSkeletons[0].SkeletonResourceHandle != null;
    }

    // Imaging BrioStudio.SkeletonOf: partial 0's resource file name (<= 260 bytes), parsed to numbers here.
    private static unsafe int[]? SkeletonOf(SkeletonResourceHandle* handle)
    {
        if (handle == null)
            return null;
        var name = &((ResourceHandle*)handle)->FileName;
        var length = (int)Math.Min(name->Length, 260);
        if (length <= 0)
            return null;
        var bytes = name->Length < 16 ? name->Buffer : name->BufferPtr;
        return bytes == null ? null : PoseMath.ParseSkeleton(new ReadOnlySpan<byte>(bytes, length));
    }

    // Imaging AnimationState.Of: the Havok control 0 local time and the animation's duration (partial 0).
    private static unsafe float[]? AnimTime(FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton* sk)
    {
        var animated = sk->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if (animated == null || animated->AnimationControls.Length == 0 || animated->AnimationControls[0].Value == null)
            return null;
        var control = animated->AnimationControls[0].Value;
        var binding = control->hkaAnimationControl.Binding;
        var duration = binding.ptr != null && binding.ptr->Animation.ptr != null ? binding.ptr->Animation.ptr->Duration : float.NaN;
        var local = control->hkaAnimationControl.LocalTime;
        if (!float.IsFinite(local))
            return null;
        return float.IsFinite(duration) ? [PoseMath.R3(local), PoseMath.R3(duration)] : [PoseMath.R3(local)];
    }

    // Imaging AnimationState.Weapons: the child draw objects (at most 8) as offsets from the draw position.
    private static unsafe float[][]? Attached(DrawObject* draw, Vector3 origin)
    {
        var list = new List<float[]>();
        var first = draw->Object.ChildObject;
        var child = first;
        for (var i = 0; child != null && i < 8; i++)
        {
            Vector3 at = child->Position;
            var offset = at - origin;
            list.Add([PoseMath.R3(offset.X), PoseMath.R3(offset.Y), PoseMath.R3(offset.Z), PoseMath.R3(child->Scale.Y)]);
            child = child->NextSiblingObject;
            if (child == first)
                break;
        }

        return list.Count == 0 ? null : list.ToArray();
    }

    private int[][] Statuses(IBattleNpc enemy)
    {
        var list = new List<int[]>();
        foreach (var status in enemy.StatusList)
        {
            if (status.StatusId == 0)
                continue;
            var timeLeft = float.IsFinite(status.RemainingTime) ? (int)MathF.Round(status.RemainingTime * 10) : 0;
            list.Add([(int)status.StatusId, status.Param, timeLeft, (byte)CategoryOfEntity(status.SourceId)]);
            if (list.Count >= 30)
                break;
        }

        return list.ToArray();
    }

    private ObjectCategory Category(ulong gameObjectId)
    {
        FillCategories();
        return categories.OfObjectId(gameObjectId);
    }

    private ObjectCategory CategoryOfEntity(uint entityId)
    {
        FillCategories();
        return categories.OfEntityId(entityId);
    }

    // Once per scan, on first use: the object table's players, owned objects and NPCs, and the party list.
    private void FillCategories()
    {
        if (categories.Filled)
            return;
        var local = objectTable.LocalPlayer;
        foreach (var obj in objectTable)
        {
            if (obj is IPlayerCharacter pc)
                categories.Add(pc.EntityId, ObjectClass.Player, 0,
                    (pc.StatusFlags & (StatusFlags.PartyMember | StatusFlags.AllianceMember)) != 0);
            else if (obj.OwnerId != 0 && obj.OwnerId != 0xE0000000)
                categories.Add(obj.EntityId, ObjectClass.Owned, obj.OwnerId, false);
            else
                categories.Add(obj.EntityId, ObjectClass.Npc, 0, false);
        }

        foreach (var member in partyList)
            categories.AddPartyMember(member.EntityId);
        categories.SetLocal(local?.EntityId ?? 0xE0000000);
    }

    private static HashSet<uint> FindStatuses(IDataManager dataManager, params string[] names) =>
        dataManager.GetExcelSheet<Status>(ClientLanguage.English)
            .Where(row => names.Contains(row.Name.ExtractText(), StringComparer.OrdinalIgnoreCase))
            .Select(row => row.RowId).ToHashSet();

    private sealed class PoseTrack(Guid trackId, long firstSeenMs, float firstSeenDistance)
    {
        public Guid TrackId { get; } = trackId;
        public float FirstSeenDistance { get; } = firstSeenDistance;
        public SettleState Settle { get; } = new(firstSeenMs);
        public SpawnPoseState Spawn { get; } = new();
        public FormWatch Form { get; } = new();
        public IBattleNpc? Npc { get; set; }
        public FormKey Key { get; set; }
        public bool Mutated { get; set; }
        public bool Adapted { get; set; }
        public uint LayoutId { get; set; }
        public Vector3? Home { get; set; }
        public Vector3 Position { get; set; }
        public float Heading { get; set; }
        public float PlayerDistance { get; set; }
        public bool Calm { get; set; }
        public bool DrawReady { get; set; }
        public bool PresentAtStart { get; set; }
        public bool SeenSpawning { get; set; }
        public EnemyPose? Cached { get; set; }
        public long CachedAtMs { get; set; }
        public EnemyPose? FormBefore { get; set; }
        public long FormBeforeAtMs { get; set; }
        public Guid? BeforeTrackId { get; set; }
        public bool HasCombatState { get; set; }
        public bool WasInCombat { get; set; }
        public byte LastTargetCategory { get; set; }
        public IdleTrackBuilder? Idle { get; set; }
        public bool IdleDone { get; set; }
        public Vector3 IdleStartPosition { get; set; }
    }
}

using System;

namespace XivMate.DataGathering.Forays.Dalamud.Models;

// Pose capture v2 wire objects (docs/CAPTURE_DESIGN.md §2). Numbers, bools, number arrays and the random
// enemyTrackId only: no strings, no names, no content ids, no game object ids. Every object carries poseVersion.
// Categories (target, claim, status source) are Core.ObjectCategory bytes.

/// <summary>One full read of an enemy (kinds 15, 16 before/after). Positions 3 dp, quaternions 4 dp, degrees 2 dp.</summary>
internal sealed record EnemyPose
{
    public int PoseVersion { get; init; } = 1;
    public uint BnpcId { get; init; }
    public uint NameId { get; init; }

    /// <summary>0 normal, 1 mutated, 2 adapted, 3 both.</summary>
    public byte Form { get; init; }

    public uint? LayoutId { get; init; }
    public long FirstSeenMs { get; init; }
    public bool PresentAtStart { get; init; }
    public bool SeenSpawning { get; init; }
    public float PlayerDistance { get; init; }
    /// <summary>Player within PoseRates.NearYalms (90 y since the 11:20 trial): the ground is loaded.</summary>
    public bool CollisionLoaded { get; init; }

    /// <summary>The skeleton tilt is trusted (same 90 y rule; a pose beyond it may read tilt 0 on a slope).</summary>
    public bool TiltTrusted { get; init; }

    /// <summary>The one upgrade re-take of this spawn instance (the first pose was untrusted).</summary>
    public bool Retake { get; init; }

    /// <summary>Where the instance was when first seen [x, y, z] (item 3: is DefaultPosition the spawn point?).</summary>
    public float[]? FirstSeenPosition { get; init; }

    /// <summary>Distance from the pose position to DefaultPosition (the home).</summary>
    public float? HomeDistance { get; init; }

    /// <summary>
    /// Distance from the first-seen position to DefaultPosition. About 0 for every instance, including ones that
    /// entered view walking, would mean DefaultPosition is the position at object creation, not a leash home.
    /// </summary>
    public float? FirstSeenHomeDistance { get; init; }

    /// <summary>Distance from the first-seen position to the pose position (wandered before settling).</summary>
    public float? FirstSeenMoved { get; init; }

    /// <summary>Milliseconds from first seen to this read.</summary>
    public long SeenForMs { get; init; }

    public float HitboxRadius { get; init; }

    /// <summary>GameObject.Position [x, y, z] (networked, interpolated).</summary>
    public float[] Position { get; init; } = [];

    /// <summary>GameObject.Rotation (radians).</summary>
    public float Rotation { get; init; }

    public float Scale { get; init; }

    /// <summary>DrawObject position: y is the settled ground height.</summary>
    public float[]? DrawPosition { get; init; }

    /// <summary>DrawObject rotation quaternion [x, y, z, w].</summary>
    public float[]? DrawRotation { get; init; }

    public float[]? DrawScale { get; init; }

    /// <summary>CharacterBase.Skeleton root transform position.</summary>
    public float[]? SkeletonPosition { get; init; }

    /// <summary>CharacterBase.Skeleton root rotation [x, y, z, w]: the slope tilt.</summary>
    public float[]? SkeletonRotation { get; init; }

    public float? SkeletonPitch { get; init; }
    public float? SkeletonYaw { get; init; }
    public float? SkeletonRoll { get; init; }
    public float? SkeletonTilt { get; init; }
    public int ModelCharaId { get; init; }

    /// <summary>Drawn skeleton [kind ascii (c/d/m/w), id, base] parsed from the resource path; the path itself is not sent.</summary>
    public int[]? Skeleton { get; init; }

    /// <summary>First 48 bits of SHA-1 over the 26 customize bytes (Imaging's 12-hex customizeHash as a number).</summary>
    public long CustomizeHash { get; init; }

    /// <summary>10 slots, id | variant &lt;&lt; 16; null when all zero.</summary>
    public int[]? Equipment { get; init; }

    /// <summary>[id, type, variant]; null when zero.</summary>
    public int[]? MainHand { get; init; }

    public int[]? OffHand { get; init; }

    /// <summary>Attached models (child draw objects, at most 8): [dx, dy, dz, scale] from the draw position.</summary>
    public float[][]? Attached { get; init; }

    public int BaseTimeline { get; init; }
    public int BaseOverride { get; init; }
    public byte Mode { get; init; }
    public byte ModeParam { get; init; }
    public float OverallSpeed { get; init; }

    /// <summary>Non-zero timeline slots: [slot, id, speed].</summary>
    public float[][] Timelines { get; init; } = [];

    /// <summary>Havok control 0 [localTime, duration] (seconds).</summary>
    public float[]? AnimTime { get; init; }

    public bool InCombat { get; init; }
    public bool Moving { get; init; }
    public long ReadAgeMs { get; init; }

    // Raw state (addendum 2): sent as read; meaning for foray mobs unverified.
    public float[]? HomePosition { get; init; }
    public float? HomeRotation { get; init; }
    public ushort? FateId { get; init; }
    public uint? EventId { get; init; }
    public uint? GimmickId { get; init; }
    public byte Battalion { get; init; }
    public byte RelationFlags { get; init; }
    public byte Flags { get; init; }
    public int StatusFlags { get; init; }
    public byte ActorControlFlags { get; init; }
    public byte EventState { get; init; }
    public int Targetable { get; init; }
    public uint NamePlateIconId { get; init; }
    public byte Icon { get; init; }
    public int RenderFlags { get; init; }
    public byte Visibility { get; init; }
    public int MovementState { get; init; }
    public short TransformationId { get; init; }
    public uint Hp { get; init; }
    public uint MaxHp { get; init; }

    /// <summary>Claimer category from CombatTaggerId.</summary>
    public byte Claim { get; init; }

    public byte ClaimType { get; init; }

    /// <summary>[[statusId, param, timeLeftDs, sourceCategory], ...] (enemy statuses, raw ids).</summary>
    public int[][] Statuses { get; init; } = [];
}

internal sealed record EnemyFormChange
{
    public int PoseVersion { get; init; } = 1;

    /// <summary>1 model, 2 scale, 4 drawScale, 8 nameId, 16 bnpcId, 32 mutated, 64 adapted, 128 skeleton.</summary>
    public int Changed { get; init; }

    public EnemyPose? Before { get; init; }
    public EnemyPose After { get; init; } = new();
    public long? BeforeAgeMs { get; init; }

    /// <summary>First detection of the change (Unix ms).</summary>
    public long ChangeMs { get; init; }

    public float? DrawHeightDelta { get; init; }
    public float? DrawOffsetDelta { get; init; }
    public bool AfterSettled { get; init; }
}

internal sealed record EnemyIdleTrack
{
    public int PoseVersion { get; init; } = 1;
    public long StartMs { get; init; }
    public int StepMs { get; init; }

    /// <summary>[[dtMs, x, y, z, heading], ...]: the first, the last and any that moved &gt; 0.05 y or turned &gt; 1 degree.</summary>
    public float[][] Samples { get; init; } = [];

    /// <summary>[[dtMs, slot0Id, baseOverride], ...] on change.</summary>
    public long[][] Timelines { get; init; } = [];

    /// <summary>0 complete (60 s), 1 combat, 2 lost, 3 form change.</summary>
    public byte EndReason { get; init; }
}

internal sealed record EnemyAggro
{
    public int PoseVersion { get; init; } = 1;
    public byte TargetCategory { get; init; }
    public float DistanceToPlayer { get; init; }

    /// <summary>Signed degrees from the enemy's facing to the player (0 = facing the player).</summary>
    public float EnemyFacingToPlayerDeg { get; init; }

    public float PlayerFacingToEnemyDeg { get; init; }

    /// <summary>Player height minus enemy height.</summary>
    public float Dy { get; init; }

    public float? EnemyHomeDistance { get; init; }
    public float? EnemyMovedSinceSettledY { get; init; }

    /// <summary>[actionId, actionType, progress 0..1].</summary>
    public float[]? EnemyCast { get; init; }

    public int[][] EnemyStatuses { get; init; } = [];
    public int EnemyStatusFlags { get; init; }
    public uint EnemyHp { get; init; }
    public uint EnemyMaxHp { get; init; }
    public AggroPlayer Player { get; init; } = new();
    public AggroLink[] Linked { get; init; } = [];
}

/// <summary>The local player's state at an aggro edge: allow-listed flags only, no raw statuses.</summary>
internal sealed record AggroPlayer
{
    public bool Moving { get; init; }

    /// <summary>Yalms per second over the last scan.</summary>
    public float Speed { get; init; }

    public bool Sprinting { get; init; }
    public bool Mounted { get; init; }
    public bool InCombatAlready { get; init; }

    /// <summary>[actionId, actionType] while casting.</summary>
    public uint[]? Casting { get; init; }

    public float HpPct { get; init; }
    public float? MpPct { get; init; }

    /// <summary>Sneak / Stealth / Hide / Invisible / Lost Stealth (allow-listed by English status name).</summary>
    public bool Stealth { get; init; }
}

/// <summary>Another enemy that entered combat within 1.5 s and 40 y of this one: bnpcId + the random track id only.</summary>
internal sealed record AggroLink
{
    public uint BnpcId { get; init; }
    public Guid EnemyTrackId { get; init; }
    public long DtMs { get; init; }
    public float Distance { get; init; }
    public byte TargetCategory { get; init; }
}

/// <summary>
/// Kind 1 (task 2026-11-08 death -> respawn): this appearance matched an earlier death of the same bnpcId in the same
/// visit, by the same non-zero layoutId or a spawn anchor within 1.5 y.
/// </summary>
internal sealed record EnemyRespawn
{
    public int PoseVersion { get; init; } = 1;
    public Guid PreviousTrackId { get; init; }
    public long DiedAtMs { get; init; }
    public long IntervalMs { get; init; }

    /// <summary>1 layoutId, 2 spawn anchor within 1.5 y.</summary>
    public byte MatchedBy { get; init; }

    public float AnchorDistance { get; init; }
    public bool SameForm { get; init; }

    /// <summary>The farthest the player was from the spawn anchor between the death and this appearance (scans).</summary>
    public float MaxPlayerDistance { get; init; }
}

using System;

namespace XivMate.DataGathering.Forays.Dalamud.Models;

internal sealed record ObservationBatch(int SchemaVersion, Guid BatchId, Observation[] Observations);

internal enum ObservationKind
{
    EnemyAppeared = 1,
    EnemySample = 2,
    EnemyDied = 3,
    EnemyDisappeared = 4,
    FateStarted = 5,
    FateUpdated = 6,
    FateEnded = 7,
    FateDisappeared = 8,
    Loot = 9,
    Chain = 10,
    Treasure = 11,
    CriticalEncounter = 12,
    SelfDefeat = 13,

    /// <summary>Coverage heartbeat (every 30 s while recording): own position, zone, map, weather and levels. Grouped by VisitId (and the API key server-side).</summary>
    RecordingActive = 14,

    /// <summary>Pose capture v2: one settled spawn pose per enemy instance (plus at most one near retake). See docs/CAPTURE_DESIGN.md.</summary>
    EnemySpawnPose = 15,

    /// <summary>Pose capture v2: an enemy's form key changed (mutation, adaptation, model, scale, ids): before and after.</summary>
    EnemyFormChange = 16,

    /// <summary>Pose capture v2, capture-pass mode only: a 60 s idle track after the spawn pose.</summary>
    EnemyIdleTrack = 17,

    /// <summary>Pose capture v2: an enemy's out-of-combat to in-combat edge, with the local player's state (categories only).</summary>
    EnemyAggro = 18
}

/// <summary>FATE result on FateEnded: from the end state (Ended/Failed), else from the last progress (100 = success).</summary>
internal enum FateOutcome : byte
{
    Unknown = 0,
    Success = 1,
    Failed = 2
}

internal sealed record Observation
{
    public ObservationKind Kind { get; init; }
    public long ObservedAtMs { get; init; }
    public Guid VisitId { get; init; }
    public uint TerritoryId { get; init; }
    public uint MapId { get; init; }
    public byte? WeatherId { get; init; }
    public uint? PlayerElementalLevel { get; init; }
    public uint? PlayerEffectiveElementalLevel { get; init; }
    public bool? PlayerElementalSynced { get; init; }
    public uint? PlayerKnowledgeLevel { get; init; }
    public uint? PlayerEffectiveKnowledgeLevel { get; init; }
    public uint? GameId { get; init; }
    public Guid? EnemyTrackId { get; init; }
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? Z { get; init; }
    public byte? EnemyElementalLevel { get; init; }
    public byte? EnemyKnowledgeLevel { get; init; }
    public byte? EnemyCombatLevel { get; init; }
    public byte? ElementId { get; init; }
    public bool? IsAdapted { get; init; }
    public bool? IsMutated { get; init; }
    public bool? IsInCombat { get; init; }
    public float? Radius { get; init; }
    public int? StartedAt { get; init; }
    public byte? State { get; init; }
    public byte? Progress { get; init; }
    public uint? Quantity { get; init; }
    public uint? ChainNumber { get; init; }
    public byte? TreasureKind { get; init; }
    public byte? TreasureGrade { get; init; }
    public uint? SecondsLeft { get; init; }

    /// <summary>Enemy ModelChara row ID (Character.ModelContainer.ModelCharaId).</summary>
    public int? ModelCharaId { get; init; }

    /// <summary>Enemy object scale (GameObject.Scale).</summary>
    public float? ModelScale { get; init; }

    /// <summary>FateEnded only: the FATE result (see <see cref="FateOutcome"/>).</summary>
    public FateOutcome? Outcome { get; init; }

    /// <summary>FateEnded only: true when no Ended/Failed state was seen and the outcome comes from the last progress.</summary>
    public bool? OutcomeInferred { get; init; }

    /// <summary>FateEnded only: seconds from the FATE start (StartedAt) to this observation.</summary>
    public uint? DurationSeconds { get; init; }

    // Pose capture v2 (docs/CAPTURE_DESIGN.md). schemaVersion stays 1; the nested objects carry poseVersion.

    /// <summary>Kinds 1-4, 15, 16, 18: GameObject.LayoutId (raw; 0 is sent as null).</summary>
    public uint? LayoutId { get; init; }

    /// <summary>Kinds 1-4, 15, 18: Character.FateId (FATE adds; 0 is sent as null).</summary>
    public ushort? FateId { get; init; }

    /// <summary>Kinds 1-4: Character.DefaultPosition [x, y, z] (raw home point; all-zero is sent as null).</summary>
    public float[]? HomePosition { get; init; }

    /// <summary>Kind 3: the claimer category (<see cref="Core.ObjectCategory"/>) from CombatTaggerId. Never the id.</summary>
    public byte? Claim { get; init; }

    /// <summary>Kind 3: Character.CombatTagType (raw).</summary>
    public byte? ClaimType { get; init; }

    /// <summary>Kind 3: the category of the enemy's last target while alive.</summary>
    public byte? LastTargetCategory { get; init; }

    /// <summary>Kind 16: the previous track when a bnpcId change made the tracker start a new track.</summary>
    public Guid? BeforeTrackId { get; init; }

    /// <summary>Kind 15.</summary>
    public EnemyPose? Pose { get; init; }

    /// <summary>Kind 16.</summary>
    public EnemyFormChange? FormChange { get; init; }

    /// <summary>Kind 17.</summary>
    public EnemyIdleTrack? Idle { get; init; }

    /// <summary>Kind 18.</summary>
    public EnemyAggro? Aggro { get; init; }

    /// <summary>Kind 1: this appearance matched an earlier death of the same enemy at the same spawn point (this visit).</summary>
    public EnemyRespawn? Respawn { get; init; }

    /// <summary>
    /// Kind 1 (task 2026-11-08-1212): the same heuristic as pose.seenSpawning. HomePosition (DefaultPosition) is the
    /// position at which the client created the object (it equals ACT's AddCombatant position), so it is the spawn
    /// point only when the object was created at its spawn: seenSpawning, or a respawn match seen from nearby.
    /// </summary>
    public bool? SeenSpawning { get; init; }

    /// <summary>Kind 1: first seen in the visit's first 5 s of scanning (zone-in or enable).</summary>
    public bool? PresentAtStart { get; init; }

    /// <summary>Kind 1: the player's distance to the enemy when first seen (yalms).</summary>
    public float? FirstSeenDistance { get; init; }
}

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
    RecordingActive = 14
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
}

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
    CriticalEncounter = 12
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
}

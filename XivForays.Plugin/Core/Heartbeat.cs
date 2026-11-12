using System.Numerics;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>
/// Coverage heartbeat ("recording active"). Every <see cref="IntervalMs"/> while recording, one record with the
/// local player's own position plus the shared context (zone, map, weather, EL/KL). A person's visits are
/// grouped by the existing VisitId (one per zone entry) and, server-side, the API key on the batch.
/// It has no name, content ID, job, world or party fields.
/// </summary>
internal static class Heartbeat
{
    public const long IntervalMs = 30_000;

    public static bool IsDue(long nowMs, long lastSentMs) => lastSentMs == 0 || nowMs - lastSentMs >= IntervalMs;

    public static Observation Create(Observation context, Vector3 position, long? gameVersion = null,
        bool? lootListening = null) => new()
    {
        Kind = ObservationKind.RecordingActive,
        ObservedAtMs = context.ObservedAtMs,
        VisitId = context.VisitId,
        TerritoryId = context.TerritoryId,
        MapId = context.MapId,
        WeatherId = context.WeatherId,
        PlayerElementalLevel = context.PlayerElementalLevel,
        PlayerEffectiveElementalLevel = context.PlayerEffectiveElementalLevel,
        PlayerElementalSynced = context.PlayerElementalSynced,
        PlayerKnowledgeLevel = context.PlayerKnowledgeLevel,
        PlayerEffectiveKnowledgeLevel = context.PlayerEffectiveKnowledgeLevel,
        X = position.X,
        Y = position.Y,
        Z = position.Z,
        GameVersion = gameVersion,
        LootListening = lootListening
    };
}

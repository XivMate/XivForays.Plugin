namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>
/// How often each stream reads the game and how often it records. Reads are local and cheap; only records
/// cost memory and upload. All streams stop at the zone gate.
/// </summary>
internal static class StreamRates
{
    /// <summary>Enemy table scan (local only). Detects appear/die/disappear/state changes within half a second.</summary>
    public const int EnemyScanMs = 500;

    /// <summary>Slow periodic EnemySample per live enemy, on top of change-driven records.</summary>
    public const long EnemyPeriodicSampleMs = 30_000;

    /// <summary>An enemy that moved this far (yalms) since its last record gets an EnemySample.</summary>
    public const float EnemyMoveThreshold = 15f;

    /// <summary>Legacy enemyposition snapshot (whole visible list, with names) for the existing route.</summary>
    public const long LegacySnapshotMs = 15_000;

    /// <summary>FATE table scan (local only); records only on start/progress/end.</summary>
    public const int FateScanMs = 1000;

    /// <summary>Treasure, CE and heartbeat tick; treasure and CE record only on change.</summary>
    public const int SignalScanMs = 1000;

    /// <summary>
    /// Baldesion Arsenal portal scan. Portals are event objects that stand still for minutes, so five seconds loses
    /// nothing. The appear time can be up to this late; the gone time is the last time the portal was seen.
    /// </summary>
    public const int PortalScanMs = 5000;

    /// <summary>
    /// Player context read. It records at most every 5 s on a change and every 60 s otherwise, so reading every second
    /// built a full context (lists, arrays, strings) that was thrown away almost every time.
    /// </summary>
    public const int PlayerContextReadMs = 2000;

    public static bool MovedEnough(float dx, float dy, float dz) =>
        dx * dx + dy * dy + dz * dz >= EnemyMoveThreshold * EnemyMoveThreshold;
}

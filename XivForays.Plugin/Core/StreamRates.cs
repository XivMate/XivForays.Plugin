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
    public const int FateScanMs = 500;

    /// <summary>Treasure, CE and heartbeat tick; treasure and CE record only on change.</summary>
    public const int SignalScanMs = 1000;

    public static bool MovedEnough(float dx, float dy, float dz) =>
        dx * dx + dy * dy + dz * dz >= EnemyMoveThreshold * EnemyMoveThreshold;
}

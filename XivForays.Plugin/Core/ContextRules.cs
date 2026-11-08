using System;
using System.Linq;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>
/// Kind 19 PlayerContext rules (task 2026-11-08-1300), dependency-free for tests: what counts as a change, when a
/// record is due, and the player-status allow-list.
/// </summary>
internal static class ContextRules
{
    /// <summary>
    /// Capture logic revision, sent on every pose and context record. 1 = pose capture v2 + context; 2 = release 0.0.0.13
    /// (70 y tilt trust, closer re-takes, language-independent notices, legacy snapshots off). Bump on any capture change.
    /// </summary>
    public const int CaptureRevision = 2;

    /// <summary>A change is recorded at most this often.</summary>
    public const long MinChangeMs = 5000;

    /// <summary>A periodic record at least this often while recording.</summary>
    public const long PeriodicMs = 60_000;

    public const int LevelBit = 1, JobBit = 2, EurekaBit = 4, BoardBit = 8, ChainBit = 16, GroupBit = 32, StatusBit = 64,
        DeadBit = 128, InstanceBit = 256, OccultBit = 512, BozjaBit = 1024, CombatBit = 2048;

    /// <summary>Changed parts. Continuous values (timers, HP, EXP inside a level) are not changes on their own.</summary>
    public static int Diff(PlayerContext? a, PlayerContext b, Observation? aTop = null, Observation? bTop = null)
    {
        if (a == null)
            return 0;
        var bits = 0;
        if (a.Level != b.Level || a.SyncedLevel != b.SyncedLevel || a.LevelSynced != b.LevelSynced ||
            aTop?.PlayerElementalLevel != bTop?.PlayerElementalLevel ||
            aTop?.PlayerEffectiveElementalLevel != bTop?.PlayerEffectiveElementalLevel ||
            aTop?.PlayerElementalSynced != bTop?.PlayerElementalSynced ||
            aTop?.PlayerKnowledgeLevel != bTop?.PlayerKnowledgeLevel ||
            aTop?.PlayerEffectiveKnowledgeLevel != bTop?.PlayerEffectiveKnowledgeLevel)
            bits |= LevelBit;
        if (a.ClassJobId != b.ClassJobId)
            bits |= JobBit;
        if (a.Eureka?.MaxElementalLevel != b.Eureka?.MaxElementalLevel)
            bits |= EurekaBit;
        if (!Same(a.Eureka?.Board, b.Eureka?.Board) || a.Eureka?.Magicite != b.Eureka?.Magicite ||
            a.Eureka?.MagiaAether != b.Eureka?.MagiaAether)
            bits |= BoardBit;
        if (ChainChanged(a.Chain, b.Chain) || a.LastChainNumber != b.LastChainNumber)
            bits |= ChainBit;
        if (a.GroupSize != b.GroupSize || a.Alliance != b.Alliance || !SameRows(a.Group, b.Group))
            bits |= GroupBit;
        if (!a.Statuses.Select(s => s[0]).Order().SequenceEqual(b.Statuses.Select(s => s[0]).Order()))
            bits |= StatusBit;
        if (a.Dead != b.Dead)
            bits |= DeadBit;
        if (a.InstanceStartTs != b.InstanceStartTs || a.PublicInstanceId != b.PublicInstanceId || a.DirectorRowId != b.DirectorRowId ||
            a.CurrentWorldId != b.CurrentWorldId)
            bits |= InstanceBit;
        if (a.Occult?.SupportJob != b.Occult?.SupportJob || !Same(a.Occult?.SupportJobLevels, b.Occult?.SupportJobLevels) ||
            a.Occult?.KnowledgeLevelSync != b.Occult?.KnowledgeLevelSync)
            bits |= OccultBit;
        if (a.InCombat != b.InCombat)
            bits |= CombatBit;
        return bits;
    }

    /// <summary>A chain starts (or is extended: the timer jumps up) or ends (the timer runs out).</summary>
    public static bool ChainChanged(float[]? a, float[]? b)
    {
        var ar = a is { Length: > 0 } ? a[0] : 0;
        var br = b is { Length: > 0 } ? b[0] : 0;
        return (ar > 0) != (br > 0) || br > ar + 0.5f;
    }

    /// <summary>1 start, 2 change, 3 periodic; 0 = not due.</summary>
    public static byte Due(long nowMs, long lastMs, bool hasLast, int changed)
    {
        if (!hasLast)
            return 1;
        if (changed != 0 && nowMs - lastMs >= MinChangeMs)
            return 2;
        return nowMs - lastMs >= PeriodicMs ? (byte)3 : (byte)0;
    }

    /// <summary>
    /// Player statuses that may be sent: movement and stealth, death/raise state, food, and the foray action buffs
    /// (Eureka logos actions end in " L", Bozja lost actions start with "Lost ", Occult phantom actions with "Phantom ").
    /// Everything else on the player is dropped.
    /// </summary>
    public static bool IsAllowedStatusName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return AllowedNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
               name.EndsWith(" L", StringComparison.Ordinal) ||
               name.StartsWith("Lost ", StringComparison.Ordinal) ||
               name.StartsWith("Phantom ", StringComparison.Ordinal);
    }

    private static readonly string[] AllowedNames =
    [
        "Sprint", "Sneak", "Stealth", "Hide", "Hidden", "Invisible", "Weakness", "Brink of Death", "Transcendent",
        "Raise", "Reraise", "Reraiser", "Well Fed", "Elemental Attunement", "Magia Aether"
    ];

    internal static bool SameInts(int[]? a, int[]? b) => Same(a, b);

    private static bool Same(int[]? a, int[]? b) => a == null ? b == null : b != null && a.SequenceEqual(b);

    private static bool SameRows(int[][] a, int[][] b) =>
        a.Length == b.Length && a.Zip(b).All(p => p.First.SequenceEqual(p.Second));
}

/// <summary>Name-free one-line summary of a kind 19 record (numbers only).</summary>
internal static class ContextLog
{
    public static string Line(Observation o)
    {
        var c = o.Situation;
        if (c == null)
            return "context 19";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Format(inv,
            "context 19 reason {0} changed 0x{1:X3} job {2} lvl {3} el {4}/{5} kl {6}/{7} board {8} chain {9} #{10} group {11} statuses {12} dead {13} instance {14}",
            c.Reason, c.Changed, c.ClassJobId, c.Level, o.PlayerEffectiveElementalLevel ?? 0, o.PlayerElementalLevel ?? 0,
            o.PlayerEffectiveKnowledgeLevel ?? 0, o.PlayerKnowledgeLevel ?? 0,
            c.Eureka == null ? "-" : string.Join(",", c.Eureka.Board), c.Chain is { Length: > 0 } ch ? ch[0].ToString("0.0", inv) : "-",
            c.LastChainNumber ?? 0, c.GroupSize, c.Statuses.Length, c.Dead ? 1 : 0, c.InstanceStartTs ?? 0);
    }
}

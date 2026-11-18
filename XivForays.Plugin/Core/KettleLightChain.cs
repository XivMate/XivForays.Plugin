using System;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>Safe reads of this process's own memory. A failed read returns false; it never faults. Reads only, never writes.</summary>
internal interface IProcessMemory
{
    bool TryReadPointer(ulong address, out ulong value);
    bool TryReadInt32(ulong address, out int value);
}

/// <summary>The kettle as the game shows it: the "yield" counter (whole kettles filled) and the light bar.</summary>
internal readonly record struct KettleReading(int Yield, int Bar);

internal enum KettleReadStatus : byte
{
    /// <summary>A plausible reading was made.</summary>
    Ok = 0,

    /// <summary>The chain did not resolve (no Eureka director, null or unreadable hop). Transient; try again next sample.</summary>
    Unreadable = 1,

    /// <summary>The chain resolved but the numbers are impossible. The source switches itself off for the session.</summary>
    OutOfRange = 2
}

/// <summary>
/// Where the kettle numbers live (read-only structs scan, 2026-11-18): the active content director, when it is the Eureka
/// director, holds a pointer at +0x440 to a per-instance block B. The yield is an int32 at B+0x78 and the light bar an
/// int32 at B+0x1D8. B is freed and rebuilt per instance, so the pointer is read again at every sample and B is never kept.
/// Every hop is null-checked and range-checked and every read can fail without side effects.
/// </summary>
internal static class KettleLightChain
{
    public const ulong BlockPointerOffset = 0x440;
    public const ulong YieldOffset = 0x78;
    public const ulong BarOffset = 0x1D8;

    /// <summary>Sanity limits: a bar outside 0 to 120 or a yield outside 0 to 999 means the layout is not what was checked.</summary>
    public const int MaxBar = 120;
    public const int MaxYield = 999;

    /// <summary>
    /// How many bar points make one yield (the rollover size). Not known yet, so deltas across a yield change are not
    /// computed; the raw values are recorded so they can be worked out later.
    /// </summary>
    public static readonly int? BarCap = null;

    /// <summary>User-mode pointer sanity: not null, not in the first 64 KiB, below the 47 bit limit.</summary>
    public static bool PlausiblePointer(ulong p) => p >= 0x10000 && p < 0x0000_8000_0000_0000UL;

    public static bool Plausible(KettleReading r) => r.Bar is >= 0 and <= MaxBar && r.Yield is >= 0 and <= MaxYield;

    /// <summary>Reads yield and bar from the Eureka director at <paramref name="director"/> (0 when there is none).</summary>
    public static KettleReadStatus Try(IProcessMemory memory, ulong director, out KettleReading reading)
    {
        reading = default;
        if (!PlausiblePointer(director))
            return KettleReadStatus.Unreadable;
        if (!memory.TryReadPointer(director + BlockPointerOffset, out var block) || !PlausiblePointer(block))
            return KettleReadStatus.Unreadable;
        if (!memory.TryReadInt32(block + YieldOffset, out var yield) || !memory.TryReadInt32(block + BarOffset, out var bar))
            return KettleReadStatus.Unreadable;
        reading = new KettleReading(yield, bar);
        return Plausible(reading) ? KettleReadStatus.Ok : KettleReadStatus.OutOfRange;
    }

    /// <summary>
    /// Light gained between two readings: (yieldAfter - yieldBefore) * cap + barAfter - barBefore. With the yield unchanged
    /// no cap is needed. With a yield change and an unknown cap the answer is null (the raw values still say it).
    /// </summary>
    public static int? Delta(KettleReading? before, KettleReading? after, int? cap)
    {
        if (before is not { } b || after is not { } a)
            return null;
        if (a.Yield == b.Yield)
            return a.Bar - b.Bar;
        return cap is { } c ? (a.Yield - b.Yield) * c + a.Bar - b.Bar : null;
    }
}

/// <summary>
/// Decides whether the offsets may be used on this game build. They were checked on one build only; on any other the source
/// stays off and the kind 20 records are notice-only.
/// </summary>
internal static class KettleLightGuard
{
    /// <summary>Game versions the offsets are allowed on. Add a version only after step 11 of docs/KETTLE_LIGHT.md passes on it.</summary>
    public static readonly string[] AllowedVersions = ["2026.11.03.0000.0000"];

    /// <summary>Null when the offsets may be read, else the reason they are off.</summary>
    public static string? Reason(string? gameVersion) =>
        gameVersion is null || Array.IndexOf(AllowedVersions, gameVersion) < 0
            ? $"game version {gameVersion ?? "unknown"} is not one the kettle offsets were checked on"
            : null;
}

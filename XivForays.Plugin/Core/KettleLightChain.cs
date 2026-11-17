using System;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>Safe reads of this process's own memory. A failed read returns false; it never faults. Reads only, never writes.</summary>
internal interface IProcessMemory
{
    bool TryReadPointer(ulong address, out ulong value);
    bool TryReadInt32(ulong address, out int value);
}

/// <summary>
/// The pointer chain a Cheat Engine table shared in the Eurekan Explorers Discord uses for the kettle light bar:
/// static pointer at module + <see cref="BaseRva"/>, then offsets 0x38, 0x10, 0xC8, 0x20 (each one a pointer read),
/// then a 4 byte integer at +0x134. Cheat Engine lists the offsets last applied first (134, 20, C8, 10, 38).
/// Every hop is null-checked and range-checked, and every read can fail without side effects.
/// </summary>
internal static class KettleLightChain
{
    public const ulong BaseRva = 0x19053B8;
    public static readonly uint[] PointerOffsets = [0x38, 0x10, 0xC8, 0x20];
    public const uint ValueOffset = 0x134;

    /// <summary>Largest value accepted as plausible. The unit is not confirmed (the table's last value was 61).</summary>
    public const int MaxPlausible = 100_000;

    /// <summary>User-mode pointer sanity: not null, not in the first 64 KiB, below the 47 bit limit.</summary>
    public static bool PlausiblePointer(ulong p) => p >= 0x10000 && p < 0x0000_8000_0000_0000UL;

    /// <summary>The kettle value, or null when any hop fails or the value is out of range.</summary>
    public static uint? Read(IProcessMemory memory, ulong moduleBase)
    {
        if (!PlausiblePointer(moduleBase))
            return null;
        if (!memory.TryReadPointer(moduleBase + BaseRva, out var p) || !PlausiblePointer(p))
            return null;
        foreach (var offset in PointerOffsets)
        {
            if (!memory.TryReadPointer(p + offset, out p) || !PlausiblePointer(p))
                return null;
        }

        if (!memory.TryReadInt32(p + ValueOffset, out var value) || value is < 0 or > MaxPlausible)
            return null;
        return (uint)value;
    }
}

/// <summary>
/// Decides whether the raw RVA may be used on this game build. It may only when the game version is one the chain was
/// checked on and the RVA lies in a writable, non-executable section of the loaded exe (a static pointer cannot live in
/// code). Otherwise the source switches itself off. The reason is returned so it can be logged once.
/// </summary>
internal static class KettleLightGuard
{
    /// <summary>Game versions the chain is allowed on. Add a version only after step 11 of docs/KETTLE_LIGHT.md passes on it.</summary>
    public static readonly string[] AllowedVersions = ["2026.11.03.0000.0000"];

    public const uint SectionExecute = 0x20000000;
    public const uint SectionWrite = 0x80000000;

    /// <summary>Null when the chain may be read, else the reason it is off.</summary>
    public static string? Reason(string? gameVersion, uint? rvaSectionCharacteristics)
    {
        if (gameVersion is null || Array.IndexOf(AllowedVersions, gameVersion) < 0)
            return $"game version {gameVersion ?? "unknown"} is not one the kettle value address was checked on";
        if (rvaSectionCharacteristics is null)
            return "the kettle value address is outside every section of the game exe";
        if ((rvaSectionCharacteristics & SectionExecute) != 0 || (rvaSectionCharacteristics & SectionWrite) == 0)
            return "the kettle value address is in code or read-only data on this build (a static pointer lives in writable data), so it was not read";
        return null;
    }

    /// <summary>The PE section characteristics covering an RVA, read from the loaded exe's headers; null if none covers it.</summary>
    public static uint? SectionCharacteristics(IProcessMemory memory, ulong moduleBase, ulong rva)
    {
        if (!memory.TryReadInt32(moduleBase + 0x3C, out var peOffset) || peOffset is < 0x40 or > 0x1000)
            return null;
        var pe = moduleBase + (ulong)peOffset;
        if (!memory.TryReadInt32(pe, out var signature) || signature != 0x4550)
            return null;
        if (!memory.TryReadInt32(pe + 4, out var machineAndCount) || !memory.TryReadInt32(pe + 20, out var optAndFlags))
            return null;
        var sections = (machineAndCount >> 16) & 0xFFFF;
        var optionalSize = optAndFlags & 0xFFFF;
        var first = pe + 24 + (ulong)optionalSize;
        for (var i = 0; i < sections && i < 96; i++)
        {
            var o = first + (ulong)(i * 40);
            if (!memory.TryReadInt32(o + 8, out var virtualSize) || !memory.TryReadInt32(o + 12, out var virtualAddress)
                || !memory.TryReadInt32(o + 36, out var characteristics))
                return null;
            if (rva >= (ulong)(uint)virtualAddress && rva < (ulong)(uint)virtualAddress + (uint)virtualSize)
                return (uint)characteristics;
        }

        return null;
    }
}

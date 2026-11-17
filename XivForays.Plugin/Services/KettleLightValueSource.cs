using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using XivMate.DataGathering.Forays.Dalamud.Core;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

/// <summary>
/// Reads the kettle light value (read-only; see docs/KETTLE_LIGHT.md). The location comes from a Cheat Engine table shared
/// in the Eurekan Explorers Discord, not from FFXIVClientStructs, so the raw chain is guarded: it is read only on a game
/// version it was checked on and only when its base lies in writable data; otherwise it logs once and returns null, and the
/// kind 20 records come from the kettle notices alone. Every read uses ReadProcessMemory on this process, which fails
/// cleanly instead of faulting. Nothing is ever written.
/// </summary>
internal sealed class KettleLightValueSource(IPluginLog log, Func<string?> gameVersion, IProcessMemory? memory = null, ulong? moduleBase = null)
{
    private bool decided;
    private bool usable;
    private bool loggedUnreadable;
    private ulong baseAddress;
    private IProcessMemory? reader;

    /// <summary>The kettle value now, or null when it cannot be read. Called at most once per signal tick, in Pagos and Pyros only.</summary>
    public uint? Read()
    {
        if (!decided)
            Decide();
        if (!usable || reader is null)
            return null;
        var value = KettleLightChain.Read(reader, baseAddress);
        if (value is null && !loggedUnreadable)
        {
            loggedUnreadable = true;
            log.Information("Kettle light value: the pointer chain did not resolve (logged once). Notices are still recorded.");
        }
        else if (value is not null)
            loggedUnreadable = false;
        return value;
    }

    private void Decide()
    {
        decided = true;
        try
        {
            reader = memory ?? new SelfProcessMemory();
            baseAddress = moduleBase ?? (ulong)Process.GetCurrentProcess().MainModule!.BaseAddress;
            var reason = KettleLightGuard.Reason(gameVersion(), KettleLightGuard.SectionCharacteristics(reader, baseAddress, KettleLightChain.BaseRva));
            usable = reason is null;
            if (reason is not null)
                log.Information($"Kettle light value is off: {reason}. Notices are still recorded.");
        }
        catch (Exception ex)
        {
            usable = false;
            log.Information($"Kettle light value is off: {ex.GetType().Name}. Notices are still recorded.");
        }
    }

    /// <summary>ReadProcessMemory on this process: returns false on any unreadable address instead of faulting.</summary>
    private sealed class SelfProcessMemory : IProcessMemory
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, UIntPtr address, byte[] buffer, UIntPtr size, out UIntPtr read);

        private static readonly IntPtr Self = new(-1);

        private static bool Read(ulong address, byte[] buffer) =>
            ReadProcessMemory(Self, (UIntPtr)address, buffer, (UIntPtr)buffer.Length, out var n) && n == (UIntPtr)buffer.Length;

        public bool TryReadPointer(ulong address, out ulong value)
        {
            var b = new byte[8];
            var ok = Read(address, b);
            value = ok ? BitConverter.ToUInt64(b) : 0;
            return ok;
        }

        public bool TryReadInt32(ulong address, out int value)
        {
            var b = new byte[4];
            var ok = Read(address, b);
            value = ok ? BitConverter.ToInt32(b) : 0;
            return ok;
        }
    }
}

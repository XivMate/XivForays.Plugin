using System;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using XivMate.DataGathering.Forays.Dalamud.Core;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

/// <summary>
/// Reads the kettle yield and bar (read-only; see docs/KETTLE_LIGHT.md). The numbers sit behind the active Eureka content
/// director (FFXIVClientStructs gives the director; the offsets inside it are not named there), so the source is on only for
/// the game build they were checked on, and it switches itself off for the session, with one log line, if a reading is ever
/// impossible. Every read uses ReadProcessMemory on this process, which fails cleanly instead of faulting. Nothing is written.
/// </summary>
internal sealed class KettleLightValueSource(IPluginLog log, Func<string?> gameVersion, Func<ulong> directorAddress, IProcessMemory? memory = null)
{
    private bool decided;
    private bool usable;
    private bool loggedUnreadable;
    private IProcessMemory? reader;

    /// <summary>True once the source has been switched on and has not switched itself off.</summary>
    public bool IsOn => decided && usable;

    /// <summary>The kettle now, or null when it cannot be read. Called at most once per signal tick, in Pagos and Pyros only.</summary>
    public KettleReading? Read()
    {
        if (!decided)
            Decide();
        if (!usable || reader is null)
            return null;
        var status = KettleLightChain.Try(reader, directorAddress(), out var reading);
        switch (status)
        {
            case KettleReadStatus.OutOfRange:
                usable = false;
                log.Information("Kettle light value is off for this session: a reading was outside 0 to 120 (bar) or 0 to 999 (yield). Notices are still recorded.");
                return null;
            case KettleReadStatus.Unreadable:
                // Not in the Eureka director yet (loading, other zone) is normal; say so once per run of failures.
                if (!loggedUnreadable)
                {
                    loggedUnreadable = true;
                    log.Debug("Kettle light value: the Eureka director chain did not resolve (logged once per run).");
                }

                return null;
            default:
                loggedUnreadable = false;
                return reading;
        }
    }

    private void Decide()
    {
        decided = true;
        try
        {
            reader = memory ?? new SelfProcessMemory();
            var version = gameVersion();
            var reason = KettleLightGuard.Reason(version);
            usable = reason is null;
            if (reason is not null)
                log.Information($"Kettle light value is off: {reason}. Notices are still recorded.");
            else
                log.Information($"Kettle light value is on (game version {version}).");
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

#if DEV_SCANNER
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

namespace XivMate.DataGathering.Forays.Dalamud.Dev;

// Dev-only, READ-ONLY value scanner. It only ever reads the game's own memory: VirtualQuery to list
// committed read-write private / image-data regions, ReadProcessMemory to copy bytes out. There is no write method
// anywhere in this folder, no hook and no pointer is ever dereferenced directly.

/// <summary>One readable region of the process.</summary>
internal readonly record struct MemRegion(ulong Base, ulong Size, bool IsImage);

/// <summary>Read-only view of process memory. No write method exists on purpose.</summary>
internal interface IScanMemory
{
    /// <summary>Committed, read-write, private or image-data regions, ascending by address.</summary>
    IEnumerable<MemRegion> Regions();

    /// <summary>Copies up to buffer.Length bytes; returns how many were read (0 when unreadable). Never throws.</summary>
    int Read(ulong address, Span<byte> buffer);
}

/// <summary>The real thing: the own process, through VirtualQuery and ReadProcessMemory (both read-only).</summary>
internal sealed class ProcessScanMemory : IScanMemory
{
    private const uint MemCommit = 0x1000, PageReadWrite = 0x04, MemPrivate = 0x20000, MemImage = 0x1000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nuint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nuint address, out MemoryBasicInformation info, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadProcessMemory(nint process, nuint address, void* buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    public IEnumerable<MemRegion> Regions()
    {
        ulong address = 0x10000;
        while (address < 0x7FFF_FFFF_0000)
        {
            if (VirtualQuery((nuint)address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
                yield break;
            var size = (ulong)info.RegionSize;
            if (info.State == MemCommit && info.Protect == PageReadWrite && info.Type is MemPrivate or MemImage)
                yield return new MemRegion((ulong)info.BaseAddress, size, info.Type == MemImage);
            var next = (ulong)info.BaseAddress + size;
            if (next <= address)
                yield break;
            address = next;
        }
    }

    public unsafe int Read(ulong address, Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;
        fixed (byte* p = buffer)
        {
            return ReadProcessMemory(GetCurrentProcess(), (nuint)address, p, (nuint)buffer.Length, out var read) || read > 0
                ? (int)read : 0;
        }
    }
}

/// <summary>Keeps the scan polite: after each slice of bytes read it sleeps, so the game never stutters.</summary>
internal sealed class Throttle(long sliceBytes = 512 * 1024, int sleepMs = 3, Action<int>? sleep = null)
{
    private long sinceSleep;
    private readonly Action<int> doSleep = sleep ?? (ms => Thread.Sleep(ms));

    public void Spent(long bytes)
    {
        sinceSleep += bytes;
        if (sinceSleep < sliceBytes)
            return;
        sinceSleep = 0;
        if (sleepMs > 0)
            doSleep(sleepMs);
    }
}

internal enum ScanType { Byte, Int16, Int32, Float, Unknown }

internal enum ScanOp { Equal, Increased, Decreased, Unchanged, Changed }

internal static class ScanTypes
{
    public static int SizeOf(ScanType type) => type switch { ScanType.Byte => 1, ScanType.Int16 => 2, _ => 4 };

    public static bool TryParse(string? text, out ScanType type)
    {
        type = (text ?? "").ToLowerInvariant() switch
        {
            "byte" or "u8" => ScanType.Byte,
            "int16" or "short" => ScanType.Int16,
            "int32" or "int" => ScanType.Int32,
            "float" or "single" => ScanType.Float,
            "unknown" => ScanType.Unknown,
            _ => (ScanType)(-1),
        };
        return (int)type >= 0;
    }

    public static bool TryParse(string? text, out ScanOp op)
    {
        op = (text ?? "").ToLowerInvariant() switch
        {
            "equal" or "eq" => ScanOp.Equal,
            "increased" or "up" => ScanOp.Increased,
            "decreased" or "down" => ScanOp.Decreased,
            "unchanged" or "same" => ScanOp.Unchanged,
            "changed" => ScanOp.Changed,
            _ => (ScanOp)(-1),
        };
        return (int)op >= 0;
    }

    /// <summary>Reads one value at the start of the span as a double (bytes unsigned, int16 and int32 signed).</summary>
    public static double Decode(ScanType type, ReadOnlySpan<byte> bytes) => type switch
    {
        ScanType.Byte => bytes[0],
        ScanType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
        ScanType.Float => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        _ => BinaryPrimitives.ReadInt32LittleEndian(bytes),
    };

    public static bool Matches(ScanType type, ScanOp op, double now, double before, double target)
    {
        if (type == ScanType.Float && !double.IsFinite(now))
            return false;
        var tolerance = type == ScanType.Float ? 0.0005 : 0;
        return op switch
        {
            ScanOp.Equal => Math.Abs(now - target) <= tolerance,
            ScanOp.Increased => now > before + tolerance,
            ScanOp.Decreased => now < before - tolerance,
            ScanOp.Unchanged => Math.Abs(now - before) <= tolerance,
            _ => Math.Abs(now - before) > tolerance,
        };
    }
}

/// <summary>Hides addresses in this tool's own memory (XOR with a per-run mask) so a pointer scan never finds the scanner itself.</summary>
internal static class Obf
{
    private static readonly ulong Mask = (ulong)Random.Shared.NextInt64() | 0x8000_0000_0000_0000UL;
    public static ulong Hide(ulong value) => value ^ Mask;
    public static ulong Show(ulong value) => value ^ Mask;
}

/// <summary>Outcome of one scan step, for the log and the result file.</summary>
internal sealed record ScanSummary(string Message, int Count, bool Cancelled = false, bool Truncated = false);

/// <summary>
/// First scan / next scan, Cheat Engine style. Equal-value first scans keep (address, value) pairs; an "unknown" first
/// scan keeps a byte snapshot (capped) that the next scan filters.
/// </summary>
internal sealed class ValueScanner(IScanMemory memory, Throttle throttle, int maxCandidates = 10_000_000, long snapshotCapBytes = 1_500_000_000)
{
    private const int Chunk = 256 * 1024;
    private readonly List<ulong> hiddenAddresses = [];
    private readonly List<double> values = [];
    private List<(ulong Base, byte[] Data)>? snapshot;

    public ScanType Type { get; private set; } = ScanType.Int32;
    public int Count => hiddenAddresses.Count;
    public bool HasSnapshot => snapshot is not null;
    public bool HasScan => snapshot is not null || hiddenAddresses.Count > 0 || Scans > 0;
    public int Scans { get; private set; }

    public ulong AddressAt(int index) => Obf.Show(hiddenAddresses[index]);
    public double ValueAt(int index) => values[index];

    public void Reset()
    {
        hiddenAddresses.Clear();
        values.Clear();
        snapshot = null;
        Scans = 0;
    }

    /// <summary>First scan. type Unknown takes a snapshot (value ignored); otherwise value is required.</summary>
    public ScanSummary First(ScanType type, double? value, CancellationToken cancel)
    {
        Reset();
        Type = type == ScanType.Unknown ? ScanType.Int32 : type;
        return type == ScanType.Unknown ? TakeSnapshot(cancel) : ScanEqual(value ?? throw new InvalidOperationException("a value is required"), cancel);
    }

    /// <summary>Called after Type is set. Walks every region in chunks; entries that match are kept.</summary>
    private ScanSummary ScanEqual(double target, CancellationToken cancel)
    {
        var size = ScanTypes.SizeOf(Type);
        var buffer = new byte[Chunk + size];
        var truncated = false;
        foreach (var region in memory.Regions())
        {
            for (ulong offset = 0; offset < region.Size; offset += Chunk)
            {
                if (cancel.IsCancellationRequested)
                    return new ScanSummary($"cancelled with {Count} candidates", Count, true, truncated);
                var want = (int)Math.Min((ulong)Chunk, region.Size - offset);
                var got = memory.Read(region.Base + offset, buffer.AsSpan(0, Math.Min(want + size - 1, buffer.Length)));
                throttle.Spent(got);
                for (var i = 0; i + size <= got && i < want; i += size)
                {
                    var now = ScanTypes.Decode(Type, buffer.AsSpan(i));
                    if (!ScanTypes.Matches(Type, ScanOp.Equal, now, 0, target))
                        continue;
                    if (Count >= maxCandidates)
                    {
                        truncated = true;
                        break;
                    }
                    hiddenAddresses.Add(Obf.Hide(region.Base + offset + (ulong)i));
                    values.Add(now);
                }
            }
        }
        Scans = 1;
        return new ScanSummary($"first scan {Type} == {target.ToString(CultureInfo.InvariantCulture)}: {Count} candidates" +
            (truncated ? $" (stopped at the {maxCandidates} cap; pick a rarer value)" : ""), Count, false, truncated);
    }

    private ScanSummary TakeSnapshot(CancellationToken cancel)
    {
        snapshot = [];
        long total = 0;
        foreach (var region in memory.Regions())
        {
            for (ulong offset = 0; offset < region.Size; offset += Chunk)
            {
                if (cancel.IsCancellationRequested)
                {
                    snapshot = null;
                    return new ScanSummary("cancelled, no snapshot kept", 0, true);
                }
                var want = (int)Math.Min((ulong)Chunk, region.Size - offset);
                if (total + want > snapshotCapBytes)
                {
                    Scans = 1;
                    return new ScanSummary($"snapshot stopped at the {snapshotCapBytes / 1_000_000} MB cap ({total / 1_000_000} MB taken, later regions missing)", 0, false, true);
                }
                var data = new byte[want];
                var got = memory.Read(region.Base + offset, data);
                throttle.Spent(got);
                if (got <= 0)
                    continue;
                if (got < want)
                    Array.Resize(ref data, got);
                snapshot.Add((region.Base + offset, data));
                total += got;
            }
        }
        Scans = 1;
        return new ScanSummary($"unknown first scan: snapshot of {total / 1_000_000} MB in {snapshot.Count} blocks", 0);
    }

    /// <summary>Filters the candidates (or the snapshot) by op against the current memory.</summary>
    public ScanSummary Next(ScanOp op, double? target, CancellationToken cancel)
    {
        if (Scans == 0)
            throw new InvalidOperationException("no first scan yet");
        if (op == ScanOp.Equal && target is null)
            throw new InvalidOperationException("equal needs a value");
        return snapshot is not null ? NextFromSnapshot(op, target ?? 0, cancel) : NextFromCandidates(op, target ?? 0, cancel);
    }

    private ScanSummary NextFromCandidates(ScanOp op, double target, CancellationToken cancel)
    {
        var size = ScanTypes.SizeOf(Type);
        var keptAddresses = new List<ulong>(Count);
        var keptValues = new List<double>(Count);
        var block = new byte[4096];
        ulong blockBase = 0;
        var blockLength = 0;
        var gone = 0;
        for (var i = 0; i < Count; i++)
        {
            if ((i & 0xFFF) == 0 && cancel.IsCancellationRequested)
                return new ScanSummary($"cancelled, candidates unchanged ({Count})", Count, true);
            var address = AddressAt(i);
            if (blockLength == 0 || address < blockBase || address + (ulong)size > blockBase + (ulong)blockLength)
            {
                blockBase = address & ~0xFFFUL;
                blockLength = memory.Read(blockBase, block);
                throttle.Spent(4096);
            }
            if (address < blockBase || address + (ulong)size > blockBase + (ulong)blockLength)
            {
                gone++;   // unreadable now (freed or decommitted)
                continue;
            }
            var now = ScanTypes.Decode(Type, block.AsSpan((int)(address - blockBase)));
            if (!ScanTypes.Matches(Type, op, now, values[i], target))
                continue;
            keptAddresses.Add(hiddenAddresses[i]);
            keptValues.Add(now);
        }
        hiddenAddresses.Clear();
        hiddenAddresses.AddRange(keptAddresses);
        values.Clear();
        values.AddRange(keptValues);
        Scans++;
        return new ScanSummary($"next scan {op}: {Count} candidates left" + (gone > 0 ? $" ({gone} no longer readable dropped)" : ""), Count);
    }

    private ScanSummary NextFromSnapshot(ScanOp op, double target, CancellationToken cancel)
    {
        var size = ScanTypes.SizeOf(Type);
        var foundAddresses = new List<ulong>();
        var foundValues = new List<double>();
        var current = new byte[Chunk];
        foreach (var (blockBase, data) in snapshot!)
        {
            if (cancel.IsCancellationRequested)
                return new ScanSummary("cancelled, snapshot kept", 0, true);
            var got = memory.Read(blockBase, current.AsSpan(0, data.Length));
            throttle.Spent(got);
            for (var i = 0; i + size <= Math.Min(got, data.Length); i += size)
            {
                var before = ScanTypes.Decode(Type, data.AsSpan(i));
                var now = ScanTypes.Decode(Type, current.AsSpan(i));
                if (!ScanTypes.Matches(Type, op, now, before, target))
                    continue;
                if (foundAddresses.Count >= maxCandidates)
                {
                    return new ScanSummary($"too many matches (over {maxCandidates}) from the snapshot; the snapshot is kept, try changed / increased / decreased instead", Count, false, true);
                }
                foundAddresses.Add(Obf.Hide(blockBase + (ulong)i));
                foundValues.Add(now);
            }
        }
        snapshot = null;
        hiddenAddresses.Clear();
        hiddenAddresses.AddRange(foundAddresses);
        values.Clear();
        values.AddRange(foundValues);
        Scans++;
        return new ScanSummary($"next scan {op} from the snapshot: {Count} candidates", Count);
    }

    /// <summary>Reads the current value at an address with the scan's type; null when unreadable.</summary>
    public double? ReadNow(ulong address)
    {
        Span<byte> bytes = stackalloc byte[4];
        return memory.Read(address, bytes.Slice(0, ScanTypes.SizeOf(Type))) >= ScanTypes.SizeOf(Type) ? ScanTypes.Decode(Type, bytes) : null;
    }
}

#endif

#if DEV_SCANNER
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace XivMate.DataGathering.Forays.Dalamud.Dev;

/// <summary>A known struct in the game (a ClientStructs singleton, agent or director): name, address, size.</summary>
internal sealed record KnownRoot(string Name, ulong Address, ulong Size);

/// <summary>The game module: its image range, so a holder inside it is a static base (module + RVA).</summary>
internal sealed record ModuleRange(string Name, ulong Base, ulong Size);

/// <summary>
/// One pointer path, in the order the game follows it: start at Start (module+RVA or a named struct + offset), then at each
/// step read a pointer and add the offset. The last step lands on the target address.
/// </summary>
internal sealed record PointerChain(string Start, ulong StartAddress, bool IsModule, IReadOnlyList<long> Offsets)
{
    public int Depth => Offsets.Count;

    /// <summary>Cheat Engine layout: the offsets last-applied first.</summary>
    public string CheatEngine => $"{Start}  offsets (CE order, last applied first): {string.Join(", ", Offsets.Reverse().Select(o => "0x" + o.ToString("X")))}";

    public override string ToString() => $"{Start} -> {string.Join(" -> ", Offsets.Select(o => "[ptr]+0x" + o.ToString("X")))}";
}

internal sealed record PointerScanResult(IReadOnlyList<PointerChain> Chains, IReadOnlyList<string> Notes);

/// <summary>
/// CE-style reverse pointer scan, throttled, one pass over memory per level. Level n holds the addresses that contain a
/// pointer into [address - maxOffset, address] of a level n-1 address. A holder inside the game module (static data) or
/// inside a known struct ends the path. Read-only.
/// </summary>
internal sealed class PointerScanner(IScanMemory memory, Throttle throttle)
{
    private const int Chunk = 256 * 1024;

    private readonly record struct Node(ulong HiddenAddress, int Parent, long Offset);

    public PointerScanResult Scan(ulong target, int depth, uint maxOffset, int maxPerLevel, int maxChains,
        ModuleRange? module, IReadOnlyList<KnownRoot> roots, CancellationToken cancel, Action<string>? progress = null)
    {
        var chains = new List<PointerChain>();
        var notes = new List<string>();
        var nodes = new List<Node> { new(Obf.Hide(target), -1, 0) };
        var frontier = new List<int> { 0 };
        for (var level = 1; level <= depth && frontier.Count > 0 && chains.Count < maxChains; level++)
        {
            if (cancel.IsCancellationRequested)
            {
                notes.Add("cancelled");
                break;
            }
            progress?.Invoke($"pointer scan level {level} of {depth}: {frontier.Count} targets, {chains.Count} chains so far");
            var targets = frontier.Select(i => (Address: Obf.Show(nodes[i].HiddenAddress), Index: i)).OrderBy(t => t.Address).ToArray();
            var next = new List<int>();
            var dropped = 0;
            using var table = new SortedTable(targets.Select(t => t.Address).ToArray());
            ScanLevel(table, targets, maxOffset, cancel, (holder, hitIndex, offset) =>
            {
                var parent = targets[hitIndex].Index;
                var node = new Node(Obf.Hide(holder), parent, offset);
                if (module is not null && holder >= module.Base && holder < module.Base + module.Size)
                {
                    nodes.Add(node);
                    if (chains.Count < maxChains)
                        chains.Add(BuildChain($"{module.Name}+0x{holder - module.Base:X}", holder, true, nodes, nodes.Count - 1));
                    return;
                }
                foreach (var root in roots)
                {
                    if (holder >= root.Address && holder < root.Address + Math.Max(root.Size, 8))
                    {
                        nodes.Add(node);
                        if (chains.Count < maxChains)
                            chains.Add(BuildChain($"{root.Name}+0x{holder - root.Address:X}", holder, false, nodes, nodes.Count - 1));
                        return;
                    }
                }
                if (next.Count >= maxPerLevel)
                {
                    dropped++;
                    return;
                }
                nodes.Add(node);
                next.Add(nodes.Count - 1);
            });
            if (dropped > 0)
                notes.Add($"level {level}: {dropped} more non-static holders dropped after the {maxPerLevel} cap (raise maxPerLevel for more)");
            notes.Add($"level {level}: {next.Count} non-static holders kept, {chains.Count} chains so far");
            frontier = next;
        }
        return new PointerScanResult(chains.OrderBy(c => c.Depth).ThenBy(c => c.Start, StringComparer.Ordinal).ToList(), notes);
    }

    /// <summary>Builds the application-order chain from a terminal node back to the target.</summary>
    private static PointerChain BuildChain(string start, ulong startAddress, bool isModule, List<Node> nodes, int terminal)
    {
        var offsets = new List<long>();
        for (var i = terminal; nodes[i].Parent >= 0; i = nodes[i].Parent)
            offsets.Add(nodes[i].Offset);
        return new PointerChain(start, startAddress, isModule, offsets);
    }

    private void ScanLevel(SortedTable table, (ulong Address, int Index)[] targets, uint maxOffset, CancellationToken cancel,
        Action<ulong, int, long> hit)
    {
        var buffer = new byte[Chunk];
        var low = targets[0].Address > maxOffset ? targets[0].Address - maxOffset : 0;
        var high = targets[^1].Address;
        foreach (var region in memory.Regions())
        {
            for (ulong offset = 0; offset < region.Size; offset += Chunk)
            {
                if (cancel.IsCancellationRequested)
                    return;
                var want = (int)Math.Min((ulong)Chunk, region.Size - offset);
                var got = memory.Read(region.Base + offset, buffer.AsSpan(0, want));
                throttle.Spent(got);
                var span = MemoryMarshal.Cast<byte, ulong>(buffer.AsSpan(0, got & ~7));
                for (var i = 0; i < span.Length; i++)
                {
                    var value = span[i];
                    if (value < low || value > high)
                        continue;
                    var holder = region.Base + offset + (ulong)i * 8;
                    if (table.Contains(holder))
                        continue;   // our own lookup table
                    var first = table.LowerBound(value);
                    for (var k = first; k < targets.Length && k < first + 4 && targets[k].Address - value <= maxOffset; k++)
                        hit(holder, k, (long)(targets[k].Address - value));
                }
            }
        }
    }

    /// <summary>Sorted target addresses in unmanaged memory, so the scan can skip its own table.</summary>
    private sealed unsafe class SortedTable : IDisposable
    {
        private readonly ulong* items;
        private readonly int count;

        public SortedTable(ulong[] sorted)
        {
            count = sorted.Length;
            items = (ulong*)NativeMemory.Alloc((nuint)Math.Max(1, count) * 8);
            sorted.AsSpan().CopyTo(new Span<ulong>(items, count));
        }

        public bool Contains(ulong address) => address >= (ulong)items && address < (ulong)(items + count);

        /// <summary>Index of the first item at or above value.</summary>
        public int LowerBound(ulong value)
        {
            int lo = 0, hi = count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (items[mid] < value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        public void Dispose() => NativeMemory.Free(items);
    }
}

/// <summary>Follows a saved chain: read the pointer at the start, add the offset, repeat; the final address is the value.</summary>
internal static class ChainResolver
{
    /// <summary>Returns the final address (the value lives there) or null when a hop is unreadable, null or not plausible.</summary>
    public static ulong? Resolve(IScanMemory memory, ulong startAddress, IReadOnlyList<long> offsets)
    {
        var address = startAddress;
        Span<byte> pointer = stackalloc byte[8];
        foreach (var offset in offsets)
        {
            if (memory.Read(address, pointer) < 8)
                return null;
            var p = BinaryPrimitives.ReadUInt64LittleEndian(pointer);
            if (p < 0x10000 || p > 0x7FFF_FFFF_FFFF)
                return null;
            address = (ulong)((long)p + offset);
        }
        return address;
    }

    public static string Format(IReadOnlyList<PointerChain> chains, int max)
    {
        var text = new StringBuilder();
        foreach (var chain in chains.Take(max))
            text.AppendLine($"depth {chain.Depth}: {chain}   |   {chain.CheatEngine}");
        return text.ToString();
    }
}

#endif

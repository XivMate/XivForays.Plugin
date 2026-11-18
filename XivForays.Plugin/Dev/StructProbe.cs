#if DEV_SCANNER
using System.Buffers.Binary;
using System.Text;

namespace XivMate.DataGathering.Forays.Dalamud.Dev;

// DEVELOPER-ONLY, pure logic (no Dalamud types), read-only: looks inside known game structs for the target address.

/// <summary>A pointer field of a struct whose pointee lies near the target: pointee + PointeeToTarget = target.</summary>
internal readonly record struct FieldHit(ulong FieldOffset, ulong Pointer, long PointeeToTarget);

internal static class StructProbe
{
    public const ulong DefaultBelow = 0x2000;
    public const ulong DefaultAbove = 0x200;
    private const ulong MaxBytes = 0x20000;

    /// <summary>Reads up to size bytes of the struct (capped), or an empty array when unreadable.</summary>
    public static byte[] ReadStruct(IScanMemory memory, KnownRoot root)
    {
        var size = (int)Math.Min(Math.Max(root.Size, 8), MaxBytes);
        var bytes = new byte[size];
        var got = memory.Read(root.Address, bytes);
        return got == size ? bytes : got <= 0 ? [] : bytes[..(got & ~7)];
    }

    /// <summary>True when target lies in [base, base + size + slack]; offset is target - base.</summary>
    public static bool Inside(KnownRoot root, ulong target, ulong slack, out long offset)
    {
        offset = (long)(target - root.Address);
        return target >= root.Address && target <= root.Address + root.Size + slack;
    }

    /// <summary>Every 8-byte aligned field holding a pointer into [target - below, target + above].</summary>
    public static IReadOnlyList<FieldHit> FindFields(byte[] structBytes, ulong target, ulong below = DefaultBelow, ulong above = DefaultAbove)
    {
        var hits = new List<FieldHit>();
        var low = target > below ? target - below : 0;
        var high = target + above;
        for (var o = 0; o + 8 <= structBytes.Length; o += 8)
        {
            var p = BinaryPrimitives.ReadUInt64LittleEndian(structBytes.AsSpan(o, 8));
            if (p >= low && p <= high)
                hits.Add(new FieldHit((ulong)o, p, (long)target - (long)p));
        }
        return hits;
    }

    /// <summary>Text report for the scanStructs file: one block per root.</summary>
    public static string Describe(IScanMemory memory, IReadOnlyList<KnownRoot> roots, ulong target)
    {
        var text = new StringBuilder().AppendLine($"scanStructs for 0x{target:X}: {roots.Count} structs, pointer window [target-0x{DefaultBelow:X}, target+0x{DefaultAbove:X}], inside test [base, base+size+0x{DefaultBelow:X}]");
        foreach (var root in roots)
        {
            text.AppendLine($"{root.Name}: base 0x{root.Address:X} size 0x{root.Size:X}");
            var inside = Inside(root, target, DefaultBelow, out var offset);
            if (inside)
                text.AppendLine($"  TARGET INSIDE: offset +0x{offset:X}");
            var hits = FindFields(ReadStruct(memory, root), target);
            foreach (var h in hits)
                text.AppendLine($"  field +0x{h.FieldOffset:X} = 0x{h.Pointer:X}; pointee + 0x{h.PointeeToTarget:X} = target");
            if (hits.Count == 0 && !inside)
                text.AppendLine("  nothing");
        }
        return text.ToString();
    }

    /// <summary>
    /// Chains that start inside a known struct: one hop (a struct field points at or before the target) and two hops (a
    /// struct field points to an object, a pointer field in that object points at or before the target). In application
    /// order for ChainResolver: start at the struct field, read the pointer, add the offset, repeat.
    /// </summary>
    public static IReadOnlyList<PointerChain> Chains(IScanMemory memory, IReadOnlyList<KnownRoot> roots, ulong target, uint maxOffset,
        int hops, int objectBytes, DateTime deadline, int max = 200)
    {
        var chains = new List<PointerChain>();
        var inner = new byte[objectBytes];
        foreach (var root in roots)
        {
            if (DateTime.UtcNow > deadline)
                break;
            var bytes = ReadStruct(memory, root);
            for (var o = 0; o + 8 <= bytes.Length && chains.Count < max; o += 8)
            {
                var p = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(o, 8));
                if (p < 0x10000 || p > 0x7FFF_FFFF_FFFF)
                    continue;
                var start = $"{root.Name}+0x{o:X}";
                var startAddress = root.Address + (ulong)o;
                if (p <= target && target - p <= maxOffset)
                    chains.Add(new PointerChain(start, startAddress, false, [(long)(target - p)]));
                else if (hops >= 2 && DateTime.UtcNow <= deadline)
                {
                    var got = memory.Read(p, inner);
                    for (var i = 0; i + 8 <= got && chains.Count < max; i += 8)
                    {
                        var q = BinaryPrimitives.ReadUInt64LittleEndian(inner.AsSpan(i, 8));
                        if (q >= 0x10000 && q <= 0x7FFF_FFFF_FFFF && q <= target && target - q <= maxOffset)
                            chains.Add(new PointerChain(start, startAddress, false, [i, (long)(target - q)]));
                    }
                }
            }
        }
        return chains.OrderBy(c => c.Depth).ThenBy(c => c.Start, StringComparer.Ordinal).ToList();
    }
}
#endif

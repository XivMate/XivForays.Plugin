#if DEV_SCANNER
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json.Nodes;
using XivMate.DataGathering.Forays.Dalamud.Dev;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>Developer-only scanner: runs only with -p:DevScanner=true. Fake memory, no game.</summary>
public sealed class DevScannerTests
{
    private const ulong Base = 0x1_0000_0000;

    /// <summary>One flat read-write region.</summary>
    private sealed class FakeMemory(int size, ulong moduleSize = 0) : IScanMemory
    {
        public readonly byte[] Bytes = new byte[size];
        public IEnumerable<MemRegion> Regions() => [new MemRegion(Base, (ulong)Bytes.Length, false)];

        public int Read(ulong address, Span<byte> buffer)
        {
            if (address < Base || address >= Base + (ulong)Bytes.Length)
                return 0;
            var n = (int)Math.Min((ulong)buffer.Length, Base + (ulong)Bytes.Length - address);
            Bytes.AsSpan((int)(address - Base), n).CopyTo(buffer);
            return n;
        }

        public void SetInt(int offset, int v) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(offset), v);
        public void SetPtr(int offset, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Bytes.AsSpan(offset), v);
    }

    private static ValueScanner NewScanner(IScanMemory m, int cap = 1000, long snapshotCap = 1_000_000_000) =>
        new(m, new Throttle(sleep: _ => { }), cap, snapshotCap);

    [Fact]
    public void FirstAndNextScansNarrowToTheChangingValue()
    {
        var mem = new FakeMemory(0x2000);
        mem.SetInt(0x100, 61);
        mem.SetInt(0x500, 61);
        mem.SetInt(0x900, 61);
        var scanner = NewScanner(mem);
        Assert.Equal(3, scanner.First(ScanType.Int32, 61, default).Count);
        mem.SetInt(0x100, 70);      // grows
        mem.SetInt(0x500, 40);      // shrinks
        Assert.Equal(2, scanner.Next(ScanOp.Changed, null, default).Count);
        mem.SetInt(0x100, 80);
        var up = scanner.Next(ScanOp.Increased, null, default);
        Assert.Equal(1, up.Count);
        Assert.Equal(Base + 0x100, scanner.AddressAt(0));
        Assert.Equal(80, scanner.ValueAt(0));
        Assert.Equal(1, scanner.Next(ScanOp.Unchanged, null, default).Count);
        Assert.Equal(1, scanner.Next(ScanOp.Equal, 80, default).Count);
        Assert.Equal(0, scanner.Next(ScanOp.Equal, 81, default).Count);
    }

    [Fact]
    public void UnknownFirstScanSnapshotsThenFilters()
    {
        var mem = new FakeMemory(0x1000);
        var scanner = NewScanner(mem);
        Assert.True(scanner.First(ScanType.Unknown, null, default).Message.Contains("snapshot", StringComparison.Ordinal));
        mem.SetInt(0x40, 5);
        mem.SetInt(0x80, 9);
        Assert.Equal(2, scanner.Next(ScanOp.Changed, null, default).Count);
        Assert.False(scanner.HasSnapshot);
        mem.SetInt(0x40, 4);
        Assert.Equal(1, scanner.Next(ScanOp.Decreased, null, default).Count);
    }

    [Fact]
    public void SnapshotCapAndCandidateCapAreReported()
    {
        var mem = new FakeMemory(0x1000);
        Assert.True(NewScanner(mem, snapshotCap: 100).First(ScanType.Unknown, null, default).Truncated);
        var scanner = NewScanner(mem, cap: 10);
        var summary = scanner.First(ScanType.Int32, 0, default);   // zeros everywhere
        Assert.True(summary.Truncated);
        Assert.Equal(10, summary.Count);
    }

    [Fact]
    public void FloatByteAndInt16TypesWork()
    {
        var mem = new FakeMemory(0x1000);
        BinaryPrimitives.WriteSingleLittleEndian(mem.Bytes.AsSpan(0x20), 0.75f);
        mem.Bytes[0x31] = 200;
        BinaryPrimitives.WriteInt16LittleEndian(mem.Bytes.AsSpan(0x42), -300);
        Assert.Equal(1, NewScanner(mem).First(ScanType.Float, 0.75, default).Count);
        Assert.Equal(1, NewScanner(mem).First(ScanType.Byte, 200, default).Count);
        Assert.Equal(1, NewScanner(mem).First(ScanType.Int16, -300, default).Count);
    }

    [Fact]
    public void CancelledScanReportsIt()
    {
        var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.True(NewScanner(new FakeMemory(0x1000)).First(ScanType.Int32, 0, cancel.Token).Cancelled);
    }

    [Fact]
    public void ThrottleSleepsOncePerSlice()
    {
        var sleeps = 0;
        var throttle = new Throttle(1000, 3, _ => sleeps++);
        for (var i = 0; i < 10; i++)
            throttle.Spent(300);
        Assert.Equal(2, sleeps);
    }

    [Fact]
    public void PointerScanFindsAStaticChainAndKnownRoots()
    {
        // static holder (inside the "module") at +0x100 -> object A; A+0x38 -> object B; B+0x10 -> target block (value at +0x134)
        var mem = new FakeMemory(0x10000);
        const int a = 0x2000, b = 0x3000, block = 0x4000;
        mem.SetPtr(0x100, Base + a);
        mem.SetPtr(a + 0x38, Base + b);
        mem.SetPtr(b + 0x10, Base + block);
        mem.SetInt(block + 0x134, 61);
        var module = new ModuleRange("game.exe", Base, 0x1000);
        var target = Base + block + 0x134;
        var result = new PointerScanner(mem, new Throttle(sleep: _ => { })).Scan(target, 4, 0x400, 1000, 50, module, [], default);
        var chain = Assert.Single(result.Chains);
        Assert.True(chain.IsModule);
        Assert.Equal("game.exe+0x100", chain.Start);
        Assert.Equal([0x38L, 0x10, 0x134], chain.Offsets);   // read the static pointer (A), +0x38, read (B), +0x10, read (block), +0x134
        Assert.Equal(target, ChainResolver.Resolve(mem, Base + 0x100, chain.Offsets));
        Assert.Contains("0x134", chain.CheatEngine, StringComparison.Ordinal);

        // a named struct instead of the module
        var named = new PointerScanner(mem, new Throttle(sleep: _ => { })).Scan(target, 4, 0x400, 1000, 50, null,
            [new KnownRoot("Agent#9", Base + 0x2000, 0x100)], default);
        Assert.Contains(named.Chains, c => c.Start == "Agent#9+0x38");
    }

    [Fact]
    public void ResolverStopsOnNullOrWildPointers()
    {
        var mem = new FakeMemory(0x1000);
        mem.SetPtr(0, 0);
        Assert.Null(ChainResolver.Resolve(mem, Base, [0x10]));
        mem.SetPtr(0, 0xFFFF_FFFF_FFFF_0000);
        Assert.Null(ChainResolver.Resolve(mem, Base, [0x10]));
        Assert.Null(ChainResolver.Resolve(mem, 0x10, [0x10]));
    }

    [Fact]
    public void ControllerRunsScansAndWritesTheFiles()
    {
        var mem = new FakeMemory(0x2000);
        mem.SetInt(0x100, 61);
        mem.SetInt(0x200, 61);
        var dir = Directory.CreateTempSubdirectory("scan-").FullName;
        using var controller = new ScannerController(mem, () => new ModuleRange("game.exe", Base, 0x10), () => [], dir, _ => { }, new Throttle(sleep: _ => { }));
        controller.Execute("t1", "scanFirst", JsonNode.Parse("""{"type":"int32","value":61}""")!.AsObject());
        controller.WaitIdle();
        Assert.Contains("2 candidates", File.ReadAllText(Path.Combine(dir, "t1-first.txt")), StringComparison.Ordinal);
        mem.SetInt(0x100, 62);
        controller.Execute("t2", "scanNext", JsonNode.Parse("""{"op":"increased"}""")!.AsObject());
        controller.WaitIdle();
        Assert.Contains("1 candidates", File.ReadAllText(Path.Combine(dir, "t2-first.txt".Replace("first", "next"))), StringComparison.Ordinal);
        controller.Execute("t3", "scanList", []);
        Assert.Contains("0x" + (Base + 0x100).ToString("X"), File.ReadAllText(Path.Combine(dir, "t3-list.txt")), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => controller.Execute("t4", "scanFirst", JsonNode.Parse("""{"type":"banana","value":1}""")!.AsObject()));
        controller.Execute("t5", "scanWatch", JsonNode.Parse("""{"seconds":1}""")!.AsObject());
        Thread.Sleep(1200);
        controller.Execute("t5b", "scanWatchStop", []);
        Assert.Contains("62", File.ReadAllText(Path.Combine(dir, "t5-watch.txt")), StringComparison.Ordinal);
        var resolved = controller.Execute("t6", "scanResolve", JsonNode.Parse($"{{\"start\":\"0x{Base + 0x100:X}\",\"offsets\":[]}}")!.AsObject());
        Assert.Contains("62", System.Text.Json.JsonSerializer.Serialize(resolved), StringComparison.Ordinal);
    }

    // Layout: struct S at Base+0x100 (size 0x80); its field +0x10 points to object O at Base+0x400; O+0x28 points to
    // Base+0x800, which is a block holding the target value at +0x20 (target = Base+0x820).
    private static FakeMemory StructWorld(out KnownRoot root, out ulong target)
    {
        var mem = new FakeMemory(0x2000);
        root = new KnownRoot("S", Base + 0x100, 0x80);
        mem.SetPtr(0x110, Base + 0x400);
        mem.SetPtr(0x428, Base + 0x800);
        mem.SetPtr(0x130, Base + 0x700);   // direct hop: 0x700 + 0x120 = 0x820
        target = Base + 0x820;
        return mem;
    }

    [Fact]
    public void StructProbeFindsInsideAndPointerFields()
    {
        var mem = StructWorld(out var root, out var target);
        Assert.True(StructProbe.Inside(root, Base + 0x180 + 0x10, 0x2000, out var offset));
        Assert.Equal(0x90, offset);
        Assert.False(StructProbe.Inside(root, Base + 0x180 + 0x2001, 0x2000, out _));
        var hits = StructProbe.FindFields(StructProbe.ReadStruct(mem, root), target);
        Assert.Equal(2, hits.Count);
        Assert.Equal(0x10UL, hits[0].FieldOffset);
        Assert.Equal(0x420, hits[0].PointeeToTarget);
        Assert.Equal(0x30UL, hits[1].FieldOffset);
        Assert.Equal(0x120, hits[1].PointeeToTarget);
        var report = StructProbe.Describe(mem, [root], target);
        Assert.Contains("field +0x30", report, StringComparison.Ordinal);
        Assert.Contains("S: base 0x" + root.Address.ToString("X"), report, StringComparison.Ordinal);
    }

    [Fact]
    public void StructChainsAreOneAndTwoHopsAndResolveToTheTarget()
    {
        var mem = StructWorld(out var root, out var target);
        var chains = StructProbe.Chains(mem, [root], target, 0x400, 2, 0x100, DateTime.UtcNow.AddSeconds(30));
        Assert.Equal(2, chains.Count);
        Assert.Equal(1, chains[0].Depth);
        Assert.Equal(2, chains[1].Depth);
        foreach (var chain in chains)
            Assert.Equal(target, ChainResolver.Resolve(mem, chain.StartAddress, chain.Offsets));
        Assert.Equal("S+0x10", chains[1].Start);
        Assert.Single(StructProbe.Chains(mem, [root], target, 0x400, 1, 0x100, DateTime.UtcNow.AddSeconds(30)));
        Assert.Empty(StructProbe.Chains(mem, [root], target, 0x400, 2, 0x100, DateTime.UtcNow.AddSeconds(-1)));
    }

    [Fact]
    public void ControllerStructCommandsWriteFilesAndOwnerUsesStructsFirst()
    {
        var mem = StructWorld(out var root, out var target);
        var dir = Directory.CreateTempSubdirectory("scan-").FullName;
        using var controller = new ScannerController(mem, () => new ModuleRange("game.exe", Base, 0x10), () => [], dir, _ => { },
            new Throttle(sleep: _ => { }), () => [root]);
        var summary = controller.Execute("s1", "scanStructs", JsonNode.Parse($"{{\"address\":\"0x{target:X}\"}}")!.AsObject());
        Assert.Contains("withPointers", System.Text.Json.JsonSerializer.Serialize(summary), StringComparison.Ordinal);
        Assert.Contains("field +0x30", File.ReadAllText(Path.Combine(dir, "s1-structs.txt")), StringComparison.Ordinal);
        controller.Execute("o1", "scanOwner", JsonNode.Parse($"{{\"address\":\"0x{target:X}\",\"roots\":\"structs\",\"timeLimit\":5,\"maxOffset\":1024,\"objectBytes\":256}}")!.AsObject());
        controller.WaitIdle();
        var owner = File.ReadAllText(Path.Combine(dir, "o1-owner.txt"));
        Assert.Contains("S+0x10", owner, StringComparison.Ordinal);
        Assert.Contains("generic heap walk skipped", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void ScannerHasNoWriteApi()
    {
        var types = typeof(IScanMemory).Assembly.GetTypes().Where(t => t.Namespace == "XivMate.DataGathering.Forays.Dalamud.Dev");
        foreach (var type in types)
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                Assert.DoesNotContain("Write", method.Name.Replace("WriteText", "").Replace("WriteInt", ""), StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(ProcessScanMemory).GetMethods(BindingFlags.NonPublic | BindingFlags.Static), m => m.Name.Contains("WriteProcessMemory", StringComparison.Ordinal));
    }
}
#endif

using XivMate.DataGathering.Forays.Dalamud.Core;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>Kettle light value: the pointer chain, the build guard and the section lookup, all against a fake memory.</summary>
public class KettleLightValueTests
{
    private const ulong Module = 0x7FF600000000;

    private sealed class FakeMemory : IProcessMemory
    {
        public readonly Dictionary<ulong, ulong> Pointers = new();
        public readonly Dictionary<ulong, int> Ints = new();
        public int Reads;

        public bool TryReadPointer(ulong address, out ulong value)
        {
            Reads++;
            return Pointers.TryGetValue(address, out value);
        }

        public bool TryReadInt32(ulong address, out int value)
        {
            Reads++;
            return Ints.TryGetValue(address, out value);
        }
    }

    private static FakeMemory FullChain(int value)
    {
        var m = new FakeMemory();
        m.Pointers[Module + 0x19053B8] = 0x1000_0000;
        m.Pointers[0x1000_0000 + 0x38] = 0x2000_0000;
        m.Pointers[0x2000_0000 + 0x10] = 0x3000_0000;
        m.Pointers[0x3000_0000 + 0xC8] = 0x4000_0000;
        m.Pointers[0x4000_0000 + 0x20] = 0x5000_0000;
        m.Ints[0x5000_0000 + 0x134] = value;
        return m;
    }

    [Fact]
    public void ChainFollowsBaseThen38_10_C8_20_and_ReadsTheIntAt134()
    {
        Assert.Equal<uint?>(61, KettleLightChain.Read(FullChain(61), Module));
        Assert.Equal<uint?>(0, KettleLightChain.Read(FullChain(0), Module));
    }

    [Theory]
    [InlineData(0x7FF600000000UL + 0x19053B8UL)]
    [InlineData(0x1000_0000UL + 0x38)]
    [InlineData(0x2000_0000UL + 0x10)]
    [InlineData(0x3000_0000UL + 0xC8)]
    [InlineData(0x4000_0000UL + 0x20)]
    public void AnyBrokenHopGivesNull(ulong hopAddress)
    {
        var m = FullChain(61);
        m.Pointers.Remove(hopAddress);
        Assert.Null(KettleLightChain.Read(m, Module));
    }

    [Fact]
    public void NullPointerAndFinalReadFailureGiveNull()
    {
        var m = FullChain(61);
        m.Pointers[0x2000_0000 + 0x10] = 0;
        Assert.Null(KettleLightChain.Read(m, Module));

        var n = FullChain(61);
        n.Ints.Clear();
        Assert.Null(KettleLightChain.Read(n, Module));
    }

    [Fact]
    public void KernelLookingAndTinyPointersAreRejectedWithoutFurtherReads()
    {
        var m = FullChain(61);
        m.Pointers[0x3000_0000 + 0xC8] = 0xFFFF_8000_0000_0000;
        Assert.Null(KettleLightChain.Read(m, Module));
        var small = FullChain(61);
        small.Pointers[Module + 0x19053B8] = 0x40;
        Assert.Null(KettleLightChain.Read(small, Module));
        Assert.Equal(1, small.Reads);
        Assert.Null(KettleLightChain.Read(new FakeMemory(), 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void ImplausibleValuesGiveNull(int value) => Assert.Null(KettleLightChain.Read(FullChain(value), Module));

    [Fact]
    public void ReadingNeverWrites() => Assert.Empty(typeof(IProcessMemory).GetMethods().Where(m => m.Name.StartsWith("Write", StringComparison.Ordinal)));

    [Fact]
    public void GuardAllowsOnlyAKnownVersionInWritableData()
    {
        const uint data = 0xC0000040; // read, write, initialized data
        const uint code = 0x60000020; // read, execute, code
        const uint rdata = 0x40000040;
        var known = KettleLightGuard.AllowedVersions[0];
        Assert.Null(KettleLightGuard.Reason(known, data));
        Assert.Contains("code or read-only", KettleLightGuard.Reason(known, code));
        Assert.Contains("code or read-only", KettleLightGuard.Reason(known, rdata));
        Assert.Contains("outside every section", KettleLightGuard.Reason(known, null));
        Assert.Contains("not one the kettle value address", KettleLightGuard.Reason("2099.01.01.0000.0000", data));
        Assert.Contains("unknown", KettleLightGuard.Reason(null, data));
    }

    [Fact]
    public void SectionLookupReadsTheLoadedHeadersAndFindsTheCoveringSection()
    {
        var m = new FakeMemory();
        m.Ints[Module + 0x3C] = 0x100;
        const ulong pe = Module + 0x100;
        m.Ints[pe] = 0x4550;
        m.Ints[pe + 4] = 0x0002_8664; // machine 0x8664, 2 sections
        m.Ints[pe + 20] = 0x00F0; // optional header size 0xF0
        var first = pe + 24 + 0xF0;
        void Section(int i, int vsize, int va, uint ch)
        {
            var o = first + (ulong)(i * 40);
            m.Ints[o + 8] = vsize;
            m.Ints[o + 12] = va;
            m.Ints[o + 36] = unchecked((int)ch);
        }

        Section(0, 0x2100000, 0x1000, 0x60000020); // code
        Section(1, 0x700000, 0x28AE000, 0xC0000040); // data
        Assert.Equal<uint?>(0x60000020, KettleLightGuard.SectionCharacteristics(m, Module, 0x19053B8));
        Assert.Equal<uint?>(0xC0000040, KettleLightGuard.SectionCharacteristics(m, Module, 0x29053B8));
        Assert.Null(KettleLightGuard.SectionCharacteristics(m, Module, 0x5000000));
        Assert.Null(KettleLightGuard.SectionCharacteristics(new FakeMemory(), Module, 0x19053B8));
    }
}

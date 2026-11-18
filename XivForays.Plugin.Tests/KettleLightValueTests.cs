using XivMate.DataGathering.Forays.Dalamud.Core;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>Kettle light value: the director pointer path, the sanity limits, the build guard and the delta maths, all against a fake memory.</summary>
public class KettleLightValueTests
{
    private const ulong Director = 0x26B_051A_E980;
    private const ulong Block = 0x224_0B76_1080;

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

    private static FakeMemory Chain(int yield, int bar)
    {
        var m = new FakeMemory();
        m.Pointers[Director + 0x440] = Block;
        m.Ints[Block + 0x78] = yield;
        m.Ints[Block + 0x1D8] = bar;
        return m;
    }

    [Fact]
    public void ReadsYieldAndBarThroughThePointerAtDirectorPlus440()
    {
        Assert.Equal(KettleReadStatus.Ok, KettleLightChain.Try(Chain(6, 91), Director, out var r));
        Assert.Equal(new KettleReading(6, 91), r);
        Assert.Equal(KettleReadStatus.Ok, KettleLightChain.Try(Chain(0, 0), Director, out r));
        Assert.Equal(new KettleReading(0, 0), r);
    }

    [Fact]
    public void ThePointerIsReadAgainEverySample_SoAMovedBlockIsFollowed()
    {
        var m = Chain(1, 10);
        Assert.Equal(KettleReadStatus.Ok, KettleLightChain.Try(m, Director, out var first));
        m.Pointers[Director + 0x440] = 0x300_0000_0000;
        m.Ints[0x300_0000_0000 + 0x78] = 2;
        m.Ints[0x300_0000_0000 + 0x1D8] = 20;
        Assert.Equal(KettleReadStatus.Ok, KettleLightChain.Try(m, Director, out var second));
        Assert.Equal(new KettleReading(1, 10), first);
        Assert.Equal(new KettleReading(2, 20), second);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0x40UL)]
    [InlineData(0xFFFF_8000_0000_0000UL)]
    public void ABadDirectorIsUnreadableWithoutAnyRead(ulong director)
    {
        var m = Chain(1, 1);
        Assert.Equal(KettleReadStatus.Unreadable, KettleLightChain.Try(m, director, out _));
        Assert.Equal(0, m.Reads);
    }

    [Fact]
    public void BrokenHopsAreUnreadable()
    {
        var noPointer = Chain(1, 1);
        noPointer.Pointers.Remove(Director + 0x440);
        Assert.Equal(KettleReadStatus.Unreadable, KettleLightChain.Try(noPointer, Director, out _));

        var nullBlock = Chain(1, 1);
        nullBlock.Pointers[Director + 0x440] = 0;
        Assert.Equal(KettleReadStatus.Unreadable, KettleLightChain.Try(nullBlock, Director, out _));

        var kernelBlock = Chain(1, 1);
        kernelBlock.Pointers[Director + 0x440] = 0xFFFF_8000_0000_0000;
        Assert.Equal(KettleReadStatus.Unreadable, KettleLightChain.Try(kernelBlock, Director, out _));

        var noBar = Chain(1, 1);
        noBar.Ints.Remove(Block + 0x1D8);
        Assert.Equal(KettleReadStatus.Unreadable, KettleLightChain.Try(noBar, Director, out _));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(0, 121)]
    [InlineData(-1, 50)]
    [InlineData(1000, 50)]
    public void ImpossibleNumbersAreOutOfRange(int yield, int bar) =>
        Assert.Equal(KettleReadStatus.OutOfRange, KettleLightChain.Try(Chain(yield, bar), Director, out _));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(999, 120)]
    public void TheLimitsThemselvesAreAccepted(int yield, int bar) =>
        Assert.Equal(KettleReadStatus.Ok, KettleLightChain.Try(Chain(yield, bar), Director, out _));

    [Fact]
    public void ReadingNeverWrites() => Assert.Empty(typeof(IProcessMemory).GetMethods().Where(m => m.Name.StartsWith("Write", StringComparison.Ordinal)));

    [Fact]
    public void GuardAllowsOnlyTheCheckedBuild()
    {
        Assert.Equal(["2026.11.03.0000.0000"], KettleLightGuard.AllowedVersions);
        Assert.Null(KettleLightGuard.Reason("2026.11.03.0000.0000"));
        Assert.Contains("not one the kettle offsets", KettleLightGuard.Reason("2099.01.01.0000.0000"));
        Assert.Contains("unknown", KettleLightGuard.Reason(null));
    }

    [Fact]
    public void DeltaWithTheSameYieldNeedsNoCap()
    {
        Assert.Equal<int?>(12, KettleLightChain.Delta(new KettleReading(5, 40), new KettleReading(5, 52), null));
        Assert.Equal<int?>(-80, KettleLightChain.Delta(new KettleReading(5, 80), new KettleReading(5, 0), null));
        Assert.Equal<int?>(0, KettleLightChain.Delta(new KettleReading(5, 60), new KettleReading(5, 60), 100));
    }

    [Fact]
    public void DeltaAcrossARolloverUsesTheCap_AndIsNullWhenTheCapIsUnknown()
    {
        // 95 -> next yield, bar 10 with cap 100 is 15 points.
        Assert.Equal<int?>(15, KettleLightChain.Delta(new KettleReading(5, 95), new KettleReading(6, 10), 100));
        // forge: yield 6 bar 10 -> yield 0 bar 0 is -(6*100+10) with cap 100.
        Assert.Equal<int?>(-610, KettleLightChain.Delta(new KettleReading(6, 10), new KettleReading(0, 0), 100));
        Assert.Null(KettleLightChain.Delta(new KettleReading(5, 95), new KettleReading(6, 10), null));
        Assert.Null(KettleLightChain.Delta(null, new KettleReading(6, 10), 100));
        Assert.Null(KettleLightChain.Delta(new KettleReading(6, 10), null, 100));
        Assert.Null(KettleLightChain.BarCap);
    }
}

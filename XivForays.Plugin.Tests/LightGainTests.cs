using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

public class LightGainTests
{
    [Theory]
    [InlineData(9020u, 1)]
    [InlineData(9021u, 2)]
    [InlineData(9026u, 7)]
    [InlineData(9027u, 8)]
    [InlineData(3743u, 9)]
    public void LightRowsMapToTypes(uint row, int type) => Assert.Equal((byte)type, LightGainRows.Type(row));

    [Theory]
    [InlineData(9019u)]
    [InlineData(9028u)]
    [InlineData(3741u)]
    [InlineData(0u)]
    public void OtherRowsAreNotLight(uint row) => Assert.Null(LightGainRows.Type(row));

    [Fact]
    public void KettleOnlyInPagosAndPyros()
    {
        Assert.True(LightGainRows.InKettleZone(ForayZones.EurekaPagos));
        Assert.True(LightGainRows.InKettleZone(ForayZones.EurekaPyros));
        Assert.False(LightGainRows.InKettleZone(ForayZones.EurekaAnemos));
        Assert.False(LightGainRows.InKettleZone(ForayZones.EurekaHydatos));
    }

    private static GainKill Kill(long at, uint id) => new(at, id, false, false, 0, 1, Guid.NewGuid());

    [Fact]
    public void KillLinksWithinThreeSecondsBefore()
    {
        var kills = new GainKills();
        kills.Add(Kill(10_000, 1));
        kills.Add(Kill(11_000, 2));
        var link = kills.Find(12_500);
        Assert.NotNull(link);
        Assert.Equal(2u, link!.Value.Kill.GameId);
        Assert.Equal(1500u, link.Value.GapMs);
    }

    [Fact]
    public void KillBoundariesAreInclusiveAtZeroAndThreeThousand()
    {
        var kills = new GainKills();
        kills.Add(Kill(10_000, 1));
        Assert.NotNull(kills.Find(10_000));
        Assert.NotNull(kills.Find(13_000));
        Assert.Null(kills.Find(13_001));
        Assert.Null(kills.Find(9_999));
    }

    [Fact]
    public void ChainNoticeAfterTheLightNoticeIsAttached()
    {
        var chains = new ChainNotices();
        chains.Add(7, 10_900);
        chains.Add(8, 14_000);
        var found = chains.Find(10_000, 10_000);
        Assert.Equal(7u, found!.Value.Number);
        Assert.Equal(900, found.Value.GapMs);
    }

    [Fact]
    public void ChainNoticeBeforeTheKillIsAttachedAndFarOnesAreNot()
    {
        var chains = new ChainNotices();
        chains.Add(30, 9_000);
        Assert.Equal(30u, chains.Find(10_000, 10_500)!.Value.Number);
        Assert.Null(chains.Find(13_001, 13_001));
    }

    [Fact]
    public void KillAfterTheLightNoticeIsFoundByFindAfter()
    {
        var kills = new GainKills();
        kills.Add(Kill(10_800, 5));
        Assert.Null(kills.Find(10_000));
        Assert.Equal(5u, kills.FindAfter(10_000, 1800)!.Value.Kill.GameId);
        Assert.Equal(800u, kills.FindAfter(10_000, 1800)!.Value.GapMs);
        Assert.Null(kills.FindAfter(10_000, 500));
    }

    [Fact]
    public void UpsertFillsTheEntryOfTheSameDefeat()
    {
        var kills = new GainKills();
        kills.Upsert(new GainKill(10_000, null, null, null, null, 1, null, 77));
        kills.Upsert(Kill(10_000, 5));
        var link = kills.Find(10_500)!.Value.Kill;
        Assert.Equal(5u, link.GameId);
        Assert.Equal(77u, link.NameId);
    }

    [Fact]
    public void NoKillMeansNoLink()
    {
        Assert.Null(new GainKills().Find(5000));
    }

    [Fact]
    public void BeforeSampleIsNewestAtOrBeforeCutoff()
    {
        var samples = new GainSamples();
        samples.Add(new GainSample(1000, 1, 10, 100, 500, 5, 5, false));
        samples.Add(new GainSample(2000, 1, 22, 150, 500, 5, 5, false));
        samples.Add(new GainSample(3000, 1, 35, 200, 500, 5, 5, false));
        Assert.Equal(22, samples.Before(2500)!.Value.Bar);
        Assert.Equal(10, samples.Before(1000)!.Value.Bar);
        Assert.Null(samples.Before(999));
    }

    [Fact]
    public void NoticeNetDedupesAndThrottlesAndSkipsLightRows()
    {
        var net = new NoticeNet();
        Assert.False(net.Accept(9020, [], 0));
        Assert.True(net.Accept(500, [1, 2], 0));
        Assert.False(net.Accept(500, [1, 2], 1000));
        Assert.True(net.Accept(500, [1, 3], 1000));
        Assert.True(net.Accept(500, [1, 2], NoticeNet.DedupeMs + 1));
        var fresh = new NoticeNet();
        var accepted = 0;
        for (uint row = 1; row <= 40; row++)
            if (fresh.Accept(row, [], 100))
                accepted++;
        Assert.Equal(NoticeNet.MaxPerWindow, accepted);
    }

    [Fact]
    public void ExpBuffsAreAllowListed()
    {
        Assert.True(ContextRules.IsAllowedStatusName("Elemental EXP Up"));
        Assert.True(ContextRules.IsAllowedStatusName("Elemental Blessing: EXP"));
        Assert.True(ContextRules.IsAllowedStatusName("Elemental Harmony"));
        Assert.False(ContextRules.IsAllowedStatusName("Medicated"));
    }

    [Fact]
    public void LogLineIsNumbersOnly()
    {
        var o = new Observation
        {
            Kind = ObservationKind.LightGain, NoticeRow = 9021, GainType = 2, GainBarBefore = 25, GainBarAfter = 37,
            GainYieldBefore = 3, GainYieldAfter = 3, GameId = 77, GainKillGapMs = 420, ChainNumber = 12,
            Situation = new PlayerContext { Statuses = [[1732, 0, 0], [1461, 0, 100]], GroupSize = 2 }
        };
        var line = LightGainLog.Line(o);
        Assert.Contains("amount 12", line);
        Assert.Contains("buffs 1732,1461", line);
        Assert.Contains("gap 420 ms", line);
    }

    [Fact]
    public void KindNumbersAreStable()
    {
        Assert.Equal(22, (int)ObservationKind.LightGain);
        Assert.Equal(23, (int)ObservationKind.Notice);
    }
}

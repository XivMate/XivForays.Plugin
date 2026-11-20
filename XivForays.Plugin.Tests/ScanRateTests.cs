using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>Pins the scan rates of the 0.0.0.19 performance pass and proves the records still come out at those rates.</summary>
public class ScanRateTests
{
    private static readonly Guid Visit = Guid.Parse("4463f91b-5bd4-4d59-8830-261375699a11");
    private const long Start = 1793940000000;

    [Fact]
    public void RatesArePinned()
    {
        Assert.Equal(1000, StreamRates.SignalScanMs);
        Assert.Equal(5000, StreamRates.PortalScanMs);
        Assert.Equal(2000, StreamRates.PlayerContextReadMs);
        Assert.Equal(1000, StreamRates.FateScanMs);
        Assert.Equal(500, StreamRates.EnemyScanMs);
        // One missed portal scan must not count as the portal being gone.
        Assert.True(PortalTracker.GoneGraceMs > StreamRates.PortalScanMs);
        // The context only records every 5 s on a change, so a 2 s read never delays it past one more read.
        Assert.True(StreamRates.PlayerContextReadMs <= ContextRules.MinChangeMs / 2);
    }

    [Fact]
    public void ThrottleRunsFirstThenOncePerInterval()
    {
        long last = 0;
        var runs = new List<long>();
        for (long ms = 1_000; ms <= 30_000; ms += 1_000)
            if (Throttle.Due(ms, ref last, 5000))
                runs.Add(ms);
        Assert.Equal(new long[] { 1_000, 6_000, 11_000, 16_000, 21_000, 26_000 }, runs);
    }

    [Fact]
    public void ThrottleResetByZeroRunsAtOnce()
    {
        long last = 0;
        Assert.True(Throttle.Due(5_000, ref last, 5000));
        Assert.False(Throttle.Due(6_000, ref last, 5000));
        last = 0;
        Assert.True(Throttle.Due(6_000, ref last, 5000));
    }

    private static Observation At(long ms) => new()
    {
        ObservedAtMs = Start + ms, VisitId = Visit, TerritoryId = 827, MapId = 515, WeatherId = 7
    };

    /// <summary>A 1 s signal tick drives the portal sampler through the throttle, exactly as the module does.</summary>
    private static List<Observation> Drive(Func<long, bool> inTable, long seconds)
    {
        var tracker = new PortalTracker();
        var emitted = new List<Observation>();
        long last = 0;
        for (long s = 1; s <= seconds; s++)
        {
            var ms = s * 1000;
            if (!Throttle.Due(Start + ms, ref last, StreamRates.PortalScanMs))
                continue;
            var table = inTable(ms) ? new[] { new PortalSnapshot(1, PortalTracker.BlueBaseId, 100, 5, -200) } : [];
            tracker.Sample(At(ms), table, emitted.Add);
        }

        return emitted;
    }

    [Fact]
    public void PortalAtTheNewRateAppearsOnceAndGoesOnceWithItsLastSeenTime()
    {
        // Up from 12 s to 70 s, then gone for good.
        var emitted = Drive(ms => ms >= 12_000 && ms < 70_000, 200);
        var appear = Assert.Single(emitted, o => o.State == 1);
        var gone = Assert.Single(emitted, o => o.State == 0);
        Assert.Equal(appear.ObjectTrackId, gone.ObjectTrackId);
        Assert.True(appear.ObservedAtMs - (Start + 12_000) <= StreamRates.PortalScanMs);
        // The last scan that saw it was before 70 s; the gone record carries that time and the visible span.
        Assert.True(gone.ObservedAtMs < Start + 70_000 && gone.ObservedAtMs >= Start + 65_000 - 1_000);
        Assert.Equal((uint)(gone.ObservedAtMs - appear.ObservedAtMs), gone.VisibleMs);
    }

    [Fact]
    public void PortalMissedByOneScanIsStillOneAppearanceAndOneGone()
    {
        var emitted = Drive(ms => ms < 90_000 && !(ms is >= 26_000 and < 31_000), 200);
        Assert.Equal(1, emitted.Count(o => o.State == 1));
        Assert.Equal(1, emitted.Count(o => o.State == 0));
    }

    [Fact]
    public void ShortLivedPortalBetweenTwoScansIsNotRecordedAndNothingBreaks()
    {
        // Documented limit of the 5 s rate: a node that lives under one scan interval can be missed.
        var emitted = Drive(ms => ms is >= 7_000 and < 9_000, 60);
        Assert.True(emitted.Count <= 2);
    }

    [Fact]
    public void LightLinkingStaysOnTheOneSecondTickAndUsesNoPerformanceThrottle()
    {
        Assert.Equal(1000, StreamRates.SignalScanMs);
        var kills = new GainKills();
        // A kill sampled on a 1 s tick still links to a notice that arrives 2.5 s later.
        kills.Add(new GainKill(10_000, 7, false, false, 0, 1, Guid.NewGuid()));
        var link = kills.Find(12_500);
        Assert.NotNull(link);
        Assert.Equal(7u, link!.Value.Kill.GameId);

        var source = File.ReadAllText(SourcePath("Gathering", "ForaySignalModule.cs"));
        var start = source.IndexOf("private void SignalTick()", StringComparison.Ordinal);
        var end = source.IndexOf("private void SamplePortals(", StringComparison.Ordinal);
        var tick = source.Substring(start, end - start);
        Assert.Contains("Throttle.Due(now, ref lastPortalScan, StreamRates.PortalScanMs)", tick);
        Assert.Contains("Throttle.Due(now, ref lastContextRead, StreamRates.PlayerContextReadMs)", tick);
        Assert.Contains("\n            LightTick(context, player.Position);", tick);
        Assert.Contains("\n            GainTick(context, false);", tick);
    }

    [Fact]
    public void TreasureClassIsLookedUpOncePerKindOfObject()
    {
        var cache = new TreasureClassCache();
        var calls = 0;
        (byte, byte?) Classify() { calls++; return (1, 3); }
        for (var scan = 0; scan < 10; scan++)
            Assert.Equal(((byte)1, (byte?)3), cache.Get(2, 2012936, Classify));
        cache.Get(3, 2012936, Classify);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void PortalScanOutsideTheFieldMapDoesNothingWhenNothingIsTracked()
    {
        var tracker = new PortalTracker();
        Assert.True(tracker.IsIdle);
        tracker.Sample(At(0), [new PortalSnapshot(1, PortalTracker.BlueBaseId, 1, 2, 3)], _ => { });
        Assert.False(tracker.IsIdle);
    }

    private static string SourcePath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "XivForays.Plugin", "Gathering", "ForaySignalModule.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, "XivForays.Plugin", .. parts]);
    }
}

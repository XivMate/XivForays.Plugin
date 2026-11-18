using System.Text.Json;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>Kettle light (kind 20): row ids, the kill and FATE link, ambiguity, merging of notice and value.</summary>
public class KettleLightTests
{
    private static readonly Guid Visit = Guid.Parse("4463f91b-5bd4-4d59-8830-261375699a11");
    private static readonly Guid Other = Guid.Parse("5563f91b-5bd4-4d59-8830-261375699a11");
    private static readonly Guid TrackA = Guid.Parse("0f8e0000-0000-0000-0000-00000000000a");
    private static readonly Guid TrackB = Guid.Parse("0f8e0000-0000-0000-0000-00000000000b");
    private const long T0 = 1793940205000;
    private const uint Pagos = 763;
    private static KettleReading R(int bar) => new(5, bar);

    private static readonly LightSnapshot Snap = new(20, 4000, 5);

    private static LightKill Kill(long at, Guid? track = null, byte form = 0) =>
        new(at, track, 7213, 62, 35, form, 1, 12, 800, null);

    [Fact]
    public void RowsAreTheKettleNoticesAndGradesFollowTheRowOrder()
    {
        Assert.Equal<byte?>(1, KettleRows.Tier(9020));
        Assert.Equal<byte?>(3, KettleRows.Tier(9022));
        Assert.Equal<byte?>(7, KettleRows.Tier(9026));
        Assert.Null(KettleRows.Tier(9027));
        Assert.True(KettleRows.IsKettleRow(9027));
        Assert.True(KettleRows.IsKettleRow(3743));
        Assert.False(KettleRows.IsKettleRow(549));
        Assert.False(KettleRows.IsKettleRow(9019));
        Assert.True(KettleRows.InKettleZone(763));
        Assert.True(KettleRows.InKettleZone(795));
        Assert.False(KettleRows.InKettleZone(732));
        Assert.False(KettleRows.InKettleZone(827));
    }

    [Fact]
    public void OneKillInTheWindowIsLinkedWithItsFields()
    {
        var r = LightLinker.Link(T0 + 900, [Kill(T0, TrackA, form: 1)], []);
        Assert.Equal(LightLinkKind.Kill, r.Kind);
        Assert.Equal(1, r.Candidates);
        Assert.Equal(TrackA, r.Kill!.Value.TrackId);
        Assert.Equal<uint?>(900, r.AgeMs);
    }

    [Fact]
    public void NoCandidateIsNone_AndOldOrFutureKillsAreIgnored()
    {
        Assert.Equal(LightLinkKind.None, LightLinker.Link(T0, [], []).Kind);
        Assert.Equal(LightLinkKind.None, LightLinker.Link(T0 + 3600, [Kill(T0)], []).Kind);
        Assert.Equal(LightLinkKind.None, LightLinker.Link(T0, [Kill(T0 + 100)], []).Kind);
        Assert.Equal(LightLinkKind.Kill, LightLinker.Link(T0 + 3500, [Kill(T0)], []).Kind);
    }

    [Fact]
    public void SeveralKillsAreAmbiguousNotGuessed()
    {
        var r = LightLinker.Link(T0 + 1000, [Kill(T0, TrackA), Kill(T0 + 400, TrackB)], []);
        Assert.Equal(LightLinkKind.Ambiguous, r.Kind);
        Assert.Equal(2, r.Candidates);
        Assert.Null(r.Kill);
        Assert.Equal([TrackB, TrackA], r.CandidateTracks);
    }

    [Fact]
    public void OneFateCompletionAloneIsLinkedAsAFate()
    {
        var r = LightLinker.Link(T0 + 500, [], [new LightFate(T0, 1367, 1)]);
        Assert.Equal(LightLinkKind.Fate, r.Kind);
        Assert.Equal<uint>(1367, r.Fate!.Value.FateId);
    }

    [Fact]
    public void AKillAndAFateCompletionTogetherAreAmbiguous()
    {
        var r = LightLinker.Link(T0 + 500, [Kill(T0, TrackA)], [new LightFate(T0, 1367, 1)]);
        Assert.Equal(LightLinkKind.Ambiguous, r.Kind);
        Assert.Equal(2, r.Candidates);
    }

    [Fact]
    public void NoticeWithoutAReadableValueStillGivesAnEventAfterTheHold()
    {
        var t = new LightTracker();
        t.NoteKill(Visit, Pagos, Kill(T0, TrackA));
        t.Notice(T0 + 300, Visit, Pagos, 9022, Snap);
        Assert.Empty(t.Tick(T0 + 300 + LightTracker.HoldMs - 1));
        var e = Assert.Single(t.Tick(T0 + 300 + LightTracker.HoldMs));
        Assert.Equal<byte?>(3, e.Tier);
        Assert.Equal<byte>(1, e.Source);
        Assert.Null(e.Delta);
        Assert.Equal(LightLinkKind.Kill, e.Link.Kind);
    }

    [Fact]
    public void KillNotedAfterTheNoticeButInsideTheHoldIsStillLinked()
    {
        // The defeat notice resolves its enemy late (up to 1.5 s) and is noted with its own, earlier time.
        var t = new LightTracker();
        t.Notice(T0 + 300, Visit, Pagos, 9020, Snap);
        t.NoteKill(Visit, Pagos, Kill(T0, TrackA));
        var e = Assert.Single(t.Tick(T0 + 2000));
        Assert.Equal(LightLinkKind.Kill, e.Link.Kind);
    }

    [Fact]
    public void ValueChangeAndNoticeMergeIntoOneEventWithBeforeAfterAndDelta()
    {
        var t = new LightTracker();
        t.Value(T0 - 5000, Visit, Pagos, R(40), Snap);
        t.NoteKill(Visit, Pagos, Kill(T0, TrackA));
        t.Notice(T0 + 200, Visit, Pagos, 9021, Snap);
        t.Value(T0 + 300, Visit, Pagos, R(52), Snap);
        var e = Assert.Single(t.Tick(T0 + 5000));
        Assert.Equal<KettleReading?>(R(40), e.Before);
        Assert.Equal<KettleReading?>(R(52), e.After);
        Assert.Equal<int?>(12, e.Delta);
        Assert.Equal<byte>(3, e.Source);
        Assert.Equal<byte?>(2, e.Tier);
    }

    [Fact]
    public void ValueChangeFirstThenNoticeAlsoMerges()
    {
        var t = new LightTracker();
        t.Value(T0 - 1000, Visit, Pagos, R(10), Snap);
        t.Value(T0, Visit, Pagos, R(15), Snap);
        t.Notice(T0 + 400, Visit, Pagos, 9022, Snap);
        var e = Assert.Single(t.Tick(T0 + 5000));
        Assert.Equal<int?>(5, e.Delta);
        Assert.Equal<uint?>(9022, e.Row);
    }

    [Fact]
    public void ValueChangeWithoutANoticeIsAnEventOfItsOwn_AndTheFirstValueIsOnlyABaseline()
    {
        var t = new LightTracker();
        t.Value(T0, Visit, Pagos, R(7), Snap);
        Assert.Equal(0, t.PendingCount);
        t.Value(T0 + 1000, Visit, Pagos, R(7), Snap);
        Assert.Equal(0, t.PendingCount);
        t.Value(T0 + 2000, Visit, Pagos, R(9), Snap);
        var e = Assert.Single(t.Tick(T0 + 9000));
        Assert.Null(e.Row);
        Assert.Equal<byte>(2, e.Source);
        Assert.Equal<int?>(2, e.Delta);
    }

    [Fact]
    public void TwoQuickNoticesAreTwoEvents()
    {
        var t = new LightTracker();
        t.Notice(T0, Visit, Pagos, 9020, Snap);
        t.Notice(T0 + 200, Visit, Pagos, 9020, Snap);
        Assert.Equal(2, t.Tick(T0 + 5000).Count);
    }

    [Fact]
    public void FullAndForgeNoticesAreMarked_AndTheRefusalIsNotAnEvent()
    {
        var t = new LightTracker();
        t.Notice(T0, Visit, Pagos, KettleRows.Full, Snap);
        t.Notice(T0 + 5000, Visit, Pagos, KettleRows.ForgeConverted, Snap);
        t.Notice(T0 + 9000, Visit, Pagos, KettleRows.ForgeRefused, Snap);
        var events = t.Tick(T0 + 20000);
        Assert.Equal(2, events.Count);
        Assert.True(events[0].Full);
        Assert.Null(events[0].Tier);
        Assert.True(events[1].Forge);
    }

    [Fact]
    public void ForgeKeepsTheValueBeforeWhenItIsReadable()
    {
        var t = new LightTracker();
        t.Value(T0, Visit, Pagos, R(80), Snap);
        t.Notice(T0 + 3000, Visit, Pagos, KettleRows.ForgeConverted, Snap);
        t.Value(T0 + 3100, Visit, Pagos, R(0), Snap);
        var e = Assert.Single(t.Tick(T0 + 9000));
        Assert.True(e.Forge);
        Assert.Equal<KettleReading?>(R(80), e.Before);
        Assert.Equal<KettleReading?>(R(0), e.After);
        Assert.Equal<int?>(-80, e.Delta);
    }

    [Fact]
    public void ForgeDropWithoutANoticeIsMarkedAsConversion()
    {
        var t = new LightTracker();
        t.Value(T0, Visit, Pagos, new KettleReading(5, 90), Snap);
        t.Value(T0 + 1000, Visit, Pagos, new KettleReading(4, 3), Snap);
        var e = Assert.Single(t.Tick(T0 + 9000));
        Assert.True(e.Forge);
        Assert.Null(e.Row);
        Assert.Null(e.Delta); // yield changed and the cap is unknown
    }

    [Fact]
    public void NoticeWithNoChangeAfterItTakesTheLatestSampleAsAfter()
    {
        var t = new LightTracker();
        t.Value(T0 - 1000, Visit, Pagos, R(60), Snap);
        t.Notice(T0, Visit, Pagos, KettleRows.Full, Snap);
        var e = Assert.Single(t.Tick(T0 + 5000));
        Assert.Equal<KettleReading?>(R(60), e.Before);
        Assert.Equal<KettleReading?>(R(60), e.After);
        Assert.Equal<int?>(0, e.Delta);
    }

    [Fact]
    public void AVisitChangeForgetsKillsAndTheBaseline()
    {
        var t = new LightTracker();
        t.Value(T0, Visit, Pagos, R(5), Snap);
        t.NoteKill(Visit, Pagos, Kill(T0, TrackA));
        t.Value(T0 + 100, Other, Pagos, R(99), Snap);
        Assert.Equal(0, t.PendingCount);
        t.Notice(T0 + 200, Other, Pagos, 9020, Snap);
        var e = Assert.Single(t.Tick(T0 + 5000));
        Assert.Equal(LightLinkKind.None, e.Link.Kind);
    }

    [Fact]
    public void FlushFinishesPendingEventsAtOnce()
    {
        var t = new LightTracker();
        t.Notice(T0, Visit, Pagos, 9022, Snap);
        Assert.Single(t.Flush());
        Assert.Equal(0, t.PendingCount);
    }

    [Fact]
    public void FateCompletionWatchReportsEachCompletionOnceWithCredit()
    {
        var watch = new FateCompletionWatch();
        FateSnapshot Fate(byte state, byte progress) => new(1367, state, progress, 100, 10, 0, 10, 30);
        Assert.Empty(watch.Observe(Visit, [Fate(FateLifecycle.Running, 50)], T0, 10, 10));
        var done = Assert.Single(watch.Observe(Visit, [Fate(FateLifecycle.Running, 100)], T0 + 1000, 12, 12));
        Assert.Equal<uint>(1367, done.FateId);
        Assert.Equal<byte>(1, done.Credit);
        Assert.Empty(watch.Observe(Visit, [Fate(FateLifecycle.Ended, 100)], T0 + 2000, 12, 12));
        var outside = Assert.Single(new FateCompletionWatch().Observe(Visit, [Fate(FateLifecycle.Ended, 100)], T0, 200, 200));
        Assert.Equal<byte>(0, outside.Credit);
    }

    [Fact]
    public void KillRecordCarriesTheKindTwentyFieldsAsNumbersOnly()
    {
        var kill = new LightKill(T0, TrackA, 7213, 62, 35, 1, 2, 12, 800, null);
        var link = LightLinker.Link(T0 + 700, [kill], []);
        var e = new LightEvent(T0 + 700, Visit, Pagos, 9022, 3, false, false, R(40), R(52), Snap, link);
        var o = e.ToObservation(new Observation { MapId = 415, WeatherId = 7 });
        Assert.Equal(ObservationKind.KettleLight, o.Kind);
        Assert.Equal(20, (int)o.Kind);
        Assert.Equal<uint?>(7213, o.GameId);
        Assert.Equal<byte?>(1, o.Form);
        Assert.True(o.IsMutated);
        Assert.False(o.IsAdapted);
        Assert.Equal<byte?>(2, o.DefeatBy);
        Assert.Equal<uint?>(12, o.ChainNumber);
        Assert.Equal<byte?>(5, o.GroupSize);
        Assert.Equal<byte?>(1, o.LightLink);
        Assert.Equal<int?>(12, o.LightDelta);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(o, ObservationJson.Options));
        var root = doc.RootElement;
        Assert.Equal(20, root.GetProperty("kind").GetInt32());
        Assert.Equal(9022, root.GetProperty("noticeRow").GetInt32());
        Assert.Equal(40, root.GetProperty("lightBarBefore").GetInt32());
        Assert.Equal(52, root.GetProperty("lightBarAfter").GetInt32());
        Assert.Equal(5, root.GetProperty("lightYieldBefore").GetInt32());
        Assert.Equal(5, root.GetProperty("lightYieldAfter").GetInt32());
        Assert.Equal(12, root.GetProperty("lightDelta").GetInt32());
        Assert.False(root.TryGetProperty("lightCap", out _));
        Assert.False(root.TryGetProperty("fateRewardId", out _));
        foreach (var p in root.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String)
                Assert.True(p.Name is "visitId" or "defeatTrackId", $"unexpected string field {p.Name}");
    }

    [Fact]
    public void FateRecordCarriesTheFateIdAndCredit_AndAmbiguousCarriesNoCause()
    {
        var fateLink = LightLinker.Link(T0 + 100, [], [new LightFate(T0, 1367, 1)]);
        var fate = new LightEvent(T0 + 100, Visit, Pagos, 9022, 3, false, false, null, null, Snap, fateLink)
            .ToObservation(new Observation());
        Assert.Equal<uint?>(1367, fate.FateRewardId);
        Assert.Equal<byte?>(1, fate.LightFateCredit);
        Assert.Null(fate.GameId);

        var amb = LightLinker.Link(T0 + 100, [Kill(T0, TrackA), Kill(T0 + 50, TrackB)], []);
        var o = new LightEvent(T0 + 100, Visit, Pagos, 9020, 1, false, false, null, null, Snap, amb)
            .ToObservation(new Observation());
        Assert.Equal<byte?>(3, o.LightLink);
        Assert.Equal<byte?>(2, o.LightCandidates);
        Assert.Null(o.GameId);
        Assert.Null(o.DefeatTrackId);
        Assert.Equal(2, o.LightCandidateTracks!.Length);
    }
}

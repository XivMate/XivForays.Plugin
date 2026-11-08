using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>Pose capture v2 rules (docs/CAPTURE_DESIGN.md §3): settle, dedupe, form debounce, budget, links, respawn.</summary>
public class PoseCaptureTests
{
    private static readonly Vector3 Here = new(-123.456f, 12.345f, 456.789f);

    // ---- settle ----

    [Fact]
    public void SettlesOnlyAfterSeenForOneAndAHalfSecondsAndStillForOne()
    {
        var settle = new SettleState(firstSeenMs: 0);
        settle.Update(0, Here, 1f, calm: true);
        Assert.False(settle.IsSettled(0, true, true, false));
        settle.Update(500, Here, 1f, calm: true);
        settle.Update(1000, Here, 1f, calm: true);
        Assert.True(settle.IsStill(1000));
        Assert.False(settle.IsSettled(1000, true, true, false)); // seen only 1.0 s
        settle.Update(1500, Here, 1f, calm: true);
        Assert.True(settle.IsSettled(1500, true, true, false));
    }

    [Fact]
    public void MovingOrTurningResetsTheStillTimer()
    {
        var settle = new SettleState(0);
        settle.Update(0, Here, 1f, true);
        settle.Update(500, Here, 1f, true);
        settle.Update(1000, Here + new Vector3(0.06f, 0, 0), 1f, true); // moved 0.06 y
        Assert.True(settle.Moving);
        settle.Update(1500, Here + new Vector3(0.06f, 0, 0), 1f, true);
        Assert.False(settle.IsSettled(1500, true, true, false));
        settle.Update(2000, Here + new Vector3(0.06f, 0, 0), 1f + 2 * MathF.PI / 180, true); // turned 2 degrees
        settle.Update(2500, Here + new Vector3(0.06f, 0, 0), 1f + 2 * MathF.PI / 180, true);
        Assert.False(settle.IsSettled(2500, true, true, false));
        settle.Update(3000, Here + new Vector3(0.06f, 0, 0), 1f + 2 * MathF.PI / 180, true);
        Assert.True(settle.IsSettled(3000, true, true, false));
    }

    [Fact]
    public void TinyJitterBelowTheThresholdsStillSettles()
    {
        var settle = new SettleState(0);
        for (var t = 0; t <= 2000; t += 500)
            settle.Update(t, Here + new Vector3(t % 1000 == 0 ? 0.01f : 0, 0, 0), 1f + (t % 1000 == 0 ? 0.005f : 0), true);
        Assert.True(settle.IsSettled(2000, true, true, false));
    }

    [Fact]
    public void CombatDrawObjectAndPendingFormChangeBlockTheSettle()
    {
        var settle = new SettleState(0);
        for (var t = 0; t <= 2000; t += 500)
            settle.Update(t, Here, 1f, calm: t < 1500);
        Assert.False(settle.IsSettled(2000, calm: false, true, false));
        Assert.False(settle.IsSettled(2000, calm: true, true, false)); // combat at 1500/2000 reset the still timer

        var other = new SettleState(0);
        for (var t = 0; t <= 2000; t += 500)
            other.Update(t, Here, 1f, true);
        Assert.False(other.IsSettled(2000, true, drawReady: false, formPending: false));
        Assert.False(other.IsSettled(2000, true, drawReady: true, formPending: true));
        Assert.True(other.IsSettled(2000, true, true, false));
    }

    // ---- spawn pose decisions and dedupe ----

    [Fact]
    public void OnePosePerTrackPlusAtMostOneNearRetake()
    {
        var near = new SpawnPoseState();
        Assert.Equal(PoseTake.None, near.Decide(settled: false, 50));
        Assert.Equal(PoseTake.First, near.Decide(true, 50));
        near.Taken(PoseTake.First, 50);
        Assert.Equal(PoseTake.None, near.Decide(true, 50)); // near first pose: never retaken

        var far = new SpawnPoseState();
        far.Taken(far.Decide(true, 150), 150);
        Assert.True(far.FirstWasFar);
        Assert.Equal(PoseTake.None, far.Decide(true, 130)); // still far
        Assert.Equal(PoseTake.NearRetake, far.Decide(true, 100));
        far.Taken(PoseTake.NearRetake, 100);
        Assert.Equal(PoseTake.None, far.Decide(true, 50));
    }

    [Fact]
    public void ContentDedupeDropsTheSameIdleMobReEnteringView()
    {
        var dedupe = new PoseDedupe();
        var first = PoseKey.Of(7777, 1234, 0, -41.63f, 471.18f, 0.10f, true);
        Assert.True(dedupe.TryAdd(first));
        // A new track of the same mob, a hair away and turned under 1.5 degrees: same key.
        Assert.False(dedupe.TryAdd(PoseKey.Of(7777, 1234, 0, -41.70f, 471.10f, 0.10f + 0.01f, true)));
        // Collision loaded differs (a near retake): new key.
        Assert.True(dedupe.TryAdd(PoseKey.Of(7777, 1234, 0, -41.63f, 471.18f, 0.10f, false)));
        // Another form, model or point: new keys.
        Assert.True(dedupe.TryAdd(PoseKey.Of(7777, 1234, 1, -41.63f, 471.18f, 0.10f, true)));
        Assert.True(dedupe.TryAdd(PoseKey.Of(7777, 1235, 0, -41.63f, 471.18f, 0.10f, true)));
        Assert.True(dedupe.TryAdd(PoseKey.Of(7777, 1234, 0, -40.63f, 471.18f, 0.10f, true)));
        dedupe.Clear();
        Assert.True(dedupe.TryAdd(first));
    }

    [Fact]
    public void HeadingKeyWrapsAtFullCircle()
    {
        Assert.Equal(PoseKey.Of(1, 1, 0, 0, 0, 0f, true), PoseKey.Of(1, 1, 0, 0, 0, 2 * MathF.PI - 0.001f, true));
        Assert.Equal(PoseKey.Of(1, 1, 0, 0, 0, -0.001f, true), PoseKey.Of(1, 1, 0, 0, 0, 0f, true));
    }

    [Fact]
    public void DedupeIsBounded()
    {
        var dedupe = new PoseDedupe(max: 3);
        for (var i = 0; i < 3; i++)
            Assert.True(dedupe.TryAdd(PoseKey.Of(1, 1, 0, i, 0, 0, true)));
        Assert.True(dedupe.TryAdd(PoseKey.Of(1, 1, 0, 99, 0, 0, true)));
        Assert.Equal(1, dedupe.Count);
    }

    // ---- form change debounce ----

    private static FormKey Key(int model = 100, float scale = 1f, float drawScale = 1f, bool mutated = false, nint skeleton = 0x1000) =>
        new(model, scale, drawScale, 55, 7777, mutated, false, skeleton);

    [Fact]
    public void FormChangeIsDueOneAndAHalfSecondsAfterTheLastChange()
    {
        var watch = new FormWatch();
        Assert.False(watch.Update(0, Key()));
        Assert.True(watch.Update(500, Key(mutated: true))); // change starts
        Assert.False(watch.Update(1000, Key(mutated: true, scale: 1.2f))); // keeps changing
        Assert.False(watch.AfterDue(2000));
        watch.Update(1500, Key(mutated: true, scale: 1.2f));
        watch.Update(2000, Key(mutated: true, scale: 1.2f));
        Assert.False(watch.AfterDue(2000));
        Assert.True(watch.AfterDue(2500)); // 1.5 s after the last change (1000)
        var bits = watch.Complete(Key(mutated: true, scale: 1.2f));
        Assert.Equal(FormKey.MutatedBit | FormKey.ScaleBit, bits);
        Assert.False(watch.Pending);
    }

    [Fact]
    public void FormChangeIsDueAtMostFiveSecondsAfterTheFirstChange()
    {
        var watch = new FormWatch();
        watch.Update(0, Key());
        watch.Update(500, Key(scale: 1.01f));
        for (var t = 1000; t < 5500; t += 500)
        {
            watch.Update(t, Key(scale: 1.01f + t / 100000f)); // a scale that keeps growing
            Assert.Equal(t >= 5500, watch.AfterDue(t));
        }

        Assert.True(watch.AfterDue(5500));
    }

    [Fact]
    public void RevertedFormChangeHasNoBitsAndUnknownPartsAreNotChanges()
    {
        var watch = new FormWatch();
        watch.Update(0, Key());
        watch.Update(500, Key(model: 101));
        watch.Update(1000, Key(model: 100));
        Assert.True(watch.AfterDue(2500));
        Assert.Equal(0, watch.Complete(Key()));

        var loading = new FormWatch();
        loading.Update(0, Key(drawScale: float.NaN, skeleton: 0)); // draw object not loaded yet
        Assert.False(loading.Update(500, Key()));
        Assert.False(loading.Pending);
        Assert.False(loading.Update(1000, Key(drawScale: float.NaN, skeleton: 0))); // unloaded again
        Assert.False(loading.Pending);
    }

    [Fact]
    public void BnpcChangeStartsAFormChangeFromOutside()
    {
        var watch = new FormWatch();
        var before = Key();
        var after = before with { BnpcId = 7778, ModelCharaId = 200 };
        watch.Begin(1000, before, after);
        Assert.True(watch.Pending);
        Assert.True(watch.AfterDue(2500));
        Assert.Equal(FormKey.BnpcBit | FormKey.ModelBit, watch.Complete(after));
    }

    // ---- budget ----

    [Fact]
    public void BudgetTakesAtMostSixReadsFormAfterFirst()
    {
        var candidates = new List<(int, long, string)>();
        for (var i = 0; i < 10; i++)
            candidates.Add((ReadBudget.Refresh, i, "refresh" + i));
        for (var i = 0; i < 4; i++)
            candidates.Add((ReadBudget.Spawn, 100 + i, "spawn" + i));
        candidates.Add((ReadBudget.FormAfter, 999, "after"));
        var picked = ReadBudget.Pick(candidates);
        Assert.Equal(PoseRates.MaxFullReadsPerScan, picked.Count);
        Assert.Equal(new[] { "after", "spawn0", "spawn1", "spawn2", "spawn3", "refresh0" }, picked);
    }

    // ---- categories ----

    [Fact]
    public void CategoriesNeverExposeIdsAndCoverEveryCase()
    {
        var c = new CategoryResolver();
        c.Add(0x10000001, ObjectClass.Player, 0, false); // self
        c.Add(0x10000002, ObjectClass.Player, 0, true); // party member (status flag)
        c.Add(0x10000003, ObjectClass.Player, 0, false); // other player
        c.Add(0x40000010, ObjectClass.Owned, 0x10000001, false); // own pet
        c.Add(0x40000011, ObjectClass.Owned, 0x10000003, false); // other player's pet
        c.Add(0x40000012, ObjectClass.Owned, 0x40000099, false); // an enemy's add
        c.Add(0x40000013, ObjectClass.Npc, 0, false);
        c.AddPartyMember(0x10000004); // party member out of view
        c.SetLocal(0x10000001);

        Assert.Equal(ObjectCategory.None, c.OfEntityId(0));
        Assert.Equal(ObjectCategory.None, c.OfEntityId(0xE0000000));
        Assert.Equal(ObjectCategory.None, c.OfObjectId(0xE0000000));
        Assert.Equal(ObjectCategory.Self, c.OfObjectId(0x10000001));
        Assert.Equal(ObjectCategory.Party, c.OfEntityId(0x10000002));
        Assert.Equal(ObjectCategory.OtherPlayer, c.OfEntityId(0x10000003));
        Assert.Equal(ObjectCategory.Party, c.OfEntityId(0x10000004));
        Assert.Equal(ObjectCategory.OwnPet, c.OfEntityId(0x40000010));
        Assert.Equal(ObjectCategory.OtherPet, c.OfEntityId(0x40000011));
        Assert.Equal(ObjectCategory.Npc, c.OfEntityId(0x40000012));
        Assert.Equal(ObjectCategory.Npc, c.OfEntityId(0x40000013));
        Assert.Equal(ObjectCategory.OtherPlayer, c.OfEntityId(0x10000077)); // a player out of view
        Assert.Equal(ObjectCategory.Unknown, c.OfEntityId(0x40000077));
        Assert.True(Enum.GetValues<ObjectCategory>().All(v => (byte)v <= 7));
    }

    // ---- present at start / seen spawning ----

    [Fact]
    public void PresentAtStartAndSeenSpawningHeuristics()
    {
        Assert.True(PlayerHistory.PresentAtStart(4_000, 0));
        Assert.False(PlayerHistory.PresentAtStart(6_000, 0));

        var history = new PlayerHistory();
        for (var t = 0; t <= 6000; t += 500)
            history.Add(t, new Vector3(0, 0, 0));
        Assert.True(history.StoodWithin(new Vector3(60, 0, 0), 80, 6000));
        Assert.False(history.StoodWithin(new Vector3(90, 0, 0), 80, 6000));
        Assert.True(PlayerHistory.SeenSpawning(false, 60, true));
        Assert.False(PlayerHistory.SeenSpawning(true, 60, true));
        Assert.False(PlayerHistory.SeenSpawning(false, 85, true));

        var fresh = new PlayerHistory();
        fresh.Add(0, Vector3.Zero);
        fresh.Add(500, Vector3.Zero);
        Assert.False(fresh.StoodWithin(Vector3.Zero, 80, 500)); // under 5 s of history
        fresh.Add(1000, new Vector3(5, 0, 0));
        Assert.Equal(10f, fresh.Speed(), 3);
    }

    // ---- idle track ----

    [Fact]
    public void IdleTrackKeepsFirstLastAndMovedSamplesForSixtySeconds()
    {
        var idle = new IdleTrackBuilder(0, Here, 1f, timeline: 3, baseOverride: 0);
        for (var t = 500; t <= 60_000; t += 500)
        {
            var moved = t >= 20_000 && t <= 24_000 ? new Vector3((t - 20_000) / 1000f, 0, 0) : t > 24_000 ? new Vector3(4, 0, 0) : Vector3.Zero;
            idle.Step(t, Here + moved, 1f, t >= 30_000 ? 34 : 3, 0);
        }

        Assert.True(idle.Complete(60_000));
        var track = idle.Finish(0);
        Assert.Equal(1, track.PoseVersion);
        Assert.Equal(2000, track.StepMs);
        Assert.Equal(0, track.Samples[0][0]);
        Assert.Equal(60_000, track.Samples[^1][0]); // last
        Assert.InRange(track.Samples.Length, 4, 6); // first, moves at 22/24 s, last
        Assert.Equal(new[] { new long[] { 0, 3, 0 }, new long[] { 30_000, 34, 0 } }, track.Timelines);
        Assert.True(track.Samples.Length <= PoseRates.IdleMaxSamples);
    }

    // ---- aggro links ----

    private static Observation AggroRecord(uint bnpc, Guid track) => new()
    {
        Kind = ObservationKind.EnemyAggro,
        GameId = bnpc,
        EnemyTrackId = track,
        Aggro = new EnemyAggro { TargetCategory = 1 }
    };

    [Fact]
    public void AggroEdgesAreHeldAndLinkedWithinOneAndAHalfSecondsAndFortyYalms()
    {
        var correlator = new AggroCorrelator();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), far = Guid.NewGuid(), late = Guid.NewGuid();
        correlator.Add(1000, 7777, a, Vector3.Zero, 1, AggroRecord(7777, a));
        correlator.Add(1500, 7777, b, new Vector3(10, 0, 0), 2, AggroRecord(7777, b));
        correlator.Add(1600, 8888, far, new Vector3(60, 0, 0), 1, AggroRecord(8888, far));
        Assert.Empty(correlator.Flush(2000));
        correlator.Add(3000, 7777, late, new Vector3(5, 0, 0), 1, AggroRecord(7777, late));

        var first = correlator.Flush(2600);
        Assert.Single(first);
        var links = first[0].Aggro!.Linked;
        Assert.Single(links);
        Assert.Equal(b, links[0].EnemyTrackId);
        Assert.Equal(500, links[0].DtMs);
        Assert.Equal(10f, links[0].Distance);
        Assert.Equal(2, links[0].TargetCategory);

        var second = correlator.Flush(4600);
        Assert.Equal(3, second.Count);
        var bLinks = second.Single(o => o.EnemyTrackId == b).Aggro!.Linked;
        Assert.Equal(new[] { a, late }.Order(), bLinks.Select(l => l.EnemyTrackId).Order());
        Assert.Empty(second.Single(o => o.EnemyTrackId == far).Aggro!.Linked);
    }

    // ---- death -> respawn ----

    [Fact]
    public void RespawnMatchesTheSameSpawnPointByLayoutOrAnchor()
    {
        var matcher = new RespawnMatcher();
        var dead = Guid.NewGuid();
        matcher.Died(dead, 7777, 4012345, Here, 0, 10_000, Here + new Vector3(30, 0, 0));
        matcher.Tick(20_000, Here + new Vector3(70, 0, 0));
        Assert.Null(matcher.Appeared(8888, 4012345, Here, 0, 60_000)); // another bnpc
        Assert.Null(matcher.Appeared(7777, 999, Here + new Vector3(5, 0, 0), 0, 60_000)); // another point
        var match = matcher.Appeared(7777, 4012345, Here + new Vector3(0.5f, 0, 0), 1, 70_000);
        Assert.NotNull(match);
        Assert.Equal(dead, match.PreviousTrackId);
        Assert.Equal(60_000, match.IntervalMs);
        Assert.Equal(3, match.MatchedBy);
        Assert.False(match.SameForm);
        Assert.Equal(70f, match.MaxPlayerDistance);
        Assert.Null(matcher.Appeared(7777, 4012345, Here, 0, 80_000)); // consumed once

        matcher.Died(Guid.NewGuid(), 7777, 0, Here, 0, 100_000, Here);
        Assert.Equal(2, matcher.Appeared(7777, 0, Here + new Vector3(1.4f, 0, 0), 0, 150_000)!.MatchedBy);
        matcher.Died(Guid.NewGuid(), 7777, 555, Here, 0, 200_000, Here);
        Assert.Equal(1, matcher.Appeared(7777, 555, Here + new Vector3(10, 0, 0), 0, 250_000)!.MatchedBy);
    }

    [Fact]
    public void RespawnDeathsExpireAfterThirtyMinutes()
    {
        var matcher = new RespawnMatcher();
        matcher.Died(Guid.NewGuid(), 7777, 1, Here, 0, 0, Here);
        matcher.Tick(PoseRates.RespawnMaxWaitMs + 1, Here);
        Assert.Equal(0, matcher.PendingCount);
    }

    // ---- math, ported from Imaging ----

    [Fact]
    public void SkeletonRootAnglesMatchImagingGroundTilt()
    {
        // Yaw 70 degrees, then pitch -10 about the actor's own right axis (Imaging's FacingPitch composition).
        var q = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 70 * MathF.PI / 180) *
                                     Quaternion.CreateFromAxisAngle(Vector3.UnitX, -10 * MathF.PI / 180));
        var (pitch, yaw, roll) = PoseMath.Degrees(q);
        Assert.Equal(-10f, pitch, 2);
        Assert.Equal(70f, yaw, 2);
        Assert.Equal(0f, roll, 2);
        Assert.Equal(10f, PoseMath.TiltDegrees(q), 2);
        Assert.Equal(0f, PoseMath.TiltDegrees(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.2f)), 2);
    }

    [Fact]
    public void CustomizeHashIsTheFirstFortyEightBitsOfSha1()
    {
        var customize = Enumerable.Range(0, 26).Select(i => (byte)(i * 7)).ToArray();
        var hex = Convert.ToHexString(SHA1.HashData(customize))[..12]; // Imaging's customizeHash
        Assert.Equal(Convert.ToInt64(hex, 16), PoseMath.CustomizeHash(customize));
        Assert.InRange(PoseMath.CustomizeHash(customize), 0, (1L << 48) - 1);
    }

    [Theory]
    [InlineData("chara/monster/m0123/skeleton/base/b0001/skl_m0123b0001.sklb", 'm', 123, 1)]
    [InlineData("chara/demihuman/d1004/skeleton/base/b0001/skl_d1004b0001.sklb", 'd', 1004, 1)]
    [InlineData("chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb", 'c', 101, 1)]
    public void SkeletonPathsBecomeNumbers(string path, char kind, int id, int baseId) =>
        Assert.Equal(new[] { (int)kind, id, baseId }, PoseMath.ParseSkeleton(Encoding.UTF8.GetBytes(path)));

    [Theory]
    [InlineData("")]
    [InlineData("chara/monster/m0123/obj/body/b0001/model/m0123b0001.mdl")]
    [InlineData("skl_x0123b0001.sklb")]
    [InlineData("skl_m01")]
    public void OtherPathsAreNotSkeletons(string path) => Assert.Null(PoseMath.ParseSkeleton(Encoding.UTF8.GetBytes(path)));

    [Fact]
    public void FacingAnglesUseTheGameHeadingConvention()
    {
        // Heading 0 faces +Z.
        Assert.Equal(0f, PoseMath.FacingToDeg(Vector3.Zero, 0, new Vector3(0, 0, 10)), 3);
        Assert.Equal(180f, MathF.Abs(PoseMath.FacingToDeg(Vector3.Zero, 0, new Vector3(0, 0, -10))), 3);
        Assert.Equal(90f, MathF.Abs(PoseMath.FacingToDeg(Vector3.Zero, 0, new Vector3(10, 0, 0))), 3);
        Assert.Equal(0f, PoseMath.FacingToDeg(Vector3.Zero, MathF.PI / 2, new Vector3(10, 0, 0)), 3);
        Assert.Equal(0f, PoseMath.HeadingDeltaDeg(0.1f, 0.1f + 2 * MathF.PI), 2);
    }

    [Fact]
    public void NonFinitePosesAreRejected()
    {
        var pose = SamplePose();
        Assert.True(PoseMath.IsFinite(pose));
        Assert.False(PoseMath.IsFinite(pose with { DrawPosition = [1, float.NaN, 3] }));
        Assert.False(PoseMath.IsFinite(pose with { SkeletonTilt = float.PositiveInfinity }));
        Assert.False(PoseMath.IsFinite(pose with { Timelines = [[0, 3, float.NaN]] }));
        Assert.False(PoseMath.IsFinite(new EnemyFormChange { After = pose, DrawHeightDelta = float.NaN }));
        Assert.False(PoseMath.IsFinite(new EnemyAggro { Dy = float.NaN }));
    }

    // ---- wire format and privacy of serialized records ----

    private static EnemyPose SamplePose() => new()
    {
        BnpcId = 7777, NameId = 1234, LayoutId = 4012345, FirstSeenMs = 1793940203100, SeenSpawning = true,
        PlayerDistance = 43.2f, CollisionLoaded = true, HitboxRadius = 1.5f, Position = [-123.456f, 12.345f, 456.789f],
        Rotation = 1.2345f, Scale = 1.2f, DrawPosition = [-123.456f, 11.987f, 456.789f], DrawRotation = [0, 0.5801f, 0, 0.8146f],
        DrawScale = [1.2f, 1.2f, 1.2f], SkeletonPosition = [-123.456f, 11.987f, 456.789f],
        SkeletonRotation = [0.0712f, 0.5788f, -0.0123f, 0.8121f], SkeletonPitch = -9.87f, SkeletonYaw = 70.9f,
        SkeletonRoll = 0.43f, SkeletonTilt = 9.9f, ModelCharaId = 1234, Skeleton = [109, 123, 1], CustomizeHash = 123456789012345,
        BaseTimeline = 3, OverallSpeed = 1, Timelines = [[0, 3, 1]], AnimTime = [1.234f, 2.4f], HomePosition = [-123.4f, 12.3f, 456.7f],
        HomeRotation = 1.2f, FateId = 1367, Hp = 100, MaxHp = 100, Claim = (byte)ObjectCategory.Self, Statuses = [[1234, 1, 300, 6]]
    };

    private static IEnumerable<Observation> SampleRecords()
    {
        var context = new Observation
        {
            ObservedAtMs = 1793940205123, VisitId = Guid.NewGuid(), TerritoryId = 732, MapId = 414, WeatherId = 3
        };
        var track = Guid.NewGuid();
        var pose = SamplePose();
        yield return context with { Kind = ObservationKind.EnemySpawnPose, GameId = 7777, EnemyTrackId = track, LayoutId = 4012345, Pose = pose };
        yield return context with
        {
            Kind = ObservationKind.EnemyFormChange, GameId = 7777, EnemyTrackId = track, BeforeTrackId = Guid.NewGuid(),
            FormChange = new EnemyFormChange { Changed = 33, Before = pose, After = pose with { Form = 1 }, BeforeAgeMs = 800, ChangeMs = 1, DrawHeightDelta = 0.12f }
        };
        yield return context with
        {
            Kind = ObservationKind.EnemyIdleTrack, GameId = 7777, EnemyTrackId = track,
            Idle = new IdleTrackBuilder(0, Here, 1f, 3, 0).Finish(0)
        };
        yield return context with
        {
            Kind = ObservationKind.EnemyAggro, GameId = 7777, EnemyTrackId = track,
            Aggro = new EnemyAggro
            {
                TargetCategory = 1, DistanceToPlayer = 18.2f, EnemyCast = [123, 1, 0.5f], EnemyStatuses = [[1, 0, 0, 1]],
                Player = new AggroPlayer { Moving = true, Speed = 6, Casting = [7, 1], HpPct = 100, Stealth = true },
                Linked = [new AggroLink { BnpcId = 7777, EnemyTrackId = Guid.NewGuid(), DtMs = 400, Distance = 8, TargetCategory = 2 }]
            }
        };
        yield return context with
        {
            Kind = ObservationKind.EnemyAppeared, GameId = 7777, EnemyTrackId = track, LayoutId = 4012345, FateId = 1367,
            HomePosition = [1, 2, 3], Respawn = new EnemyRespawn { PreviousTrackId = Guid.NewGuid(), IntervalMs = 60_000, MatchedBy = 3 }
        };
        yield return context with
        {
            Kind = ObservationKind.EnemyDied, GameId = 7777, EnemyTrackId = track, Claim = 1, ClaimType = 1, LastTargetCategory = 2
        };
    }

    private static readonly HashSet<string> GuidKeys = ["visitId", "enemyTrackId", "beforeTrackId", "previousTrackId"];

    [Fact]
    public void SerializedRecordsCarryNoStringsExceptRandomTrackIds()
    {
        foreach (var record in SampleRecords())
        {
            var json = JsonSerializer.Serialize(record, ObservationJson.Options);
            using var document = JsonDocument.Parse(json);
            Walk(document.RootElement, null);
            Assert.DoesNotContain("name\"", json, StringComparison.OrdinalIgnoreCase); // nameId is a sheet row: "nameId":
            Assert.DoesNotContain("objectId", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("contentId", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("entityId", json, StringComparison.OrdinalIgnoreCase);
        }

        static void Walk(JsonElement element, string? key)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                        Walk(property.Value, property.Name);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Walk(item, key);
                    break;
                case JsonValueKind.String:
                    Assert.True(key != null && GuidKeys.Contains(key) && Guid.TryParse(element.GetString(), out _),
                        $"string value under \"{key}\"");
                    break;
            }
        }
    }

    [Fact]
    public void PoseObjectsCarryPoseVersionOneAndTheBatchStaysSchemaVersionOne()
    {
        foreach (var record in SampleRecords())
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(record, ObservationJson.Options));
            var root = document.RootElement;
            foreach (var name in new[] { "pose", "formChange", "idle", "aggro", "respawn" })
                if (root.TryGetProperty(name, out var nested))
                    Assert.Equal(1, nested.GetProperty("poseVersion").GetInt32());
        }

        var batch = new ObservationBatch(1, Guid.NewGuid(), SampleRecords().ToArray());
        using var batchJson = JsonDocument.Parse(JsonSerializer.Serialize(batch, ObservationJson.Options));
        Assert.Equal(1, batchJson.RootElement.GetProperty("schemaVersion").GetInt32());
        var spawn = batchJson.RootElement.GetProperty("observations")[0];
        Assert.Equal(15, spawn.GetProperty("kind").GetInt32());
        Assert.Equal(4012345u, spawn.GetProperty("layoutId").GetUInt32());
        Assert.Equal(9.9f, spawn.GetProperty("pose").GetProperty("skeletonTilt").GetSingle());
        Assert.False(spawn.GetProperty("pose").TryGetProperty("equipment", out _)); // null omitted
    }

    [Fact]
    public void SpawnPoseLogLineIsNameFree()
    {
        var line = PoseLog.Line(SampleRecords().First());
        Assert.Equal("pose 15 bnpc 7777 layout 4012345 near 1 tilt 9.9 dy -0.358 form 0 start 0 spawning 1 home 1 fate 1367 dist 43.2", line);
        foreach (var record in SampleRecords())
            Assert.Matches("^pose [A-Za-z0-9 .:-]+$", PoseLog.Line(record));
    }
}

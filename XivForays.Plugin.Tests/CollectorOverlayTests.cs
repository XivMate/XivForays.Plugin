using System.Numerics;
using System.Text;
using Newtonsoft.Json;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;
using PluginConfiguration = XivMate.DataGathering.Forays.Dalamud.Configuration.Configuration;

namespace XivForays.Plugin.Tests;

/// <summary>The collector overlay (0.0.0.16): status mapping, matching, local confirmation, timers and toggles.</summary>
public class CollectorOverlayTests
{
    private const uint Anemos = ForayZones.EurekaAnemos;

    private static CoveragePoint Point(string key, uint bnpc, uint layout, float x, float z, float y = 10f,
        CoverageStatus status = CoverageStatus.ToConfirm, bool heightKnown = true) => new()
    {
        Key = key, BnpcId = bnpc, LayoutId = layout, X = x, Y = y, Z = z, Status = status, HeightKnown = heightKnown
    };

    private static CoverageSnapshot Snapshot(params CoveragePoint[] points) => new(Anemos, "2026-11-13T15:00:00Z", points);

    private static Observation Appeared(Guid track, uint layout = 7000001, bool? seenSpawning = null, float? respawnMax = null,
        float[]? home = null) => new()
    {
        Kind = ObservationKind.EnemyAppeared,
        TerritoryId = Anemos,
        MapId = 414,
        EnemyTrackId = track,
        GameId = 8000,
        LayoutId = layout == 0 ? null : layout,
        HomePosition = home ?? [100f, 10f, 200f],
        SeenSpawning = seenSpawning,
        Respawn = respawnMax is { } max ? new EnemyRespawn { PreviousTrackId = Guid.NewGuid(), MaxPlayerDistance = max } : null
    };

    // ---- status mapping ----

    [Theory]
    [InlineData("exact", "Confirmed", "plugin-spawn", (byte)CoverageStatus.Confirmed, (byte)CoverageQualifier.None)]
    [InlineData("exact", "Seen nearby", "act-nearby", (byte)CoverageStatus.ToConfirm, (byte)CoverageQualifier.None)]
    [InlineData("approximate", "Seen nearby", null, (byte)CoverageStatus.WalkPast, (byte)CoverageQualifier.None)]
    [InlineData("unseen", "Unwatched", null, (byte)CoverageStatus.NeverSeen, (byte)CoverageQualifier.None)]
    [InlineData("unseen", "FATE", null, (byte)CoverageStatus.NeverSeen, (byte)CoverageQualifier.Fate)]
    [InlineData("unseen", "Doubtful", null, (byte)CoverageStatus.NeverSeen, (byte)CoverageQualifier.Doubtful)]
    [InlineData("unseen", "Not real", null, (byte)CoverageStatus.NeverSeen, (byte)CoverageQualifier.NotReal)]
    [InlineData("approximate", "Confirmed", null, (byte)CoverageStatus.Unknown, (byte)CoverageQualifier.None)]
    [InlineData("exact", "Unwatched", null, (byte)CoverageStatus.Unknown, (byte)CoverageQualifier.None)]
    [InlineData(null, null, null, (byte)CoverageStatus.Unknown, (byte)CoverageQualifier.None)]
    public void MapsCoverageRows(string? spawn, string? rank, string? exactBy, byte status, byte qualifier)
    {
        Assert.True(CoverageStatusMap.TryMap(spawn, rank, exactBy, out var mapped, out var q));
        Assert.Equal((CoverageStatus)status, mapped);
        Assert.Equal((CoverageQualifier)qualifier, q);
    }

    [Fact]
    public void DuplicateRowsAreHidden() =>
        Assert.False(CoverageStatusMap.TryMap("exact", "Duplicate", null, out _, out _));

    [Fact]
    public void QualifiersAreNeverAConfirmation()
    {
        foreach (var rank in new[] { "FATE", "Doubtful", "Not real" })
        {
            CoverageStatusMap.TryMap("unseen", rank, null, out var status, out _);
            Assert.NotEqual(CoverageStatus.Confirmed, status);
        }
    }

    // ---- parsing and validation ----

    private static string File(uint territory = Anemos, string schema = "1.0.0", string? rows = null) =>
        "{\"schemaVersion\":\"" + schema + "\",\"territoryId\":" + territory + ",\"dataThrough\":\"2026-11-13T15:00:00Z\"," +
        "\"enemies\":[{\"bnpcId\":8000,\"name\":\"Test Enemy\"}],\"rows\":[" + (rows ??
            "{\"key\":\"a\",\"bnpcId\":8000,\"layoutId\":7000001,\"x\":1,\"y\":2,\"z\":3,\"spawn\":\"exact\",\"rank\":\"Confirmed\",\"exactBy\":\"plugin-spawn\",\"status\":\"partial\",\"ySource\":\"act\",\"condition\":\"night\"}," +
            "{\"key\":\"b\",\"bnpcId\":8000,\"layoutId\":null,\"x\":5,\"y\":-698.1,\"z\":6,\"spawn\":\"approximate\",\"rank\":\"Seen nearby\",\"ySource\":\"home\"}," +
            "{\"key\":\"c\",\"bnpcId\":8000,\"layoutId\":7000002,\"x\":9,\"y\":2,\"z\":9,\"spawn\":\"exact\",\"rank\":\"Duplicate\"}") + "]}";

    [Fact]
    public void ParsesAValidFile()
    {
        var snapshot = CoverageParser.Parse(Encoding.UTF8.GetBytes(File()), Anemos, out var error);
        Assert.NotNull(snapshot);
        Assert.Equal("", error);
        Assert.Equal(2, snapshot!.Points.Count); // Duplicate dropped
        var a = snapshot.ByKey["a"];
        Assert.Equal(CoverageStatus.Confirmed, a.Status);
        Assert.True(a.PhotoNeeded);
        Assert.True(a.HeightKnown);
        Assert.Equal("Test Enemy", a.Name);
        Assert.Equal("night", a.Condition);
        var b = snapshot.ByKey["b"];
        Assert.Equal(0u, b.LayoutId);
        Assert.False(b.HeightKnown); // home height: list only
        Assert.Single(snapshot.WithLayout(7000001));
        Assert.Empty(snapshot.WithLayout(0));
    }

    [Theory]
    [InlineData("wrong zone")]
    [InlineData("schema")]
    [InlineData("not JSON")]
    [InlineData("repeated key")]
    [InlineData("bad coordinates")]
    [InlineData("empty")]
    public void RejectsInvalidFiles(string reason)
    {
        var json = reason switch
        {
            "wrong zone" => File(territory: ForayZones.EurekaPagos),
            "schema" => File(schema: "2.0.0"),
            "not JSON" => "{\"schemaVersion\":\"1.0.0\",\"rows\":[",
            "repeated key" => File(rows: "{\"key\":\"a\",\"x\":1,\"y\":1,\"z\":1},{\"key\":\"a\",\"x\":1,\"y\":1,\"z\":1}"),
            "bad coordinates" => File(rows: "{\"key\":\"a\",\"x\":\"1\",\"y\":1,\"z\":1}"),
            _ => ""
        };
        Assert.Null(CoverageParser.Parse(Encoding.UTF8.GetBytes(json), Anemos, out var error));
        Assert.Equal(reason, error);
    }

    // ---- matching ----

    [Fact]
    public void OneLayoutAtSeveralSpotsIsDecidedByTheAnchor()
    {
        var snapshot = Snapshot(Point("s1", 8000, 7321541, 0, 0), Point("s2", 8000, 7321541, 30, 0), Point("s3", 8000, 7321541, 60, 0));
        var match = CoverageMatcher.Match(snapshot, 8000, 7321541, 30.4f, 0.3f);
        Assert.Equal("s2", match.Point?.Key);
        Assert.Equal(MatchKind.LayoutPosition, match.Kind);
        // A new spot of the same layout must not recolour any of its other spots.
        Assert.Null(CoverageMatcher.Match(snapshot, 8000, 7321541, 45f, 0f).Point);
    }

    [Fact]
    public void LayoutZeroFallsBackToTheNearestRowWithinOneAndAHalfYalms()
    {
        var snapshot = Snapshot(Point("p", 8000, 0, 100, 200), Point("q", 8000, 0, 140, 200));
        var match = CoverageMatcher.Match(snapshot, 8000, 0, 101f, 200.5f);
        Assert.Equal("p", match.Point?.Key);
        Assert.Equal(MatchKind.Position, match.Kind);
        Assert.Null(CoverageMatcher.Match(snapshot, 8000, 0, 103f, 200f).Point); // 3 y: not 12 y walk-past window
        Assert.Null(CoverageMatcher.Match(snapshot, 8001, 0, 100f, 200f).Point); // another bnpcId
    }

    [Fact]
    public void TwoNearbySameEnemyRowsPickTheNearestOrRefuseATie()
    {
        var snapshot = Snapshot(Point("near", 8000, 0, 0, 0), Point("far", 8000, 0, 1.2f, 0));
        Assert.Equal("near", CoverageMatcher.Match(snapshot, 8000, 0, 0.1f, 0).Point?.Key);
        var tie = CoverageMatcher.Match(snapshot, 8000, 0, 0.6f, 0);
        Assert.Null(tie.Point);
        Assert.Equal(MatchKind.Ambiguous, tie.Kind);
    }

    [Fact]
    public void AnAlreadyClaimedRowIsNotMatchedTwice()
    {
        var snapshot = Snapshot(Point("p", 8000, 0, 0, 0));
        Assert.Null(CoverageMatcher.Match(snapshot, 8000, 0, 0, 0, new HashSet<string> { "p" }).Point);
    }

    [Fact]
    public void HeightNeverDecidesTheMatch()
    {
        // Home heights are unreliable (one live row had y = -698.1): x/z only.
        var snapshot = Snapshot(Point("deep", 8000, 0, 50, 50, y: -698.1f, heightKnown: false));
        Assert.Equal("deep", CoverageMatcher.Match(snapshot, 8000, 0, 50.5f, 50.5f).Point?.Key);
    }

    [Fact]
    public void ALayoutOfAnotherEnemyIsUnknownNotAConfirmation()
    {
        var snapshot = Snapshot(Point("p", 9000, 7000001, 0, 0));
        var match = CoverageMatcher.Match(snapshot, 8000, 7000001, 0, 0);
        Assert.Null(match.Point);
        Assert.Equal(MatchKind.BnpcMismatch, match.Kind);
    }

    [Fact]
    public void APulledEnemyKeepsItsSpawnPoint()
    {
        var session = new CollectorSession();
        session.SetSnapshot(Snapshot(Point("spot", 8000, 0, 100, 200)));
        var track = Guid.NewGuid();
        var home = new Vector3(100.2f, 10f, 200.2f);
        session.BeginScan(0, Vector3.Zero);
        session.Seen(track, 8000, 0, home, home, home, false, 0);
        session.EndScan();
        Assert.Equal("spot", session.Live[track].Match.Point?.Key);
        // Pulled 40 y away and in combat: still the same spawn point, not rematched by its position.
        session.BeginScan(500, Vector3.Zero);
        session.Seen(track, 8000, 0, home, home, new Vector3(140f, 12f, 200f), true, 0);
        session.EndScan();
        Assert.Equal("spot", session.Live[track].Match.Point?.Key);
        Assert.True(session.IsOccupied(session.Snapshot!.ByKey["spot"]));
        // Its death timer anchors at the matched row's spawn point, not at the corpse.
        session.BeginScan(1000, Vector3.Zero);
        session.Died(track, 8000, 0, home, new Vector3(140f, 12f, 200f), 0);
        var death = Assert.Single(session.Deaths);
        Assert.Equal(new Vector3(100, 10, 200), death.Anchor);
        Assert.Equal(AnchorSource.Row, death.AnchorSource);
        Assert.False(death.AnchorApproximate);
        Assert.Equal("spot", death.PointKey);
        Assert.False(session.IsOccupied(session.Snapshot!.ByKey["spot"]));
    }

    [Fact]
    public void AMatchedEnemyWithoutHomeAnchorsAtItsRow()
    {
        var session = new CollectorSession();
        session.SetSnapshot(Snapshot(Point("spot", 8000, 0, 0, 0)));
        var track = Guid.NewGuid();
        session.BeginScan(0, Vector3.Zero);
        session.Seen(track, 8000, 0, null, new Vector3(0.5f, 0, 0), new Vector3(0.5f, 0, 0), false, 0);
        session.BeginScan(500, Vector3.Zero);
        session.Seen(track, 8000, 0, null, new Vector3(0.5f, 0, 0), new Vector3(30f, 0, 0), true, 0);
        session.Died(track, 8000, 0, null, new Vector3(30f, 0, 0), 0);
        var death = Assert.Single(session.Deaths);
        Assert.False(death.AnchorApproximate);
        Assert.Equal(new Vector3(0, 10, 0), death.Anchor);
    }

    [Fact]
    public void AnUnmatchedEnemyAnchorsAtItsHomeNotTheCorpse()
    {
        var session = new CollectorSession();
        session.SetSnapshot(Snapshot());
        var track = Guid.NewGuid();
        var home = new Vector3(50, 5, 60);
        session.BeginScan(0, Vector3.Zero);
        session.Seen(track, 8000, 0, home, home, home, false, 0);
        // The home read later (a different DefaultPosition) does not replace the first one.
        session.BeginScan(500, Vector3.Zero);
        session.Seen(track, 8000, 0, new Vector3(90, 5, 60), home, new Vector3(90, 5, 60), true, 0);
        session.Died(track, 8000, 0, new Vector3(90, 5, 60), new Vector3(90, 5, 60), 0);
        var death = Assert.Single(session.Deaths);
        Assert.Equal(home, death.Anchor);
        Assert.Equal(AnchorSource.Home, death.AnchorSource);
        Assert.False(death.AnchorApproximate);
    }

    [Fact]
    public void WithNoRowAndNoHomeTheTimerSitsAtTheDeathSpotFlaggedApproximate()
    {
        var session = new CollectorSession();
        session.SetSnapshot(Snapshot());
        var track = Guid.NewGuid();
        session.BeginScan(0, Vector3.Zero);
        session.Seen(track, 8000, 0, null, new Vector3(0.5f, 0, 0), new Vector3(0.5f, 0, 0), false, 0);
        session.Died(track, 8000, 0, null, new Vector3(30f, 2f, 0), 0);
        var death = Assert.Single(session.Deaths);
        Assert.Equal(AnchorSource.DeathSpot, death.AnchorSource);
        Assert.True(death.AnchorApproximate);
        Assert.Equal(new Vector3(30f, 2f, 0), death.Anchor);
        Assert.Equal("approximate (death spot)", SpawnAnchor.Note(death.AnchorSource));
    }

    [Fact]
    public void SpawnAnchorPrefersTheRowThenTheHomeThenTheDeathSpot()
    {
        var home = new Vector3(10, 3, 10);
        var corpse = new Vector3(50, 4, 10);
        var exact = Point("a", 1, 0, 1, 2, y: 7);
        var walkPast = Point("b", 1, 0, 5, 6, status: CoverageStatus.WalkPast);
        var noHeight = Point("c", 1, 0, 8, 9, y: 99, heightKnown: false);

        Assert.Equal(new ResolvedAnchor(new Vector3(1, 7, 2), true, AnchorSource.Row), SpawnAnchor.Resolve(exact, home, corpse));
        var approx = SpawnAnchor.Resolve(walkPast, home, corpse);
        Assert.Equal(new Vector3(5, 10, 6), approx.Position);
        Assert.True(approx.Approximate);
        Assert.Equal("approximate (walk-past spot)", approx.Note);
        // A row without a trusted height keeps its x/z, the height comes from the home.
        Assert.Equal(new ResolvedAnchor(new Vector3(8, 3, 9), false, AnchorSource.Row), SpawnAnchor.Resolve(noHeight, home, corpse));
        Assert.Equal(new ResolvedAnchor(home, true, AnchorSource.Home), SpawnAnchor.Resolve(null, home, corpse));
        Assert.Equal(new ResolvedAnchor(corpse, true, AnchorSource.DeathSpot), SpawnAnchor.Resolve(null, null, corpse));
        Assert.Equal(AnchorSource.DeathSpot, SpawnAnchor.Resolve(null, Vector3.Zero, corpse).Source);
    }

    // ---- local confirmation ----

    [Fact]
    public void SeenSpawningWithAHomeConfirmsLocally() =>
        Assert.Equal(LocalEvidence.Confirmed, LocalConfirmation.Evaluate(Appeared(Guid.NewGuid(), seenSpawning: true)));

    [Fact]
    public void ARespawnWatchedWithinOneHundredYalmsConfirms() =>
        Assert.Equal(LocalEvidence.Confirmed, LocalConfirmation.Evaluate(Appeared(Guid.NewGuid(), respawnMax: 99.5f)));

    [Theory]
    [InlineData(100.5f)]
    [InlineData(140f)]
    [InlineData(float.NaN)]
    public void AFarRespawnMustNotConfirm(float maxPlayerDistance) =>
        Assert.Equal(LocalEvidence.None, LocalConfirmation.Evaluate(Appeared(Guid.NewGuid(), respawnMax: maxPlayerDistance)));

    [Fact]
    public void WithoutAHomeNothingConfirms()
    {
        Assert.Equal(LocalEvidence.None, LocalConfirmation.Evaluate(Appeared(Guid.NewGuid(), seenSpawning: true, home: [0f, 0f, 0f])));
        Assert.Equal(LocalEvidence.None, LocalConfirmation.Evaluate(Appeared(Guid.NewGuid(), seenSpawning: false)));
    }

    [Fact]
    public void WithoutALayoutTheServerMatchIsUncertain() =>
        Assert.Equal(LocalEvidence.NoLayout, LocalConfirmation.Evaluate(Appeared(Guid.NewGuid(), layout: 0, seenSpawning: true)));

    [Fact]
    public void ASpawnPoseSeenSpawningConfirms()
    {
        var pose = new Observation
        {
            Kind = ObservationKind.EnemySpawnPose,
            EnemyTrackId = Guid.NewGuid(),
            Pose = new EnemyPose { SeenSpawning = true, HomePosition = [1f, 2f, 3f], LayoutId = 7000001 }
        };
        Assert.Equal(LocalEvidence.Confirmed, LocalConfirmation.Evaluate(pose));
        Assert.Equal(LocalEvidence.None, LocalConfirmation.Evaluate(pose with { Pose = pose.Pose with { SeenSpawning = false } }));
    }

    [Fact]
    public void TheSessionConfirmsOnlyServerEquivalentEvidence()
    {
        var session = new CollectorSession();
        session.SetSnapshot(Snapshot(Point("spot", 8000, 7000001, 100, 200, status: CoverageStatus.ToConfirm)));
        var far = Guid.NewGuid();
        session.BeginScan(0, Vector3.Zero);
        Assert.Null(session.Evidence(Appeared(far, respawnMax: 160f)));
        Assert.Empty(session.Confirmations);
        Assert.Equal(CoverageStatus.ToConfirm, session.StatusOf(session.Snapshot!.ByKey["spot"], out _));

        var near = Guid.NewGuid();
        var confirmation = session.Evidence(Appeared(near, respawnMax: 60f));
        Assert.NotNull(confirmation);
        Assert.Equal("spot", confirmation!.Key);
        Assert.Equal(CoverageStatus.Confirmed, session.StatusOf(session.Snapshot.ByKey["spot"], out var local));
        Assert.NotNull(local); // upload pending until the server agrees
    }

    [Fact]
    public void ACleanSpawnFarFromAnyRowIsAProvisionalPoint()
    {
        var session = new CollectorSession();
        session.SetSnapshot(Snapshot(Point("spot", 8000, 0, 0, 0)));
        session.BeginScan(0, Vector3.Zero);
        var confirmation = session.Evidence(Appeared(Guid.NewGuid(), layout: 0, seenSpawning: true, home: [500f, 1f, 500f]));
        Assert.NotNull(confirmation);
        Assert.True(confirmation!.Provisional);
        Assert.True(confirmation.NoLayout);
        Assert.Equal(CoverageStatus.ToConfirm, session.StatusOf(session.Snapshot!.ByKey["spot"], out _));
    }

    [Fact]
    public void TheLocalOverrideGoesOnceTheServerAgreesEvenUnderANewKey()
    {
        var session = new CollectorSession();
        session.SetSnapshot(Snapshot(Point("layout:7000001", 8000, 7000001, 100, 200, status: CoverageStatus.WalkPast)));
        session.BeginScan(0, Vector3.Zero);
        Assert.NotNull(session.Evidence(Appeared(Guid.NewGuid(), seenSpawning: true, home: [100.3f, 10f, 200.3f])));
        Assert.Single(session.Confirmations);
        // A stale download (same rows) must not undo the local feedback.
        session.SetSnapshot(Snapshot(Point("layout:7000001", 8000, 7000001, 100, 200, status: CoverageStatus.WalkPast)));
        Assert.Single(session.Confirmations);
        // Aggregation replaced the approximate row with an exact Confirmed one under another key.
        session.SetSnapshot(Snapshot(Point("Anemos:8000:exact:1", 8000, 7000001, 100.3f, 200.3f, status: CoverageStatus.Confirmed)));
        Assert.Empty(session.Confirmations);
    }

    // ---- respawn countdown ----

    [Fact]
    public void TheCountdownIsApproximateAndEndsAtAwaitingSpawn()
    {
        Assert.Equal("~50 s", RespawnText.Text(RespawnText.Remaining(0, 0)));
        Assert.Equal("~18 s", RespawnText.Text(RespawnText.Remaining(0, 32_000)));
        Assert.Equal("~1 s", RespawnText.Text(RespawnText.Remaining(0, 49_500)));
        Assert.Equal(RespawnText.Awaiting, RespawnText.Text(RespawnText.Remaining(0, 50_000)));
        Assert.Equal(RespawnText.Awaiting, RespawnText.Text(RespawnText.Remaining(0, 600_000)));
        Assert.DoesNotContain("Confirmed", RespawnText.Text(0));
    }

    [Fact]
    public void YourOwnKillsComeFirstThroughDefeatLinks()
    {
        var session = new CollectorSession();
        session.BeginScan(0, Vector3.Zero);
        var observed = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var party = Guid.NewGuid();
        session.Died(observed, 1, 0, new Vector3(1, 0, 0), Vector3.Zero, 0);
        session.DefeatLinked(mine, 1); // the notice can arrive before the scan sees HP 0
        session.Died(mine, 2, 0, new Vector3(2, 0, 0), Vector3.Zero, 0);
        session.Died(party, 3, 0, new Vector3(3, 0, 0), Vector3.Zero, 0);
        session.DefeatLinked(party, 2);
        var buffer = new OverlayDeath[5];
        Assert.Equal(3, session.SortedDeaths(buffer));
        Assert.Equal(mine, buffer[0].TrackId);
        Assert.Equal(party, buffer[1].TrackId);
        Assert.Equal(observed, buffer[2].TrackId);
    }

    [Fact]
    public void LeavingOneHundredYalmsLosesTheRespawnWatch()
    {
        var session = new CollectorSession();
        var anchor = new Vector3(0, 0, 0);
        session.BeginScan(0, new Vector3(10, 0, 0));
        session.Died(Guid.NewGuid(), 1, 0, anchor, anchor, 0);
        session.BeginScan(500, new Vector3(95, 0, 0));
        Assert.False(WatchRules.ContinuityLost(session.Deaths[0].MaxPlayerDistance));
        session.BeginScan(1000, new Vector3(101, 0, 0));
        session.BeginScan(1500, new Vector3(10, 0, 0)); // coming back does not erase the excursion
        Assert.True(WatchRules.ContinuityLost(session.Deaths[0].MaxPlayerDistance));
    }

    [Fact]
    public void ARespawnEndsTheCountdown()
    {
        var session = new CollectorSession();
        session.BeginScan(0, Vector3.Zero);
        var dead = Guid.NewGuid();
        session.Died(dead, 8000, 0, Vector3.Zero, Vector3.Zero, 0);
        var appeared = Appeared(Guid.NewGuid(), respawnMax: 150f) with
        {
            Respawn = new EnemyRespawn { PreviousTrackId = dead, MaxPlayerDistance = 150f }
        };
        Assert.Null(session.Evidence(appeared));
        Assert.Empty(session.Deaths);
    }

    // ---- range and readiness ----

    [Fact]
    public void TheWatchMeterAgreesWithTheCaptureHistory()
    {
        var random = new Random(7);
        for (var run = 0; run < 50; run++)
        {
            var meter = new WatchMeter();
            var history = new PlayerHistory();
            var point = new Vector3(0, 0, 0);
            long now = 0;
            for (var step = 0; step < 30; step++)
            {
                now += 400 + random.Next(300);
                var position = new Vector3(random.Next(-100, 100), random.Next(-5, 5), random.Next(-100, 100));
                meter.Add(now, position);
                history.Add(now, position);
                Assert.Equal(history.StoodWithin(point, 80f, now), meter.StoodWithin(point, 80f, now));
            }
        }
    }

    [Fact]
    public void MovingAroundInsideEightyYalmsIsFine()
    {
        var meter = new WatchMeter();
        var point = Vector3.Zero;
        for (long t = 0; t <= 5000; t += 500)
            meter.Add(t, new Vector3(t % 1000 == 0 ? 70 : -70, 0, 0));
        Assert.True(meter.StoodWithin(point, 80f, 5000));
        Assert.Equal(5000, meter.WithinMs(point, 80f, 5000));
        Assert.Equal(WatchAdvice.Ready, WatchRules.Advice(70, true));
        Assert.Equal(WatchAdvice.Filling, WatchRules.Advice(62, false));
        Assert.Equal(WatchAdvice.MoveCloser, WatchRules.Advice(81, true));
        meter.Add(5500, new Vector3(90, 0, 0));
        Assert.False(meter.StoodWithin(point, 80f, 5500));
        Assert.Equal(0, meter.WithinMs(point, 80f, 5500));
    }

    [Theory]
    [InlineData(0f, -10f, "N")]
    [InlineData(10f, 0f, "E")]
    [InlineData(0f, 10f, "S")]
    [InlineData(-10f, 0f, "W")]
    [InlineData(10f, -10f, "NE")]
    [InlineData(-10f, 10f, "SW")]
    public void CompassUsesWorldNorth(float dx, float dz, string expected) =>
        Assert.Equal(expected, Compass.Direction(dx, dz));

    // ---- conditions ----

    [Theory]
    [InlineData("any time", null, null, (byte)ConditionState.Available)]
    [InlineData("night", null, true, (byte)ConditionState.Available)]
    [InlineData("night", null, false, (byte)ConditionState.Unavailable)]
    [InlineData("weather Blizzard/Snow", "Blizzards", null, (byte)ConditionState.Available)]
    [InlineData("weather Gales", "Fair Skies", null, (byte)ConditionState.Unavailable)]
    [InlineData("weather Gales", null, null, (byte)ConditionState.Unknown)]
    [InlineData("during its FATE (1346)", null, null, (byte)ConditionState.Available)]
    [InlineData("during its FATE (1341)", null, null, (byte)ConditionState.Unavailable)]
    [InlineData("during its FATE (id unknown)", null, null, (byte)ConditionState.Unknown)]
    [InlineData("during its FATE (1346) + weather Gales", "Snow", null, (byte)ConditionState.Unavailable)]
    [InlineData("something new", null, null, (byte)ConditionState.Unknown)]
    public void ReadsConditionText(string condition, string? weather, bool? night, byte expected) =>
        Assert.Equal((ConditionState)expected, ConditionCheck.Evaluate(condition, weather, night, new HashSet<uint> { 1346 }));

    [Fact]
    public void MissingFateDataNeverMeansInactive() =>
        Assert.Equal(ConditionState.Unknown, ConditionCheck.Evaluate("during its FATE (1346)", null, null, null));

    [Fact]
    public void NightFollowsTheExporterBoundary()
    {
        Assert.True(ConditionCheck.IsNight(0)); // ET 00:00
        Assert.False(ConditionCheck.IsNight(6 * 175_000L));
        Assert.True(ConditionCheck.IsNight(18 * 175_000L));
        Assert.Equal("Needs Gales", ConditionCheck.Reason("weather Gales"));
        Assert.Equal("Night only", ConditionCheck.Reason("weather Gales + night"));
        Assert.Equal("FATE inactive", ConditionCheck.Reason("during its FATE (1329) + weather Gales"));
    }

    // ---- supported zones ----

    [Theory]
    [InlineData(ForayZones.EurekaAnemos, 414u, true)]
    [InlineData(ForayZones.EurekaHydatos, 515u, true)]
    [InlineData(ForayZones.EurekaHydatos, 520u, false)] // Baldesion Arsenal
    [InlineData(ForayZones.EurekaHydatos, 527u, false)]
    [InlineData(ForayZones.EurekaPyros, 0u, false)] // loading
    [InlineData(ForayZones.BozjanSouthernFront, 606u, false)]
    [InlineData(ForayZones.SouthHorn, 1u, false)]
    public void OnlyEurekaFieldMapsHaveCoverage(uint territory, uint map, bool expected) =>
        Assert.Equal(expected, OverlayZones.IsSupportedFieldMap(territory, map));

    // ---- toggles ----

    [Fact]
    public void EverythingIsOffByDefault()
    {
        var features = new PluginConfiguration().OverlayFeatures;
        Assert.False(features.Enabled);
        Assert.False(features.NeedsDownload);
        Assert.False(features.NeedsScan);
        Assert.False(features.DrawsWorld);
        Assert.False(features.NearbyList || features.Respawn || features.Range || features.LocalConfirm ||
                     features.EnemyMarkers || features.ShowConfirmed || features.EmptyPoints || features.Rings || features.Conditions);
        Assert.Equal(100f, features.DisplayYalms);
        Assert.Equal(64, features.MaxMarkers);
    }

    [Fact]
    public void AnOlderConfigLoadsWithTheOverlayOff()
    {
        var config = JsonConvert.DeserializeObject<PluginConfiguration>("{\"Version\":0,\"CanCrowdsourceData\":true}")!;
        Assert.False(config.OverlayFeatures.Enabled);
    }

    [Fact]
    public void TheMasterSwitchOffMeansNoDownloadAndNoScanWorkWhateverIsTicked()
    {
        var config = new PluginConfiguration
        {
            OverlayEnabled = false,
            OverlayNearbyList = true, OverlayRespawn = true, OverlayRange = true, OverlayLocalConfirm = true,
            OverlayEnemyMarkers = true, OverlayShowConfirmed = true, OverlayEmptyPoints = true, OverlayRings = true,
            OverlayConditions = true
        };
        var off = config.OverlayFeatures;
        Assert.False(off.NeedsDownload);
        Assert.False(off.NeedsScan);
        Assert.False(off.DrawsWorld);
        Assert.False(off.EnemyMarkers);

        config.OverlayEnabled = true;
        Assert.True(config.OverlayFeatures.NeedsScan);
        // Master on with only the condition text: a download, but no scan hook.
        var textOnly = new PluginConfiguration { OverlayEnabled = true, OverlayConditions = true }.OverlayFeatures;
        Assert.True(textOnly.NeedsDownload);
        Assert.False(textOnly.NeedsScan);
    }

    [Fact]
    public void OptionsAreClamped()
    {
        var features = new PluginConfiguration
        {
            OverlayEnabled = true, OverlayDisplayYalms = 900, OverlayMaxMarkers = 1000, OverlayListRows = 2, OverlayOpacity = float.NaN
        }.OverlayFeatures;
        Assert.Equal(150f, features.DisplayYalms);
        Assert.Equal(64, features.MaxMarkers);
        Assert.Equal(5, features.ListRows);
        Assert.Equal(0.9f, features.Opacity);
    }
}

/// <summary>The coverage download: 200 / 304 / corrupt / wrong zone / offline / late answers.</summary>
public class CoverageFeedTests
{
    private const uint Anemos = ForayZones.EurekaAnemos;
    private const uint Pagos = ForayZones.EurekaPagos;

    private static byte[] Body(uint territory, string key = "a") => Encoding.UTF8.GetBytes(
        "{\"schemaVersion\":\"1.0.0\",\"territoryId\":" + territory + ",\"dataThrough\":\"2026-11-13T15:00:00Z\",\"rows\":[" +
        "{\"key\":\"" + key + "\",\"bnpcId\":8000,\"layoutId\":7000001,\"x\":1,\"y\":2,\"z\":3,\"spawn\":\"exact\",\"rank\":\"Seen nearby\",\"exactBy\":\"act-nearby\"}]}");

    private sealed class FakeFetcher : ICoverageFetcher
    {
        public readonly Queue<Func<Task<CoverageResponse>>> Answers = new();
        public readonly List<(string Url, string? ETag)> Requests = new();

        public Task<CoverageResponse> Get(string url, string? etag, string? lastModified, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add((url, etag));
            return Answers.Count > 0 ? Answers.Dequeue()() : Task.FromResult(new CoverageResponse(0, null, null, null));
        }
    }

    private sealed class MemoryStore : ICoverageStore
    {
        public readonly Dictionary<string, (byte[] Body, string? ETag, string? LastModified)> Files = new();
        public (byte[] Body, string? ETag, string? LastModified)? Load(string zone) => Files.TryGetValue(zone, out var f) ? f : null;
        public void Save(string zone, byte[] body, string? etag, string? lastModified) => Files[zone] = (body, etag, lastModified);
    }

    private static void Run(CoverageFeed feed, long now, uint territory)
    {
        feed.Tick(now, territory);
        Assert.True(feed.WaitForCompletion(5000));
        feed.Tick(now + 1, territory);
    }

    [Fact]
    public void A200ThenA304KeepsTheBodyAndSendsTheETag()
    {
        var fetcher = new FakeFetcher();
        var store = new MemoryStore();
        var feed = new CoverageFeed(fetcher, store);
        fetcher.Answers.Enqueue(() => Task.FromResult(new CoverageResponse(200, Body(Anemos), "\"v1\"", null)));
        Run(feed, 0, Anemos);
        Assert.Equal(CoverageFeedState.Fresh, feed.State);
        var first = feed.Snapshot;
        Assert.NotNull(first);
        Assert.EndsWith("coverage-anemos.json", fetcher.Requests[0].Url);
        Assert.Null(fetcher.Requests[0].ETag);
        Assert.True(store.Files.ContainsKey("anemos"));

        // Not again before the 3 minute poll.
        feed.Tick(60_000, Anemos);
        Assert.Single(fetcher.Requests);

        fetcher.Answers.Enqueue(() => Task.FromResult(new CoverageResponse(304, null, "\"v1\"", null)));
        Run(feed, CoverageFeed.PollMs + CoverageFeed.PollJitterMs + 1, Anemos);
        Assert.Equal(2, fetcher.Requests.Count);
        Assert.Equal("\"v1\"", fetcher.Requests[1].ETag);
        Assert.Same(first, feed.Snapshot);
        Assert.Equal(CoverageFeedState.Fresh, feed.State);
    }

    [Fact]
    public void ACorruptOrWrongZoneAnswerKeepsTheLastGoodSnapshot()
    {
        var fetcher = new FakeFetcher();
        var feed = new CoverageFeed(fetcher, null);
        fetcher.Answers.Enqueue(() => Task.FromResult(new CoverageResponse(200, Body(Anemos), "\"v1\"", null)));
        Run(feed, 0, Anemos);
        var good = feed.Snapshot;

        fetcher.Answers.Enqueue(() => Task.FromResult(new CoverageResponse(200, Encoding.UTF8.GetBytes("{broken"), "\"v2\"", null)));
        Run(feed, 200_000, Anemos);
        Assert.Same(good, feed.Snapshot);
        Assert.Equal(CoverageFeedState.OfflineCached, feed.State);
        Assert.Equal("not JSON", feed.LastError);

        fetcher.Answers.Enqueue(() => Task.FromResult(new CoverageResponse(200, Body(Pagos), "\"v3\"", null)));
        Run(feed, 400_000, Anemos);
        Assert.Same(good, feed.Snapshot);
        Assert.Equal("wrong zone", feed.LastError);
    }

    [Fact]
    public void OfflineWithoutACacheIsUnavailableAndWithACacheShowsIt()
    {
        var fetcher = new FakeFetcher();
        var feed = new CoverageFeed(fetcher, new MemoryStore());
        fetcher.Answers.Enqueue(() => throw new HttpRequestException("offline"));
        Run(feed, 0, Anemos);
        Assert.Null(feed.Snapshot);
        Assert.Equal(CoverageFeedState.Unavailable, feed.State);

        var store = new MemoryStore();
        store.Save("anemos", Body(Anemos, "cached"), "\"old\"", null);
        var cached = new CoverageFeed(fetcher, store);
        fetcher.Answers.Enqueue(() => Task.FromResult(new CoverageResponse(0, null, null, null)));
        cached.Tick(0, Anemos);
        Assert.NotNull(cached.Snapshot); // shown at once, before any answer
        Assert.True(cached.WaitForCompletion(5000));
        cached.Tick(1, Anemos);
        Assert.Equal(CoverageFeedState.OfflineCached, cached.State);
        Assert.True(cached.Snapshot!.ByKey.ContainsKey("cached"));
        Assert.Equal("\"old\"", fetcher.Requests[^1].ETag);
    }

    [Fact]
    public void ALateAnswerForThePreviousZoneIsDropped()
    {
        var fetcher = new FakeFetcher();
        var feed = new CoverageFeed(fetcher, null);
        var late = new TaskCompletionSource<CoverageResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fetcher.Answers.Enqueue(() => late.Task);
        feed.Tick(0, Anemos);
        Assert.True(feed.Busy);

        // Zone change to Pagos: the Anemos request is cancelled and its answer, arriving late, is ignored.
        fetcher.Answers.Enqueue(() => Task.FromResult(new CoverageResponse(200, Body(Pagos, "pagos"), "\"p\"", null)));
        feed.Tick(100, Pagos);
        Assert.True(feed.WaitForCompletion(5000));
        late.SetResult(new CoverageResponse(200, Body(Anemos, "anemos"), "\"a\"", null));
        Thread.Sleep(50);
        feed.Tick(200, Pagos);
        Assert.NotNull(feed.Snapshot);
        Assert.Equal(Pagos, feed.Snapshot!.Territory);
        Assert.True(feed.Snapshot.ByKey.ContainsKey("pagos"));
    }

    [Fact]
    public void DisablingCancelsAndForgets()
    {
        var fetcher = new FakeFetcher();
        var feed = new CoverageFeed(fetcher, null);
        var pending = new TaskCompletionSource<CoverageResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fetcher.Answers.Enqueue(() => pending.Task);
        feed.Tick(0, Anemos);
        feed.Stop();
        pending.SetResult(new CoverageResponse(200, Body(Anemos), "\"a\"", null));
        Thread.Sleep(50);
        Assert.Null(feed.Snapshot);
        Assert.Equal(CoverageFeedState.Idle, feed.State);
        Assert.False(feed.Busy);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(ForayZones.BozjanSouthernFront)]
    [InlineData(ForayZones.SouthHorn)]
    public void UnsupportedZonesStartNoRequest(uint territory)
    {
        var fetcher = new FakeFetcher();
        var feed = new CoverageFeed(fetcher, null);
        for (long t = 0; t < 10 * CoverageFeed.PollMs; t += 500)
            feed.Tick(t, territory);
        Assert.Equal(0, feed.RequestsStarted);
        Assert.Empty(fetcher.Requests);
    }

    [Fact]
    public void AnUntickedFeedNeverRequests()
    {
        // The overlay off: the service creates no feed and runs no tick, so nothing is requested.
        var fetcher = new FakeFetcher();
        _ = new CoverageFeed(fetcher, null);
        Assert.Empty(fetcher.Requests);
    }

    [Fact]
    public void FailuresBackOff()
    {
        var fetcher = new FakeFetcher();
        var feed = new CoverageFeed(fetcher, null);
        Run(feed, 0, Anemos); // offline (no answer queued)
        feed.Tick(5_000, Anemos);
        Assert.Single(fetcher.Requests);
        Run(feed, 15_002, Anemos);
        Assert.Equal(2, fetcher.Requests.Count);
        feed.Tick(15_002 + 20_000, Anemos); // second retry waits 30 s
        Assert.Equal(2, fetcher.Requests.Count);
    }
}

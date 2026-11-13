using System.Numerics;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;
using PluginConfiguration = XivMate.DataGathering.Forays.Dalamud.Configuration.Configuration;

namespace XivForays.Plugin.Tests;

/// <summary>Collector overlay fixes after the first in-game test: one label per spawn, condition-aware timers, pack watch.</summary>
public class OverlayRoundOneFixTests
{
    private const uint Pagos = ForayZones.EurekaPagos;
    private const long Hour = EorzeaClock.EtHourMs;

    // ---- one label per spawn ----

    [Fact]
    public void TheSameSpawnIsLabelledOnlyOnce()
    {
        var labels = new SpawnLabels();
        Assert.True(labels.TryClaim("layout:1", new Vector3(10, 0, 10)));
        // The confirmation flash of the same row, a little above or beside the enemy marker: skipped.
        Assert.False(labels.TryClaim("layout:1", new Vector3(10.4f, 3, 10.2f)));
        labels.Clear();
        Assert.True(labels.TryClaim("layout:1", new Vector3(10, 0, 10)));
    }

    [Fact]
    public void AProvisionalFlashOnAnUnmatchedEnemyIsDeduplicatedByPosition()
    {
        var labels = new SpawnLabels();
        Assert.True(labels.TryClaim(null, new Vector3(50, 0, 50)));
        Assert.False(labels.TryClaim("local:abc", new Vector3(50.5f, 2, 50.5f)));
        Assert.True(labels.TryClaim("local:def", new Vector3(60, 0, 50)));
    }

    [Fact]
    public void TwoRowsCloseTogetherKeepTheirOwnLabels()
    {
        var labels = new SpawnLabels();
        Assert.True(labels.TryClaim("layout:1", new Vector3(0, 0, 0)));
        Assert.True(labels.TryClaim("layout:2", new Vector3(1, 0, 0)));
    }

    [Fact]
    public void AnAppearanceAndItsPoseGiveOneConfirmation()
    {
        var session = new CollectorSession();
        session.SetSnapshot(new CoverageSnapshot(Pagos, "x", [Row("layout:7", 9000, 7, 100, 200)]));
        var track = Guid.NewGuid();
        session.BeginScan(1000, new Vector3(100, 10, 210));
        session.Seen(track, 9000, 7, new Vector3(100, 10, 200), new Vector3(100, 10, 200), new Vector3(100, 10, 200), false, 0);
        var first = session.Evidence(new Observation
        {
            Kind = ObservationKind.EnemyAppeared, EnemyTrackId = track, GameId = 9000, LayoutId = 7,
            HomePosition = [100f, 10f, 200f], SeenSpawning = true
        });
        var second = session.Evidence(new Observation
        {
            Kind = ObservationKind.EnemySpawnPose, EnemyTrackId = track, GameId = 9000, LayoutId = 7,
            Pose = new EnemyPose { HomePosition = [100f, 10f, 200f], SeenSpawning = true, LayoutId = 7 }
        });
        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Single(session.Confirmations);
    }

    // ---- Eorzea clock and weather (same maths as the website) ----

    [Theory]
    [InlineData(1763060000000L, 55)]
    [InlineData(1700000000000L, 53)]
    [InlineData(1800000000123L, 78)]
    public void TheWeatherHashMatchesTheWebsite(long ms, int expected) => Assert.Equal(expected, EorzeaClock.WeatherTarget(ms));

    [Fact]
    public void TheWeatherFollowsTheZoneTable()
    {
        // Target 55 in Pagos: Fair 10, Fog 18 (28), Heat 18 (46), Snow 18 (64) -> Snow.
        Assert.Equal("Snow", EorzeaClock.WeatherAt(Pagos, 1763060000000L));
        Assert.Null(EorzeaClock.WeatherAt(9999, 1763060000000L));
    }

    [Fact]
    public void NightIsEighteenToSix()
    {
        Assert.True(EorzeaClock.IsNight(18 * Hour));
        Assert.True(EorzeaClock.IsNight(5 * Hour));
        Assert.False(EorzeaClock.IsNight(6 * Hour));
        Assert.Equal("18:00", EorzeaClock.EtText(18 * Hour));
    }

    [Fact]
    public void ANightWindowStartsAtEighteen()
    {
        var noon = 1000 * EorzeaClock.EtDayMs + 12 * Hour;
        var window = EorzeaClock.Window(Pagos, "night", noon);
        Assert.NotNull(window);
        Assert.False(window!.Value.Active);
        Assert.Equal(1000 * EorzeaClock.EtDayMs + 18 * Hour, window.Value.Start);
        Assert.Equal(1001 * EorzeaClock.EtDayMs + 6 * Hour, window.Value.End);
    }

    [Fact]
    public void AWeatherWindowStartsWhereTheForecastTurns()
    {
        var now = 1763060000000L;
        var window = EorzeaClock.Window(Pagos, "weather Heat", now);
        Assert.NotNull(window);
        var w = window!.Value;
        Assert.Equal("Heat", EorzeaClock.WeatherAt(Pagos, w.Active ? now : w.Start));
        if (!w.Active)
            Assert.NotEqual("Heat", EorzeaClock.WeatherAt(Pagos, w.Start - 1));
        Assert.NotEqual("Heat", EorzeaClock.WeatherAt(Pagos, w.End));
        Assert.Equal(0, w.Start % EorzeaClock.WeatherPeriodMs);
    }

    [Fact]
    public void AWeatherTheZoneNeverHasHasNoWindow() => Assert.Null(EorzeaClock.Window(Pagos, "weather Gales", 1763060000000L));

    // ---- condition-aware respawn timer ----

    [Fact]
    public void AshkinUseTheirMeasuredTimerNotFiftySeconds()
    {
        var zombie = RespawnTable.For(9069, "night", Pagos); // Zombie Brobinyak, n 37
        Assert.True(zombie.HasTimer);
        Assert.Equal(146, zombie.Seconds);
        Assert.Equal(37, zombie.Count);
        var sprite = RespawnTable.For(9090, "weather Blizzard/Snow", Pagos); // Snowstorm Sprite
        Assert.Equal(191, sprite.Seconds);
        Assert.Equal("n 27", RespawnTimer.Basis(sprite));
    }

    [Fact]
    public void AnUnmeasuredNightEnemyUsesTheNightClass()
    {
        var estimate = RespawnTable.For(123456, "night", Pagos);
        Assert.Equal(146, estimate.Seconds);
        Assert.Equal("class, n 248", RespawnTimer.Basis(estimate));
        Assert.Equal(191, RespawnTable.For(123456, "weather Fog", ForayZones.EurekaPyros).Seconds);
    }

    [Fact]
    public void TooFewSamplesFallBackToTheClass()
    {
        var wolf = RespawnTable.For(8653, "night", ForayZones.EurekaAnemos); // Crystal Wolf, n 4
        Assert.Equal(-RespawnTable.NightClass.Count, wolf.Count);
    }

    [Fact]
    public void WithoutDataTheTimerIsUnknownNotFiftySeconds()
    {
        // Hydatos has no measured kills yet.
        var estimate = RespawnTable.For(123456, "night", ForayZones.EurekaHydatos);
        Assert.False(estimate.HasTimer);
        var night = 1000 * EorzeaClock.EtDayMs + 20 * Hour;
        Assert.Equal("timer unknown (night only)", RespawnTimer.Text(ForayZones.EurekaHydatos, "night", estimate, night, night + 1000, out var left));
        Assert.Equal(-1, left);
    }

    [Fact]
    public void AnyTimeEnemiesKeepTheDefault()
    {
        var estimate = RespawnTable.For(123456, "any time", Pagos);
        Assert.Equal(OverlayRates.RespawnEstimateSeconds, estimate.Seconds);
        Assert.Equal("default", RespawnTimer.Basis(estimate));
        Assert.Equal("~40 s", RespawnTimer.Text(Pagos, "any time", estimate, 0, 10_000, out _));
    }

    [Fact]
    public void ANightEnemyByDaySaysWhenItIsBack()
    {
        var noon = 1000 * EorzeaClock.EtDayMs + 12 * Hour; // night in 6 ET hours = 17.5 min
        var text = RespawnTimer.Text(Pagos, "night", RespawnTable.For(9069, "night", Pagos), noon - 5000, noon, out var left);
        Assert.Equal("Back at night (ET 18:00, in ~18 min)", text);
        Assert.Equal(-1, left);
        Assert.False(RespawnTimer.HoldsNow(Pagos, "night", noon));
    }

    [Fact]
    public void ANightEnemyAtNightCountsDown()
    {
        var night = 1000 * EorzeaClock.EtDayMs + 20 * Hour;
        var text = RespawnTimer.Text(Pagos, "night", RespawnTable.For(9069, "night", Pagos), night, night + 46_000, out var left);
        Assert.Equal("~100 s", text);
        Assert.Equal(100, left);
    }

    [Fact]
    public void TheCountStartsWhenTheConditionCameBack()
    {
        // Killed at ET 05:00, night ended at 06:00 and came back at 18:00: the timer runs from 18:00.
        var day = 1000 * EorzeaClock.EtDayMs;
        var died = day + 5 * Hour;
        var now = day + 18 * Hour + 46_000;
        RespawnTimer.Text(Pagos, "night", RespawnTable.For(9069, "night", Pagos), died, now, out var left);
        Assert.Equal(100, left);
    }

    [Fact]
    public void ASpriteOutOfItsWeatherSaysWhichWeatherAndWhen()
    {
        var now = 1763060000000L; // Snow in Pagos
        var text = RespawnTimer.Text(Pagos, "weather Heat", RespawnTable.For(9093, "weather Heat", Pagos), now - 1000, now, out _);
        Assert.StartsWith("Needs Heat (next in ~", text);
        Assert.EndsWith(" min)", text);
    }

    [Fact]
    public void FateAndUnknownConditionsAreNotForecast()
    {
        Assert.True(RespawnTimer.HoldsNow(Pagos, "during its FATE (1351)", 0));
        Assert.True(RespawnTimer.HoldsNow(Pagos, "something new", 0));
        Assert.True(SpawnCondition.TryParse("during its FATE (1360) + weather Fog", out var both));
        Assert.False(both.Forecastable);
    }

    // ---- pack watch geometry ----

    [Fact]
    public void TheEnclosingCircleOfASquareIsItsCentre()
    {
        var (centre, radius) = PackGeometry.EnclosingCircle([new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(5, 5)]);
        Assert.Equal(5f, centre.X, 3);
        Assert.Equal(5f, centre.Y, 3);
        Assert.Equal(MathF.Sqrt(50), radius, 3);
    }

    [Fact]
    public void TheEnclosingCircleHandlesOneAndCollinearPoints()
    {
        Assert.Equal(0f, PackGeometry.EnclosingCircle([new(3, 4)]).Radius);
        var (centre, radius) = PackGeometry.EnclosingCircle([new(0, 0), new(10, 0), new(4, 0)]);
        Assert.Equal(5f, centre.X, 3);
        Assert.Equal(5f, radius, 3);
        Assert.Equal(-1f, PackGeometry.EnclosingCircle([]).Radius);
    }

    [Fact]
    public void TheEnclosingCircleIsMinimalAndCoversEverything()
    {
        var random = new Random(7);
        for (var round = 0; round < 50; round++)
        {
            var points = new List<Vector2>();
            for (var i = 0; i < 2 + round % 7; i++)
                points.Add(new Vector2(random.Next(-100, 100), random.Next(-100, 100)));
            var (centre, radius) = PackGeometry.EnclosingCircle(points);
            foreach (var p in points)
                Assert.True(Vector2.Distance(p, centre) <= radius + 0.01f);
            // Never larger than the brute force over all pair and triple circles that cover every point.
            var best = float.MaxValue;
            for (var i = 0; i < points.Count; i++)
            for (var j = i + 1; j < points.Count; j++)
            {
                var c = (points[i] + points[j]) / 2;
                var r = Vector2.Distance(points[i], points[j]) / 2;
                if (points.TrueForAll(p => Vector2.Distance(p, c) <= r + 0.01f))
                    best = MathF.Min(best, r);
            }

            Assert.True(radius <= best + 0.01f || best == float.MaxValue);
        }
    }

    [Fact]
    public void TheSafeAreaKeepsEveryPointWithinTheRadius()
    {
        List<Vector2> points = [new(0, 0), new(60, 0), new(30, 40)];
        var (centre, _) = PackGeometry.EnclosingCircle(points);
        Assert.True(PackGeometry.Coverable(points, 60));
        Assert.True(PackGeometry.InAll(points, centre, 60));
        var outline = new Vector2[64];
        PackGeometry.Outline(points, centre, 60, outline);
        foreach (var edge in outline)
        {
            Assert.True(PackGeometry.InAll(points, edge, 60.01f));
            // Just outside the edge, at least one point is out of range.
            var beyond = centre + (edge - centre) * 1.02f + Vector2.Normalize(edge - centre) * 0.5f;
            Assert.False(PackGeometry.InAll(points, beyond, 60));
        }
    }

    [Fact]
    public void MovingTheAdvisedDistanceBringsEveryPointInRange()
    {
        List<Vector2> points = [new(0, 0), new(50, 0), new(25, 30)];
        var (centre, _) = PackGeometry.EnclosingCircle(points);
        var player = new Vector2(150, 80);
        var move = PackGeometry.MoveInto(points, centre, 60, player);
        Assert.True(move > 0);
        var after = player + Vector2.Normalize(centre - player) * (move + 0.1f);
        Assert.True(PackGeometry.InAll(points, after, 60));
        var before = player + Vector2.Normalize(centre - player) * (move - 1f);
        Assert.False(PackGeometry.InAll(points, before, 60));
        Assert.Equal(0f, PackGeometry.MoveInto(points, centre, 60, centre));
    }

    [Fact]
    public void PointsTooFarApartHaveNoCommonSpot()
    {
        List<Vector2> points = [new(0, 0), new(140, 0)];
        Assert.False(PackGeometry.Coverable(points, 60));
        var (a, b, d) = PackGeometry.FarthestPair(points);
        Assert.Equal((0, 1), (a, b));
        Assert.Equal(140f, d, 3);
    }

    // ---- pack watch flow ----

    private static PackPoint Kill(string key, string name, float x, float z, long at, bool done = false, string condition = "any time") =>
        new() { Key = key, Name = name, BnpcId = 9000, Anchor = new Vector3(x, 0, z), Condition = condition, DiedAtMs = at, AlreadyDone = done };

    [Fact]
    public void PackWatchSaysWhereToStandWaitAndWhenAllIsGood()
    {
        var watch = new PackWatch();
        watch.Killed(Kill("a", "Val Keeper", 0, 0, 0));
        watch.Killed(Kill("b", "Yukinko", 40, 0, 0));
        watch.Killed(Kill("c", "Yukinko", 20, 30, 0));
        watch.Killed(Kill("d", "Old point", 10, 10, 0, done: true));

        var far = watch.Status(new Vector3(200, 0, 0), 60, 1000, _ => false, 40);
        Assert.StartsWith("Pack watch: in range of 0/3, move ", far.Line);
        Assert.EndsWith(" W", far.Line);
        Assert.True(far.HasSpot);

        var inside = watch.Status(new Vector3(20, 0, 10), 60, 1000, _ => false, 40);
        Assert.Equal("In range of all 3, waiting: next ~40 s", inside.Line);
        Assert.True(inside.AllInRange);

        watch.Respawned("a", 50_000, 10, 60);
        watch.Respawned("b", 50_000, 10, 60);
        watch.Respawned("c", 50_000, 90, 60);
        var done = watch.Status(new Vector3(20, 0, 10), 60, 60_000, p => p.Key != "c", -1);
        Assert.True(done.Done);
        Assert.Equal("All good: 3 of 3 respawned, 2 confirmed, 1 already done.", done.Line);
        Assert.Equal("1 missed: Yukinko", done.Detail);

        var clean = watch.Status(new Vector3(20, 0, 10), 60, 60_000, _ => true, -1);
        Assert.Equal("All good: 3 of 3 respawned, 3 confirmed, 1 already done. Move on.", clean.Line);
        Assert.Equal("", clean.Detail);
    }

    [Fact]
    public void AMissedRespawnWaitsForTheConfirmationGrace()
    {
        var watch = new PackWatch();
        watch.Killed(Kill("a", "Val Keeper", 0, 0, 0));
        watch.Respawned("a", 10_000, 10, 60);
        Assert.Equal("", watch.Status(Vector3.Zero, 60, 11_000, _ => false, -1).Detail);
        Assert.Equal("1 missed: Val Keeper", watch.Status(Vector3.Zero, 60, 14_000, _ => false, -1).Detail);
    }

    [Fact]
    public void PackWatchSaysWhenNoSpotCoversAll()
    {
        var watch = new PackWatch();
        watch.Killed(Kill("a", "Anubys", 0, 0, 0));
        watch.Killed(Kill("b", "Wildebeest", 140, 0, 0));
        watch.Killed(Kill("c", "Fluturini", 10, 5, 0));
        var status = watch.Status(Vector3.Zero, 60, 1000, _ => false, 10);
        // Standing at Anubys: inside the area of Anubys and Fluturini, so no walk advice.
        Assert.True(status.HasSpot);
        Assert.True(status.InArea);
        Assert.Equal("Pack watch: no spot covers all 3, area covers 2, left out: Wildebeest", status.Line);
        Assert.Equal(new[] { new Vector2(0, 0), new Vector2(10, 5) }, watch.AreaXz);
    }

    [Fact]
    public void PointsLeaveAfterFiveMinutesOrWhenTheirConditionEnds()
    {
        var watch = new PackWatch();
        watch.Killed(Kill("a", "Zombie Brobinyak", 0, 0, 0, condition: "night"));
        watch.Killed(Kill("b", "Val Keeper", 10, 0, 0));
        watch.Expire(10_000, p => p.Condition != "night");
        Assert.Equal(PackOutcome.ConditionEnded, watch.Points[0].Outcome);
        Assert.Equal(1, watch.PendingCount);
        watch.Expire(PackWatch.KeepMs + 1, _ => true);
        Assert.Equal(PackOutcome.TimedOut, watch.Points[1].Outcome);
        var status = watch.Status(Vector3.Zero, 60, PackWatch.KeepMs + 1, _ => false, -1);
        Assert.Equal("All good: 0 of 2 respawned, 0 confirmed, 0 already done. Move on.", status.Line);
        Assert.Equal("dropped: Zombie Brobinyak (day), Val Keeper (5 min)", status.Detail);
    }

    [Fact]
    public void AKillAfterAFinishedPackStartsANewOne()
    {
        var watch = new PackWatch();
        watch.Killed(Kill("a", "A", 0, 0, 0));
        watch.Respawned("a", 1000, 0, 60);
        watch.Killed(Kill("b", "B", 0, 0, 2000));
        Assert.Single(watch.Points);
        Assert.Equal("b", watch.Points[0].Key);
    }

    [Fact]
    public void TheSessionAddsOwnKillsToThePackAndSeesTheRespawn()
    {
        var session = new CollectorSession { PackEnabled = true, PackRadius = 60 };
        session.SetSnapshot(new CoverageSnapshot(Pagos, "x", [Row("layout:7", 9000, 7, 100, 200), Row("layout:8", 9000, 8, 130, 200)]));
        var first = Guid.NewGuid();
        var other = Guid.NewGuid();
        session.BeginScan(1000, new Vector3(110, 10, 200));
        session.Seen(first, 9000, 7, new Vector3(100, 10, 200), new Vector3(100, 10, 200), new Vector3(100, 10, 200), false, 0);
        session.Seen(other, 9000, 8, new Vector3(130, 10, 200), new Vector3(130, 10, 200), new Vector3(130, 10, 200), false, 0);
        session.EndScan();
        session.BeginScan(1500, new Vector3(110, 10, 200));
        session.Died(first, 9000, 7, new Vector3(100, 10, 200), new Vector3(100, 10, 200), 0);
        session.Died(other, 9000, 8, new Vector3(130, 10, 200), new Vector3(130, 10, 200), 0);
        session.DefeatLinked(first, 1); // your kill
        Assert.Single(session.Pack.Points); // an observed death (no defeat link) is not part of the pack
        Assert.Equal("layout:7", session.Pack.Points[0].Key);

        session.BeginScan(60_000, new Vector3(110, 10, 200));
        session.Seen(Guid.NewGuid(), 9000, 7, new Vector3(100, 10, 200), new Vector3(100, 10, 200), new Vector3(100, 10, 200), false, 0);
        Assert.Equal(PackOutcome.Respawned, session.Pack.Points[0].Outcome);
        Assert.False(session.Pack.Points[0].OutOfRangeAtRespawn);
    }

    [Fact]
    public void APulledEnemyKilledFortyYalmsFromItsHomeGetsItsRingAtTheSpawnNotTheCorpse()
    {
        var session = new CollectorSession { PackEnabled = true, PackRadius = 60 };
        session.SetSnapshot(new CoverageSnapshot(Pagos, "x", [Row("layout:7", 9000, 7, 100, 200)]));
        var matched = Guid.NewGuid();
        var unmatched = Guid.NewGuid();
        var home = new Vector3(100, 10, 200);
        var otherHome = new Vector3(300, 12, 200);
        session.BeginScan(1000, new Vector3(110, 10, 200));
        session.Seen(matched, 9000, 7, home, home, home, false, 0);
        session.Seen(unmatched, 9100, 0, otherHome, otherHome, otherHome, false, 0);
        session.EndScan();
        // Both pulled 40 y and killed there.
        session.BeginScan(1500, new Vector3(140, 10, 200));
        session.Seen(matched, 9000, 7, home, home, new Vector3(140, 10, 200), true, 0);
        session.Seen(unmatched, 9100, 0, otherHome, otherHome, new Vector3(340, 12, 200), true, 0);
        session.BeginScan(2000, new Vector3(140, 10, 200));
        session.Died(matched, 9000, 7, home, new Vector3(140, 10, 200), 0);
        session.Died(unmatched, 9100, 0, otherHome, new Vector3(340, 12, 200), 0);
        session.DefeatLinked(matched, 1);
        session.DefeatLinked(unmatched, 1);

        Assert.Equal(2, session.Pack.Points.Count);
        var row = session.Pack.Points[0];
        Assert.Equal("layout:7", row.Key);
        Assert.Equal(new Vector3(100, 10, 200), row.Anchor);
        Assert.Equal(AnchorSource.Row, row.Source);
        Assert.False(row.Approximate);
        var byHome = session.Pack.Points[1];
        Assert.Equal(otherHome, byHome.Anchor);
        Assert.Equal(AnchorSource.Home, byHome.Source);
        Assert.False(byHome.Approximate);
        // The countdown (box 2) uses the same anchors.
        Assert.Contains(session.Deaths, d => d.TrackId == matched && d.Anchor == row.Anchor);
        Assert.Contains(session.Deaths, d => d.TrackId == unmatched && d.Anchor == otherHome);

        // The best spot and the safe area come from the spawn spots: the midpoint of the two, 100 y from each.
        var status = session.Pack.Status(new Vector3(200, 10, 200), 120, 2500, _ => false, -1);
        Assert.True(status.HasSpot);
        Assert.Equal(200f, status.Spot.X, 2);
        Assert.Equal(200f, status.Spot.Y, 2);
        Assert.Equal("", status.Detail);
    }

    [Fact]
    public void WithNoRowAndNoHomeThePackPointIsTheDeathSpotFlaggedApproximate()
    {
        var session = new CollectorSession { PackEnabled = true, PackRadius = 60 };
        session.SetSnapshot(new CoverageSnapshot(Pagos, "x", []));
        var track = Guid.NewGuid();
        session.BeginScan(1000, new Vector3(110, 10, 200));
        session.Seen(track, 9100, 0, null, new Vector3(100, 10, 200), new Vector3(100, 10, 200), false, 0);
        session.BeginScan(1500, new Vector3(110, 10, 200));
        session.Died(track, 9100, 0, null, new Vector3(130, 10, 200), 0);
        session.DefeatLinked(track, 2); // a party kill

        var point = Assert.Single(session.Pack.Points);
        Assert.Equal(new Vector3(130, 10, 200), point.Anchor);
        Assert.Equal(AnchorSource.DeathSpot, point.Source);
        Assert.True(point.Approximate);
        var status = session.Pack.Status(new Vector3(110, 10, 200), 60, 2000, _ => false, -1);
        Assert.Equal("1 approximate (death spot)", status.Detail);

        // Its respawn (linked to the dead track) still clears it.
        session.BeginScan(60_000, new Vector3(110, 10, 200));
        session.Evidence(new Observation
        {
            Kind = ObservationKind.EnemyAppeared, EnemyTrackId = Guid.NewGuid(), GameId = 9100,
            Respawn = new EnemyRespawn { PreviousTrackId = track, MaxPlayerDistance = 20f }
        });
        Assert.Equal(PackOutcome.Respawned, session.Pack.Points[0].Outcome);
    }

    [Fact]
    public void ThePackIsOffWhenPackWatchIsOff()
    {
        var session = new CollectorSession();
        session.SetSnapshot(new CoverageSnapshot(Pagos, "x", [Row("layout:7", 9000, 7, 100, 200)]));
        var track = Guid.NewGuid();
        session.BeginScan(1000, new Vector3(110, 10, 200));
        session.Seen(track, 9000, 7, new Vector3(100, 10, 200), new Vector3(100, 10, 200), new Vector3(100, 10, 200), false, 0);
        session.Died(track, 9000, 7, new Vector3(100, 10, 200), new Vector3(100, 10, 200), 0);
        session.DefeatLinked(track, 1);
        Assert.Empty(session.Pack.Points);
    }

    // ---- settings and the edge arrow ----

    [Fact]
    public void PackWatchIsOffByDefaultWithASixtyYalmRadius()
    {
        var configuration = new PluginConfiguration();
        Assert.False(configuration.OverlayPackWatch);
        Assert.False(configuration.OverlayRings);
        Assert.Equal(60f, configuration.OverlayPackRadius);
        configuration.OverlayEnabled = true;
        configuration.OverlayPackRadius = 200;
        Assert.Equal(80f, configuration.OverlayFeatures.PackRadius);
        configuration.OverlayPackRadius = 10;
        Assert.Equal(50f, configuration.OverlayFeatures.PackRadius);
        configuration.OverlayPackWatch = true;
        Assert.True(configuration.OverlayFeatures.NeedsScan);
        Assert.True(configuration.OverlayFeatures.DrawsWorld);
    }

    [Fact]
    public void ThePointerNeedsTheNearbyList()
    {
        var configuration = new PluginConfiguration { OverlayEnabled = true };
        Assert.True(configuration.OverlayPointer);
        Assert.False(configuration.OverlayFeatures.Pointer);
        configuration.OverlayNearbyList = true;
        Assert.True(configuration.OverlayFeatures.Pointer);
        configuration.OverlayEnabled = false;
        Assert.False(configuration.OverlayFeatures.Pointer);
    }

    [Theory]
    [InlineData(1f, 0f, 1850f, 540f)]
    [InlineData(0f, -1f, 960f, 70f)]
    [InlineData(-1f, -1f, 490f, 70f)]
    public void TheEdgeArrowSitsOnTheInsetScreenEdge(float dx, float dy, float x, float y)
    {
        var at = ScreenEdge.Point(new Vector2(1920, 1080), new Vector2(dx, dy), 70);
        Assert.Equal(x, at.X, 1);
        Assert.Equal(y, at.Y, 1);
    }

    private static CoveragePoint Row(string key, uint bnpc, uint layout, float x, float z, string condition = "any time",
        CoverageStatus status = CoverageStatus.ToConfirm) => new()
    {
        Key = key, BnpcId = bnpc, LayoutId = layout, X = x, Y = 10, Z = z, Status = status, HeightKnown = true,
        Condition = condition, Name = "Enemy"
    };
}

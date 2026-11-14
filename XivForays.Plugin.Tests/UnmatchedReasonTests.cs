using System.Numerics;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>An enemy without a coverage row says why it did not match, so the player knows what (if anything) helps.</summary>
public class UnmatchedReasonTests
{
    private const uint Pagos = ForayZones.EurekaPagos;

    private static CoveragePoint Point(string key, uint bnpc, uint layout, float x, float z,
        CoverageStatus status = CoverageStatus.ToConfirm) => new()
    {
        Key = key, BnpcId = bnpc, LayoutId = layout, X = x, Y = 10, Z = z, Status = status, HeightKnown = true
    };

    private static CoverageSnapshot Snapshot(params CoveragePoint[] points) => new(Pagos, "2026-11-13T21:00:00Z", points);

    [Fact]
    public void ANewSpotOfAKnownLayoutIsNewSpotNotNone()
    {
        // One layout reused at several far spots (a roaming spawner): the anchor is near none of them.
        var snapshot = Snapshot(Point("s1", 9037, 7422775, -174, 288), Point("s2", 9037, 7422775, -881, 155));
        var match = CoverageMatcher.Match(snapshot, 9037, 7422775, 450, 211);
        Assert.Null(match.Point);
        Assert.Equal(MatchKind.NewSpot, match.Kind);
        Assert.Equal(UnmatchedText.NewSpot, UnmatchedText.For(match.Kind, 7422775, false));
    }

    [Fact]
    public void TheOnlyRowOfALayoutHeldByAnotherEnemyIsTaken()
    {
        var snapshot = Snapshot(Point("p", 9112, 7448900, 0, 0));
        var match = CoverageMatcher.Match(snapshot, 9112, 7448900, 1, 0, new HashSet<string> { "p" });
        Assert.Null(match.Point);
        Assert.Equal(MatchKind.Taken, match.Kind);
        Assert.Equal(UnmatchedText.InUse, UnmatchedText.For(match.Kind, 7448900, true));
    }

    [Fact]
    public void AnUnknownLayoutWithNoNearbyRowIsNotOnTheMapYet()
    {
        var match = CoverageMatcher.Match(Snapshot(Point("p", 8000, 7000001, 0, 0)), 8000, 7000002, 50, 50);
        Assert.Null(match.Point);
        Assert.Equal(MatchKind.None, match.Kind);
        Assert.Equal(UnmatchedText.NotOnMap, UnmatchedText.For(match.Kind, 7000002, false));
        // Once its spawn was watched (a provisional local confirmation): the next refresh adds it.
        Assert.Equal(UnmatchedText.WatchedNew, UnmatchedText.For(match.Kind, 7000002, false, watchedNew: true));
    }

    [Theory]
    [InlineData((byte)MatchKind.None, 0u, true, UnmatchedText.FateEnemy)]
    [InlineData((byte)MatchKind.None, 0u, false, UnmatchedText.NoLayout)]
    [InlineData((byte)MatchKind.Ambiguous, 0u, true, UnmatchedText.Ambiguous)]
    [InlineData((byte)MatchKind.Ambiguous, 7000001u, false, UnmatchedText.Ambiguous)]
    [InlineData((byte)MatchKind.BnpcMismatch, 7000001u, false, UnmatchedText.OtherForm)]
    public void TheReasonFollowsTheMatchKindAndTheEnemy(byte kindValue, uint layout, bool fateBound, string expected)
    {
        var kind = (MatchKind)kindValue;
        Assert.Equal(expected, UnmatchedText.For(kind, layout, fateBound));
        // A watched spawn never hides these reasons: without a layout id or with another form the server cannot use it.
        Assert.Equal(expected, UnmatchedText.For(kind, layout, fateBound, watchedNew: true));
    }

    [Fact]
    public void AFateEnemyWithoutLayoutKeepsItsReasonOnTheDeath()
    {
        var session = new CollectorSession();
        session.SetSnapshot(Snapshot(Point("p", 8000, 7000001, 0, 0)));
        var track = Guid.NewGuid();
        var home = new Vector3(300, 5, 300);
        session.BeginScan(0, Vector3.Zero);
        session.Seen(track, 9067, 0, home, home, home, false, 1352);
        session.EndScan();
        Assert.Null(session.Live[track].Match.Point);
        Assert.Equal(UnmatchedText.FateEnemy, UnmatchedText.For(session.Live[track].Match.Kind, 0, true));
        session.Died(track, 9067, 0, home, home, 1352);
        var death = Assert.Single(session.Deaths);
        Assert.Null(death.PointKey);
        Assert.Equal(UnmatchedText.FateEnemy, UnmatchedText.For(death.MatchKind, death.LayoutId, death.FateBound));
    }

    [Fact]
    public void AWatchedSpawnOfAnUnmappedLayoutIsRememberedPerTrackUntilTheServerAgrees()
    {
        var session = new CollectorSession();
        session.SetSnapshot(Snapshot(Point("other", 8000, 7000001, 0, 0)));
        var track = Guid.NewGuid();
        var home = new Vector3(100, 10, 200);
        session.BeginScan(0, Vector3.Zero);
        session.Seen(track, 8000, 7000009, home, home, home, false, 0);
        var appeared = new Observation
        {
            Kind = ObservationKind.EnemyAppeared, TerritoryId = Pagos, EnemyTrackId = track, GameId = 8000,
            LayoutId = 7000009, HomePosition = [100f, 10f, 200f], SeenSpawning = true
        };
        var confirmation = session.Evidence(appeared);
        Assert.NotNull(confirmation);
        Assert.True(confirmation!.Provisional);
        Assert.True(session.WatchedNewPoint(track));
        Assert.False(session.WatchedNewPoint(Guid.NewGuid()));
        Assert.Equal(MatchKind.None, session.Live[track].Match.Kind);

        // The refresh adds the point as Confirmed: the provisional confirmation and the flag go.
        session.SetSnapshot(Snapshot(Point("other", 8000, 7000001, 0, 0),
            Point("new", 8000, 7000009, 100, 200, CoverageStatus.Confirmed)));
        Assert.False(session.WatchedNewPoint(track));
        Assert.Empty(session.Confirmations);
    }

    [Fact]
    public void AWatchedSpawnWithoutLayoutIsNotAWatchedNewPoint()
    {
        var session = new CollectorSession();
        session.SetSnapshot(Snapshot());
        var track = Guid.NewGuid();
        var home = new Vector3(100, 10, 200);
        session.BeginScan(0, Vector3.Zero);
        session.Seen(track, 9067, 0, home, home, home, false, 1352);
        session.Evidence(new Observation
        {
            Kind = ObservationKind.EnemyAppeared, TerritoryId = Pagos, EnemyTrackId = track, GameId = 9067,
            HomePosition = [100f, 10f, 200f], SeenSpawning = true
        });
        Assert.False(session.WatchedNewPoint(track));
        session.Clear();
        Assert.False(session.WatchedNewPoint(track));
    }
}

using System.Collections.Generic;
using System.Numerics;
using XivMate.DataGathering.Forays.Dalamud.Core;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>Pack watch draws one walkable area: the intersection of the waiting points' circles.</summary>
public class PackAreaTests
{
    private const float R = 60f;

    private static Vector2[] Outline(List<Vector2> points, out bool ok, out Vector2 inside)
    {
        var outline = new Vector2[64];
        ok = PackGeometry.IntersectionOutline(points, R, outline, out inside);
        return outline;
    }

    /// <summary>Every outline point is within the radius of every circle and on the edge of at least one.</summary>
    private static void AssertOnBoundary(List<Vector2> points, Vector2[] outline)
    {
        foreach (var edge in outline)
        {
            Assert.True(PackGeometry.InAll(points, edge, R + 0.01f));
            var onOne = false;
            foreach (var p in points)
                onOne |= System.MathF.Abs(Vector2.Distance(p, edge) - R) < 0.05f;
            Assert.True(onOne);
        }
    }

    [Fact]
    public void OneCircleIsTheWholeCircle()
    {
        List<Vector2> points = [new(100, 200)];
        var outline = Outline(points, out var ok, out var inside);
        Assert.True(ok);
        Assert.Equal(new Vector2(100, 200), inside);
        foreach (var edge in outline)
            Assert.Equal(R, Vector2.Distance(edge, points[0]), 2);
    }

    [Fact]
    public void TwoCirclesMakeALens()
    {
        List<Vector2> points = [new(0, 0), new(80, 0)];
        var outline = Outline(points, out var ok, out var inside);
        Assert.True(ok);
        Assert.Equal(new Vector2(40, 0), inside);
        AssertOnBoundary(points, outline);
        // The lens is 40 y wide along the line (20..60) and 2*sqrt(60^2-40^2) across.
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = 0f;
        foreach (var e in outline)
        {
            minX = System.MathF.Min(minX, e.X);
            maxX = System.MathF.Max(maxX, e.X);
            maxY = System.MathF.Max(maxY, System.MathF.Abs(e.Y));
        }

        Assert.Equal(20f, minX, 1);
        Assert.Equal(60f, maxX, 1);
        Assert.InRange(maxY, 44.0f, 44.8f); // sqrt(2000) = 44.7, sampled
    }

    [Fact]
    public void ManyCirclesStayInsideAll()
    {
        List<Vector2> points = [new(0, 0), new(40, 0), new(20, 30), new(5, 25), new(35, 20)];
        var outline = Outline(points, out var ok, out var inside);
        Assert.True(ok);
        Assert.True(PackGeometry.InAll(points, inside, R));
        AssertOnBoundary(points, outline);
    }

    [Fact]
    public void NestedAndCoincidentCirclesLeaveTheAreaUnchanged()
    {
        List<Vector2> two = [new(0, 0), new(80, 0)];
        var lens = Outline(two, out _, out _);

        // The same spot twice, and a third circle that holds the whole lens (it does not cut it).
        List<Vector2> nested = [new(0, 0), new(80, 0), new(0, 0), new(40, 5)];
        var outline = Outline(nested, out var ok, out _);
        Assert.True(ok);
        for (var i = 0; i < lens.Length; i++)
            Assert.True(Vector2.Distance(lens[i], outline[i]) < 0.05f);

        // All points at one spot: the full circle.
        List<Vector2> same = [new(5, 5), new(5, 5), new(5, 5)];
        var circle = Outline(same, out ok, out _);
        Assert.True(ok);
        foreach (var edge in circle)
            Assert.Equal(R, Vector2.Distance(edge, new Vector2(5, 5)), 2);
    }

    [Fact]
    public void AnEmptyIntersectionGivesNoOutline()
    {
        List<Vector2> points = [new(0, 0), new(121, 0)];
        var outline = new Vector2[64];
        outline[0] = new Vector2(7, 7);
        Assert.False(PackGeometry.IntersectionOutline(points, R, outline, out _));
        Assert.Equal(new Vector2(7, 7), outline[0]); // untouched
        Assert.False(PackGeometry.IntersectionOutline([], R, outline, out _));

        // Pairwise overlapping but no common spot (a wide triangle).
        List<Vector2> triangle = [new(0, 0), new(110, 0), new(55, 95)];
        Assert.False(PackGeometry.IntersectionOutline(triangle, R, outline, out _));
    }

    [Fact]
    public void TouchingCirclesGiveOneSpot()
    {
        List<Vector2> points = [new(0, 0), new(120, 0)];
        var outline = Outline(points, out var ok, out var inside);
        Assert.True(ok);
        Assert.Equal(new Vector2(60, 0), inside);
        foreach (var edge in outline)
            Assert.True(Vector2.Distance(edge, inside) < 0.5f);
    }

    [Fact]
    public void BestCoverTakesTheMostPoints()
    {
        // Three close together, two far away (and far from each other).
        List<Vector2> points = [new(300, 0), new(0, 0), new(30, 10), new(15, 40), new(-300, 0)];
        var chosen = new List<int>();
        Assert.Equal(3, PackGeometry.BestCover(points, R, chosen));
        Assert.Equal(new[] { 1, 2, 3 }, chosen);

        // Two groups of two where a crossing point (not a point itself) is needed: 110 y apart pairs.
        List<Vector2> pairs = [new(0, 0), new(110, 0), new(500, 0), new(500, 200)];
        Assert.Equal(2, PackGeometry.BestCover(pairs, R, chosen));
        Assert.Equal(new[] { 0, 1 }, chosen);
        Assert.Equal(0, PackGeometry.BestCover([], R, chosen));
    }

    private static PackPoint Kill(string key, string name, float x, float z, string condition = "any time") =>
        new() { Key = key, Name = name, BnpcId = 9000, Anchor = new Vector3(x, 0, z), Condition = condition, DiedAtMs = 0 };

    [Fact]
    public void ThePackDrawsTheAreaOfAllWaitingPoints()
    {
        var watch = new PackWatch();
        watch.Killed(Kill("a", "A", 0, 0));
        watch.Killed(Kill("b", "B", 80, 0));
        var status = watch.Status(new Vector3(500, 0, 0), R, 1000, _ => false, -1);
        Assert.True(status.HasSpot);
        Assert.False(status.InArea);
        Assert.False(status.Partial);
        Assert.Equal(2, watch.AreaXz.Count);
        var inside = watch.Status(new Vector3(40, 0, 0), R, 1000, _ => false, -1);
        Assert.True(inside.InArea);
        Assert.True(inside.AllInRange);
    }

    [Fact]
    public void WithNoCommonSpotTheAreaCoversTheMostAndNamesTheRest()
    {
        var watch = new PackWatch();
        watch.Killed(Kill("a", "Anubys", 0, 0));
        watch.Killed(Kill("b", "Wildebeest", 300, 0));
        watch.Killed(Kill("c", "Fluturini", 30, 10));
        watch.Killed(Kill("d", "Val Bomb", 15, 40));
        watch.Killed(Kill("e", "Tiger", -300, 0));
        var status = watch.Status(new Vector3(0, 0, 200), R, 1000, _ => false, -1);
        Assert.True(status.HasSpot);
        Assert.True(status.Partial);
        Assert.False(status.InArea);
        Assert.StartsWith("Pack watch: no spot covers all 5, area covers 3, left out: Wildebeest, Tiger. Move ", status.Line);
        Assert.Equal(3, watch.AreaXz.Count);
        var outline = new Vector2[64];
        Assert.True(PackGeometry.IntersectionOutline(watch.AreaXz, R, outline, out var spot));
        Assert.Equal(status.Spot, spot);
    }

    [Fact]
    public void AConditionEndDropsAtOnceIsNamedOnceAndIsNeverMissed()
    {
        var watch = new PackWatch();
        watch.Killed(Kill("a", "Ashkin", 0, 0, "night"));
        watch.Killed(Kill("b", "Ashkin", 10, 0, "night"));
        watch.Killed(Kill("c", "Sprite", 20, 0, "weather Gales"));
        watch.Killed(Kill("d", "Val Keeper", 5, 5));

        // Day comes and the weather turns on the first tick: dropped at once.
        watch.Expire(2000, p => p.Condition == "any time", p => PackWatch.EndReason(p.Condition, false));
        Assert.Equal(1, watch.PendingCount);
        Assert.All(watch.Points, p => Assert.True(p.Key == "d" ? p.Outcome == PackOutcome.Pending : p.Outcome == PackOutcome.ConditionEnded));
        var waiting = watch.Status(new Vector3(5, 0, 5), R, 2000, _ => false, -1);
        Assert.Equal("dropped: Ashkin x2 (day), Sprite (weather over)", waiting.Detail);
        Assert.Equal(1, waiting.Pending);

        // Long after (past the confirmation grace) the dropped points are still not missed.
        watch.Respawned("d", 50_000, 0, R);
        var done = watch.Status(new Vector3(5, 0, 5), R, 60_000, _ => true, -1);
        Assert.True(done.Done);
        Assert.Equal("All good: 1 of 4 respawned, 1 confirmed, 0 already done. Move on.", done.Line);
        Assert.DoesNotContain("missed", done.Detail);
        Assert.Equal("dropped: Ashkin x2 (day), Sprite (weather over)", done.Detail);

        // A later respawn of a dropped point does not bring it back or count it.
        watch.Respawned("a", 61_000, 500, R);
        Assert.Equal(PackOutcome.ConditionEnded, watch.Points[0].Outcome);
    }

    [Fact]
    public void EndReasonWords()
    {
        Assert.Equal("day", PackWatch.EndReason("night", false));
        Assert.Equal("day", PackWatch.EndReason("night", null));
        Assert.Equal("night", PackWatch.EndReason("day", true));
        Assert.Equal("weather over", PackWatch.EndReason("weather Gales", true));
        Assert.Equal("weather over", PackWatch.EndReason("night + weather Snow", true));
        Assert.Equal("day", PackWatch.EndReason("night + weather Snow", false));
        Assert.Equal("FATE over", PackWatch.EndReason("during its FATE (Wail of the Dead)", null));
        Assert.Equal("condition over", PackWatch.EndReason("", null));
    }

    [Fact]
    public void PackRingsAreOffByDefault()
    {
        var configuration = new XivMate.DataGathering.Forays.Dalamud.Configuration.Configuration
        {
            OverlayEnabled = true,
            OverlayPackWatch = true
        };
        Assert.False(configuration.OverlayPackRings);
        Assert.True(configuration.OverlayFeatures.PackWatch);
        Assert.False(configuration.OverlayFeatures.PackRings);
        configuration.OverlayPackRings = true;
        Assert.True(configuration.OverlayFeatures.PackRings);
        configuration.OverlayPackWatch = false;
        Assert.False(configuration.OverlayFeatures.PackRings);
    }
}

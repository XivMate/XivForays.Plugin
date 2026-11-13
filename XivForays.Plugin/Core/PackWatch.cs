using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

// Collector overlay "Pack watch": the maintainer's workflow of killing a pack, standing where every spawn point of
// the pack is in range, and waiting until all of them have respawned. Dependency-free so it can be unit tested.

/// <summary>Horizontal (x/z) geometry of the pack: enclosing circle, the common safe area and the way into it.</summary>
internal static class PackGeometry
{
    /// <summary>
    /// The minimum enclosing circle of the points (incremental Welzl, exact for the small sets used here). Points are
    /// x/z pairs. An empty set gives radius -1.
    /// </summary>
    public static (Vector2 Centre, float Radius) EnclosingCircle(IReadOnlyList<Vector2> points)
    {
        if (points.Count == 0)
            return (Vector2.Zero, -1f);
        var c = points[0];
        var r = 0f;
        for (var i = 1; i < points.Count; i++)
        {
            if (Inside(points[i], c, r))
                continue;
            c = points[i];
            r = 0f;
            for (var j = 0; j < i; j++)
            {
                if (Inside(points[j], c, r))
                    continue;
                c = (points[i] + points[j]) / 2f;
                r = Vector2.Distance(points[i], points[j]) / 2f;
                for (var k = 0; k < j; k++)
                {
                    if (Inside(points[k], c, r))
                        continue;
                    (c, r) = Circumcircle(points[i], points[j], points[k]);
                }
            }
        }

        return (c, r);
    }

    private static bool Inside(Vector2 p, Vector2 c, float r) => Vector2.Distance(p, c) <= r + 1e-3f;

    private static (Vector2, float) Circumcircle(Vector2 a, Vector2 b, Vector2 c)
    {
        var d = 2f * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (MathF.Abs(d) < 1e-6f)
        {
            // Collinear: the widest pair decides.
            var ab = Vector2.Distance(a, b);
            var ac = Vector2.Distance(a, c);
            var bc = Vector2.Distance(b, c);
            return ab >= ac && ab >= bc ? ((a + b) / 2f, ab / 2f) : ac >= bc ? ((a + c) / 2f, ac / 2f) : ((b + c) / 2f, bc / 2f);
        }

        var a2 = a.LengthSquared();
        var b2 = b.LengthSquared();
        var c2 = c.LengthSquared();
        var centre = new Vector2(
            (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d,
            (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
        return (centre, MathF.Max(Vector2.Distance(centre, a), MathF.Max(Vector2.Distance(centre, b), Vector2.Distance(centre, c))));
    }

    /// <summary>True when one spot is within the radius of every point (the circles intersect).</summary>
    public static bool Coverable(IReadOnlyList<Vector2> points, float radius) =>
        points.Count > 0 && EnclosingCircle(points).Radius <= radius;

    public static bool InAll(IReadOnlyList<Vector2> points, Vector2 at, float radius)
    {
        foreach (var p in points)
            if (Vector2.Distance(p, at) > radius + 1e-3f)
                return false;
        return true;
    }

    /// <summary>
    /// How far from <paramref name="inside"/> (a spot inside the safe area) the edge of the area is in a direction
    /// (unit vector): the nearest exit over all circles. The area is convex, so this traces its outline.
    /// </summary>
    public static float EdgeDistance(IReadOnlyList<Vector2> points, Vector2 inside, Vector2 direction, float radius)
    {
        var best = float.MaxValue;
        foreach (var p in points)
        {
            var w = p - inside;
            var along = Vector2.Dot(direction, w);
            var disc = radius * radius - w.LengthSquared() + along * along;
            var exit = disc <= 0 ? 0f : along + MathF.Sqrt(disc);
            best = MathF.Min(best, MathF.Max(0f, exit));
        }

        return best == float.MaxValue ? 0f : best;
    }

    /// <summary>Fills <paramref name="outline"/> with the safe area outline (as many points as the span holds).</summary>
    public static void Outline(IReadOnlyList<Vector2> points, Vector2 inside, float radius, Span<Vector2> outline)
    {
        for (var i = 0; i < outline.Length; i++)
        {
            var angle = i * MathF.Tau / outline.Length;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            outline[i] = inside + direction * EdgeDistance(points, inside, direction, radius);
        }
    }

    /// <summary>
    /// The walk from the player into the safe area, straight towards the best spot: 0 when already inside. The
    /// direction is the compass word of the walk.
    /// </summary>
    public static float MoveInto(IReadOnlyList<Vector2> points, Vector2 centre, float radius, Vector2 player)
    {
        if (InAll(points, player, radius))
            return 0f;
        var away = player - centre;
        var length = away.Length();
        if (length < 1e-3f)
            return 0f;
        var edge = EdgeDistance(points, centre, away / length, radius);
        return MathF.Max(0f, length - edge);
    }

    /// <summary>The two points furthest apart (indices), for the "cannot cover from one spot" advice.</summary>
    public static (int A, int B, float Distance) FarthestPair(IReadOnlyList<Vector2> points)
    {
        var best = (A: -1, B: -1, Distance: -1f);
        for (var i = 0; i < points.Count; i++)
        for (var j = i + 1; j < points.Count; j++)
        {
            var d = Vector2.Distance(points[i], points[j]);
            if (d > best.Distance)
                best = (i, j, d);
        }

        return best;
    }

    /// <summary>
    /// The outline of the intersection of the circles (radius <paramref name="radius"/> round each point): the area
    /// from where every point is within the radius. Convex, traced from the centre of the enclosing circle (which lies
    /// inside it whenever it is not empty). False and nothing written when the intersection is empty (or no points).
    /// Coincident points and circles that do not cut the area (nested) leave the outline unchanged.
    /// </summary>
    public static bool IntersectionOutline(IReadOnlyList<Vector2> points, float radius, Span<Vector2> outline, out Vector2 inside)
    {
        var (centre, mec) = EnclosingCircle(points);
        inside = centre;
        if (mec < 0 || mec > radius + 1e-3f)
            return false;
        Outline(points, centre, radius, outline);
        return true;
    }

    /// <summary>
    /// The largest set of points one spot can keep within the radius, for when no spot covers all of them. The best
    /// spot is at a point or where two circles cross, so only those are tried (no allocation: <paramref name="chosen"/>
    /// is refilled with the indices). Ties keep the earliest candidate. Returns the count.
    /// </summary>
    public static int BestCover(IReadOnlyList<Vector2> points, float radius, List<int> chosen)
    {
        chosen.Clear();
        if (points.Count == 0)
            return 0;
        var bestAt = points[0];
        var best = -1;
        for (var i = 0; i < points.Count; i++)
        {
            Try(points[i]);
            for (var j = i + 1; j < points.Count; j++)
            {
                var d = Vector2.Distance(points[i], points[j]);
                if (d > 2f * radius || d < 1e-4f)
                    continue;
                var mid = (points[i] + points[j]) / 2f;
                var h = MathF.Sqrt(MathF.Max(0f, radius * radius - d * d / 4f));
                var across = new Vector2(-(points[j].Y - points[i].Y), points[j].X - points[i].X) / d;
                Try(mid + across * h);
                Try(mid - across * h);
            }
        }

        for (var i = 0; i < points.Count; i++)
            if (Vector2.Distance(points[i], bestAt) <= radius + 1e-2f)
                chosen.Add(i);
        return chosen.Count;

        void Try(Vector2 at)
        {
            var n = 0;
            foreach (var p in points)
                if (Vector2.Distance(p, at) <= radius + 1e-2f)
                    n++;
            if (n > best)
            {
                best = n;
                bestAt = at;
            }
        }
    }
}

/// <summary>Why a pack point left the waiting set.</summary>
internal enum PackOutcome : byte
{
    Pending,
    Respawned,

    /// <summary>The point was already Confirmed when killed: not waited for, counted as "already done".</summary>
    AlreadyDone,
    ConditionEnded,
    TimedOut
}

/// <summary>One killed spawn point of the pack.</summary>
internal sealed class PackPoint
{
    public required string Key { get; init; }
    public string Name { get; init; } = "";
    public uint BnpcId { get; init; }
    /// <summary>Where it respawns: the matched row, else the enemy's home, else the death spot (<see cref="SpawnAnchor"/>).</summary>
    public Vector3 Anchor { get; init; }

    public bool HeightKnown { get; init; } = true;
    public AnchorSource Source { get; init; }

    /// <summary>A walk-past row or the death spot: drawn as a hollow ring.</summary>
    public bool Approximate => Source is AnchorSource.RowApproximate or AnchorSource.DeathSpot;

    public string Condition { get; init; } = "";

    /// <summary>The point was already Confirmed when it was killed: its respawn adds nothing.</summary>
    public bool AlreadyDone { get; init; }

    public long DiedAtMs { get; init; }
    public PackOutcome Outcome { get; set; }
    public long ResolvedAtMs { get; set; }

    /// <summary>The player was beyond the keep-within radius when it respawned.</summary>
    public bool OutOfRangeAtRespawn { get; set; }

    /// <summary>Why it was dropped ("day", "night", "weather over", "FATE over", "5 min"), "" while not dropped.</summary>
    public string DropReason { get; set; } = "";
}

/// <summary>The status of the pack for the window and the screen text.</summary>
internal readonly record struct PackStatus(
    string Line,
    string Detail,
    int Pending,
    bool HasSpot,
    Vector2 Spot,
    bool AllInRange,
    bool Done,
    bool InArea = false,
    bool Partial = false);

/// <summary>
/// The set of recently killed spawn points (own and party kills) waiting for their respawn. A point leaves the
/// waiting set when it respawns, when its condition ends, or after 5 minutes. A kill after the whole pack resolved
/// starts a new pack.
/// </summary>
internal sealed class PackWatch
{
    public const long KeepMs = 5 * 60 * 1000;

    /// <summary>A respawn without a local confirmation counts as missed only after this grace (the record can lag).</summary>
    public const long ConfirmGraceMs = 3000;

    public const int MaxPoints = 16;
    public const float DefaultRadius = 60f;
    public const float MinRadius = 50f;
    public const float MaxRadius = 80f;

    private readonly List<PackPoint> points = new();
    private readonly List<Vector2> scratch = new();
    private readonly List<PackPoint> scratchPoints = new();
    private readonly List<Vector2> areaXz = new();
    private readonly List<int> chosen = new();
    private readonly bool[] covered = new bool[MaxPoints];

    public IReadOnlyList<PackPoint> Points => points;

    public void Clear() => points.Clear();

    public int PendingCount
    {
        get
        {
            var n = 0;
            foreach (var p in points)
                if (p.Outcome == PackOutcome.Pending)
                    n++;
            return n;
        }
    }

    /// <summary>An own or party kill (a matched spawn point, or an unmatched kill keyed by its track).</summary>
    public void Killed(PackPoint point)
    {
        if (PendingCount == 0 && points.Exists(p => p.Outcome is PackOutcome.Respawned or PackOutcome.ConditionEnded or PackOutcome.TimedOut))
            points.Clear(); // the previous pack is finished: a new one starts
        points.RemoveAll(p => p.Key == point.Key);
        point.Outcome = point.AlreadyDone ? PackOutcome.AlreadyDone : PackOutcome.Pending;
        if (points.Count >= MaxPoints)
            points.RemoveAt(0);
        points.Add(point);
    }

    /// <summary>The point's enemy is up again.</summary>
    public void Respawned(string key, long nowMs, float playerDistance, float radius)
    {
        foreach (var p in points)
            if (p.Key == key && p.Outcome == PackOutcome.Pending)
            {
                p.Outcome = PackOutcome.Respawned;
                p.ResolvedAtMs = nowMs;
                p.OutOfRangeAtRespawn = playerDistance > radius;
            }
    }

    /// <summary>Drops points whose condition ended or that waited 5 minutes.</summary>
    public void Expire(long nowMs, Func<PackPoint, bool> conditionHolds) => Expire(nowMs, conditionHolds, null);

    /// <summary>
    /// Drops points whose condition ended (at once, on the first tick it no longer holds) or that waited 5 minutes. A
    /// dropped point is never counted as missed. <paramref name="reason"/> words why its condition ended (default from
    /// the condition text alone).
    /// </summary>
    public void Expire(long nowMs, Func<PackPoint, bool> conditionHolds, Func<PackPoint, string>? reason)
    {
        foreach (var p in points)
        {
            if (p.Outcome != PackOutcome.Pending)
                continue;
            if (nowMs - p.DiedAtMs > KeepMs)
            {
                p.Outcome = PackOutcome.TimedOut;
                p.ResolvedAtMs = nowMs;
                p.DropReason = "5 min";
            }
            else if (!conditionHolds(p))
            {
                p.Outcome = PackOutcome.ConditionEnded;
                p.ResolvedAtMs = nowMs;
                p.DropReason = reason?.Invoke(p) ?? EndReason(p.Condition, null);
            }
        }
    }

    /// <summary>
    /// Why a condition no longer holds, in a word: a night enemy at day says "day", a day enemy at night "night", a
    /// weather enemy "weather over", a FATE enemy "FATE over". <paramref name="nightNow"/> null: not known.
    /// </summary>
    public static string EndReason(string condition, bool? nightNow)
    {
        if (!SpawnCondition.TryParse(condition, out var parsed))
            return "condition over";
        if (parsed.Night is { } wantNight && (nightNow == null ? parsed.Weathers == null : nightNow != wantNight))
            return wantNight ? "day" : "night";
        if (parsed.Weathers != null)
            return "weather over";
        return parsed.Fate ? "FATE over" : "condition over";
    }

    /// <summary>
    /// The status lines. <paramref name="confirmed"/> says whether a respawned point is Confirmed now (locally or in
    /// the coverage); <paramref name="nextSeconds"/> is the soonest estimated respawn of the waiting points (-1 unknown).
    /// </summary>
    public PackStatus Status(Vector3 player, float radius, long nowMs, Func<PackPoint, bool> confirmed, int nextSeconds)
    {
        areaXz.Clear();
        if (points.Count == 0)
            return new PackStatus("", "", 0, false, default, false, false);
        scratch.Clear();
        scratchPoints.Clear();
        var inRange = 0;
        foreach (var p in points)
        {
            if (p.Outcome != PackOutcome.Pending)
                continue;
            scratch.Add(new Vector2(p.Anchor.X, p.Anchor.Z));
            scratchPoints.Add(p);
            if (DistanceTo(p, player) <= radius)
                inRange++;
        }

        var missed = new StringBuilder();
        var missedCount = 0;
        int respawned = 0, newlyConfirmed = 0, alreadyDone = 0, dropped = 0;
        foreach (var p in points)
        {
            switch (p.Outcome)
            {
                case PackOutcome.AlreadyDone:
                    alreadyDone++;
                    break;
                case PackOutcome.Respawned:
                    respawned++;
                    if (confirmed(p))
                        newlyConfirmed++;
                    else if (nowMs - p.ResolvedAtMs > ConfirmGraceMs)
                    {
                        missedCount++;
                        missed.Append(missed.Length == 0 ? "" : ", ").Append(NameOf(p));
                    }

                    break;
                case PackOutcome.ConditionEnded:
                case PackOutcome.TimedOut:
                    // Never missed: the point is out of the pack and named once in the dropped list.
                    dropped++;
                    break;
            }
        }

        var missedText = missedCount == 0 ? "" : missedCount.ToString(CultureInfo.InvariantCulture) + " missed: " + missed;
        if (dropped > 0)
            missedText += (missedText.Length > 0 ? " | " : "") + DroppedText();
        var pending = scratch.Count;
        var approximate = 0;
        var deathSpots = 0;
        foreach (var p in scratchPoints)
        {
            if (p.Approximate)
                approximate++;
            if (p.Source == AnchorSource.DeathSpot)
                deathSpots++;
        }

        if (approximate > 0)
            missedText += (missedText.Length > 0 ? " | " : "") + approximate.ToString(CultureInfo.InvariantCulture) +
                          (deathSpots == approximate ? " approximate (death spot)" : " approximate");
        var total = points.Count - alreadyDone;
        if (pending == 0)
        {
            if (total == 0)
                return new PackStatus("", "", 0, false, default, false, false);
            var line = "All good: " + respawned.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture) +
                       " respawned, " + newlyConfirmed.ToString(CultureInfo.InvariantCulture) + " confirmed, " +
                       alreadyDone.ToString(CultureInfo.InvariantCulture) + " already done." +
                       (missedCount == 0 ? " Move on." : "");
            return new PackStatus(line, missedText, 0, false, default, false, true);
        }

        var (centre, mec) = PackGeometry.EnclosingCircle(scratch);
        var playerXz = new Vector2(player.X, player.Z);
        if (mec > radius)
        {
            // No spot covers all: the area that covers the most of them, and which are left out.
            PackGeometry.BestCover(scratch, radius, chosen);
            Array.Clear(covered);
            foreach (var i in chosen)
            {
                covered[i] = true;
                areaXz.Add(scratch[i]);
            }

            var (spot, _) = PackGeometry.EnclosingCircle(areaXz);
            var left = new StringBuilder();
            var inChosen = 0;
            for (var i = 0; i < scratch.Count; i++)
            {
                if (!covered[i])
                    left.Append(left.Length == 0 ? "" : ", ").Append(NameOf(scratchPoints[i]));
                else if (DistanceTo(scratchPoints[i], player) <= radius)
                    inChosen++;
            }

            var inArea = inChosen == chosen.Count;
            var line = "Pack watch: no spot covers all " + pending.ToString(CultureInfo.InvariantCulture) + ", area covers " +
                       chosen.Count.ToString(CultureInfo.InvariantCulture) + ", left out: " + left;
            if (!inArea)
            {
                var move = PackGeometry.MoveInto(areaXz, spot, radius, playerXz);
                var to = spot - playerXz;
                line += ". Move " + MathF.Max(1f, MathF.Ceiling(move)).ToString("0", CultureInfo.InvariantCulture) + " y " +
                        Compass.Direction(to.X, to.Y);
            }

            return new PackStatus(line, missedText, pending, true, spot, false, false, inArea, true);
        }

        areaXz.AddRange(scratch);
        if (inRange < pending)
        {
            var move = PackGeometry.MoveInto(scratch, centre, radius, playerXz);
            var to = centre - playerXz;
            var line = "Pack watch: in range of " + inRange.ToString(CultureInfo.InvariantCulture) + "/" + pending.ToString(CultureInfo.InvariantCulture) +
                       ", move " + MathF.Max(1f, MathF.Ceiling(move)).ToString("0", CultureInfo.InvariantCulture) + " y " + Compass.Direction(to.X, to.Y);
            return new PackStatus(line, missedText, pending, true, centre, false, false);
        }

        var wait = nextSeconds < 0 ? "next unknown" : nextSeconds == 0 ? "next due now" : "next ~" + nextSeconds.ToString(CultureInfo.InvariantCulture) + " s";
        return new PackStatus("In range of all " + pending.ToString(CultureInfo.InvariantCulture) + ", waiting: " + wait, missedText,
            pending, true, centre, true, false, true);
    }

    /// <summary>"dropped: Ashkin (day), Val Keeper x2 (5 min)": each dropped name and reason once.</summary>
    private string DroppedText()
    {
        var text = new StringBuilder("dropped: ");
        var first = true;
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!IsDropped(p))
                continue;
            var seenBefore = false;
            for (var j = 0; j < i && !seenBefore; j++)
                seenBefore = SameDrop(points[j], p);
            if (seenBefore)
                continue;
            var count = 0;
            for (var j = i; j < points.Count; j++)
                if (SameDrop(points[j], p))
                    count++;
            text.Append(first ? "" : ", ").Append(NameOf(p));
            if (count > 1)
                text.Append(" x").Append(count.ToString(CultureInfo.InvariantCulture));
            text.Append(" (").Append(p.DropReason.Length > 0 ? p.DropReason : "condition over").Append(')');
            first = false;
        }

        return text.ToString();
    }

    private static bool IsDropped(PackPoint p) => p.Outcome is PackOutcome.ConditionEnded or PackOutcome.TimedOut;

    private static bool SameDrop(PackPoint a, PackPoint b) =>
        IsDropped(a) && IsDropped(b) && a.DropReason == b.DropReason && NameOf(a) == NameOf(b);

    /// <summary>3D distance when the point's height is trusted, else horizontal.</summary>
    public static float DistanceTo(PackPoint p, Vector3 player) =>
        p.HeightKnown ? Vector3.Distance(p.Anchor, player) : CoverageMatcher.DistanceXz(p.Anchor.X, p.Anchor.Z, player.X, player.Z);

    private static string NameOf(PackPoint p) => p.Name.Length > 0 ? p.Name : "Enemy " + p.BnpcId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The waiting points as x/z pairs (refilled by <see cref="Status"/>).</summary>
    public IReadOnlyList<Vector2> PendingXz => scratch;

    /// <summary>
    /// The points whose circles make the one drawn area (refilled by <see cref="Status"/>): all waiting points, or when
    /// no spot covers all of them the largest set one spot can cover.
    /// </summary>
    public IReadOnlyList<Vector2> AreaXz => areaXz;
}

/// <summary>Where the off-screen pointer arrow goes: on a rectangle inset from the screen edge, along a direction.</summary>
internal static class ScreenEdge
{
    public static Vector2 Point(Vector2 size, Vector2 direction, float margin)
    {
        var centre = size / 2f;
        if (direction.LengthSquared() < 1e-6f)
            return centre with { Y = size.Y - margin };
        var d = Vector2.Normalize(direction);
        var halfW = MathF.Max(1f, centre.X - margin);
        var halfH = MathF.Max(1f, centre.Y - margin);
        var tx = MathF.Abs(d.X) < 1e-6f ? float.MaxValue : halfW / MathF.Abs(d.X);
        var ty = MathF.Abs(d.Y) < 1e-6f ? float.MaxValue : halfH / MathF.Abs(d.Y);
        return centre + d * MathF.Min(tx, ty);
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

// Collector overlay (0.0.0.16) rules, dependency-free so they can be unit tested. Read-only helpers: nothing here
// changes what is captured or sent.

/// <summary>Distances and times of the collector overlay.</summary>
internal static class OverlayRates
{
    /// <summary>The watch radius of a seen spawn (same as <see cref="PoseRates.SpawningYalms"/>).</summary>
    public const float WatchYalms = PoseRates.SpawningYalms;

    /// <summary>The photo radius (same as <see cref="PoseRates.NearYalms"/>).</summary>
    public const float PhotoYalms = PoseRates.NearYalms;

    /// <summary>A watched respawn counts on the server only when the player stayed within this distance.</summary>
    public const float RespawnWatchYalms = 100f;

    /// <summary>Position fallback (x/z only) when no layout id decides the point: the exporter's exact match.</summary>
    public const float MatchYalms = 1.5f;

    /// <summary>Two candidates closer to each other than this (in their distance to the anchor) are a tie.</summary>
    public const float TieYalms = 0.1f;

    /// <summary>An approximate respawn estimate (historical median about 50 s). Shown as "~N s", never exact.</summary>
    public const int RespawnEstimateSeconds = 50;

    /// <summary>A death timer is forgotten this long after the death (the capture matcher keeps deaths 30 min).</summary>
    public const long DeathKeepMs = 10 * 60 * 1000;

    /// <summary>"Confirmed locally" is shown this long, then a small "upload pending" qualifier remains.</summary>
    public const long ConfirmFlashMs = 3000;

    public const float DefaultDisplayYalms = 100f;
    public const float MaxDisplayYalms = 150f;
    public const int DefaultMaxMarkers = 64;
    public const int MinListRows = 5;
    public const int MaxListRows = 10;
}

/// <summary>What the overlay may show from a local appearance record.</summary>
internal enum LocalEvidence : byte
{
    /// <summary>Not server-equivalent evidence (no home, not seen spawning, or a respawn watched from too far).</summary>
    None = 0,

    /// <summary>Server-equivalent: the point will be Confirmed once the upload is aggregated.</summary>
    Confirmed = 1,

    /// <summary>Watched cleanly, but the record has no layout id: the server match is uncertain.</summary>
    NoLayout = 2
}

/// <summary>
/// Local confirmation: only what the coverage exporter accepts counts. A kind 1 appearance with a valid home and
/// seenSpawning, or a respawn whose maxPlayerDistance is at most 100 y; a kind 15 / 16 pose with seenSpawning and a
/// home. A respawn association alone (watched from further away) is NOT a confirmation.
/// </summary>
internal static class LocalConfirmation
{
    public static LocalEvidence Evaluate(Observation record)
    {
        bool clean;
        float[]? home;
        uint? layout;
        switch (record.Kind)
        {
            case ObservationKind.EnemyAppeared:
                home = record.HomePosition;
                layout = record.LayoutId;
                clean = record.SeenSpawning == true ||
                        record.Respawn is { } respawn && float.IsFinite(respawn.MaxPlayerDistance) &&
                        respawn.MaxPlayerDistance >= 0 && respawn.MaxPlayerDistance <= OverlayRates.RespawnWatchYalms;
                break;
            case ObservationKind.EnemySpawnPose when record.Pose is { } pose:
                home = pose.HomePosition;
                layout = pose.LayoutId ?? record.LayoutId;
                clean = pose.SeenSpawning;
                break;
            case ObservationKind.EnemyFormChange when record.FormChange is { After: var after }:
                home = after.HomePosition;
                layout = after.LayoutId ?? record.LayoutId;
                clean = after.SeenSpawning;
                break;
            default:
                return LocalEvidence.None;
        }

        if (!clean || !ValidHome(home))
            return LocalEvidence.None;
        return layout is null or 0 ? LocalEvidence.NoLayout : LocalEvidence.Confirmed;
    }

    public static bool ValidHome(float[]? home) =>
        home is { Length: 3 } h && float.IsFinite(h[0]) && float.IsFinite(h[1]) && float.IsFinite(h[2]) &&
        (h[0] != 0 || h[1] != 0 || h[2] != 0);
}

/// <summary>How a live enemy was tied to a coverage row.</summary>
internal enum MatchKind : byte
{
    None = 0,
    /// <summary>The only row of its layout id (kept when the enemy walks away or is pulled).</summary>
    Layout = 1,
    /// <summary>One of several rows sharing its layout id, chosen by the spawn anchor (x/z within 1.5 y).</summary>
    LayoutPosition = 2,
    /// <summary>No layout row: same bnpcId and the nearest row within 1.5 y x/z of the spawn anchor.</summary>
    Position = 3,
    /// <summary>Two candidates tie: no row is chosen.</summary>
    Ambiguous = 4,
    /// <summary>The layout's rows are all another bnpcId: Unknown, never a confirmation.</summary>
    BnpcMismatch = 5
}

internal readonly record struct PointMatch(CoveragePoint? Point, MatchKind Kind)
{
    public static PointMatch NoMatch(MatchKind kind = MatchKind.None) => new(null, kind);
}

/// <summary>
/// Ties a live enemy to a coverage row: layout id first, then position (x/z only, height never decides). The anchor is
/// the spawn anchor (home position, else the first seen position), never the current (pulled) position, and the
/// caller binds the result for the track instead of rematching while the enemy moves.
/// </summary>
internal static class CoverageMatcher
{
    public static PointMatch Match(CoverageSnapshot snapshot, uint bnpcId, uint layoutId, float anchorX, float anchorZ,
        ISet<string>? claimed = null)
    {
        var layoutRows = snapshot.WithLayout(layoutId);
        if (layoutRows.Length > 0)
        {
            var same = 0;
            CoveragePoint? only = null;
            foreach (var row in layoutRows)
                if (row.BnpcId == bnpcId)
                {
                    same++;
                    only = row;
                }

            if (same == 0)
                return PointMatch.NoMatch(MatchKind.BnpcMismatch);
            if (same == 1 && layoutRows.Length == 1)
                return claimed != null && claimed.Contains(only!.Key) ? PointMatch.NoMatch() : new PointMatch(only, MatchKind.Layout);
            // One layout, several spots (FATE spawners, reused layouts): the anchor decides.
            var chosen = Nearest(layoutRows, bnpcId, anchorX, anchorZ, claimed, out var tie);
            if (tie)
                return PointMatch.NoMatch(MatchKind.Ambiguous);
            if (chosen != null)
                return new PointMatch(chosen, MatchKind.LayoutPosition);
            // A new spot of a known layout: do not recolour another spot of it.
            return PointMatch.NoMatch();
        }

        var near = Nearest(snapshot.WithBnpc(bnpcId), bnpcId, anchorX, anchorZ, claimed, out var tied);
        if (tied)
            return PointMatch.NoMatch(MatchKind.Ambiguous);
        return near == null ? PointMatch.NoMatch() : new PointMatch(near, MatchKind.Position);
    }

    private static CoveragePoint? Nearest(CoveragePoint[] rows, uint bnpcId, float x, float z, ISet<string>? claimed,
        out bool tie)
    {
        tie = false;
        CoveragePoint? best = null;
        var bestDistance = float.MaxValue;
        var secondDistance = float.MaxValue;
        foreach (var row in rows)
        {
            if (row.BnpcId != bnpcId || (claimed != null && claimed.Contains(row.Key)))
                continue;
            var distance = DistanceXz(row.X, row.Z, x, z);
            if (distance > OverlayRates.MatchYalms)
                continue;
            if (distance < bestDistance)
            {
                secondDistance = bestDistance;
                bestDistance = distance;
                best = row;
            }
            else if (distance < secondDistance)
            {
                secondDistance = distance;
            }
        }

        tie = best != null && secondDistance - bestDistance < OverlayRates.TieYalms;
        return tie ? null : best;
    }

    public static float DistanceXz(float ax, float az, float bx, float bz)
    {
        var dx = ax - bx;
        var dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>
/// The player's own positions of the last 6 s (one per 500 ms scan) in a fixed ring: no allocation per sample.
/// <see cref="StoodWithin"/> is the same test as <see cref="PlayerHistory.StoodWithin"/>, so "Ready to watch" means
/// what the capture means.
/// </summary>
internal sealed class WatchMeter
{
    private const int Capacity = 32;
    private const long KeepMs = 6000;
    private readonly long[] times = new long[Capacity];
    private readonly Vector3[] positions = new Vector3[Capacity];
    private int start;
    private int count;

    public void Clear()
    {
        start = 0;
        count = 0;
    }

    public void Add(long nowMs, Vector3 position)
    {
        while (count > 0 && nowMs - times[start] > KeepMs)
        {
            start = (start + 1) % Capacity;
            count--;
        }

        if (count == Capacity)
        {
            start = (start + 1) % Capacity;
            count--;
        }

        var index = (start + count) % Capacity;
        times[index] = nowMs;
        positions[index] = position;
        count++;
    }

    /// <summary>True when the samples cover the last 5 s (600 ms tolerance) and all of them were within the radius.</summary>
    public bool StoodWithin(Vector3 point, float radius, long nowMs)
    {
        if (count == 0 || nowMs - times[start] < PoseRates.StartWindowMs - 600)
            return false;
        for (var i = 0; i < count; i++)
        {
            var index = (start + i) % Capacity;
            if (nowMs - times[index] <= PoseRates.StartWindowMs && Vector3.Distance(point, positions[index]) > radius)
                return false;
        }

        return true;
    }

    /// <summary>How long (ms, at most 5000) the player has been within the radius without a break, for the meter.</summary>
    public long WithinMs(Vector3 point, float radius, long nowMs)
    {
        long since = -1;
        for (var i = count - 1; i >= 0; i--)
        {
            var index = (start + i) % Capacity;
            if (Vector3.Distance(point, positions[index]) > radius)
                break;
            since = times[index];
        }

        return since < 0 ? 0 : Math.Min(PoseRates.StartWindowMs, nowMs - since);
    }
}

/// <summary>The approximate respawn countdown text (cached strings, no allocation per frame).</summary>
internal static class RespawnText
{
    public const string Awaiting = "Awaiting spawn";
    private static readonly string[] Seconds = Build();

    /// <summary>Whole seconds left on the estimate (0 = due).</summary>
    public static int Remaining(long diedAtMs, long nowMs, int estimateSeconds = OverlayRates.RespawnEstimateSeconds)
    {
        var left = estimateSeconds * 1000L - (nowMs - diedAtMs);
        return left <= 0 ? 0 : (int)((left + 999) / 1000);
    }

    /// <summary>"~18 s" while counting, "Awaiting spawn" at zero. Never "Confirmed": a timer confirms nothing.</summary>
    public static string Text(int remainingSeconds) =>
        remainingSeconds <= 0 ? Awaiting : remainingSeconds < Seconds.Length ? Seconds[remainingSeconds] : "~" + remainingSeconds + " s";

    private static string[] Build()
    {
        var result = new string[121];
        for (var i = 0; i < result.Length; i++)
            result[i] = "~" + i + " s";
        return result;
    }
}

/// <summary>World compass directions (north = -Z, east = +X), so turning the camera never flips the meaning.</summary>
internal static class Compass
{
    private static readonly string[] Points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    public static string Direction(float dx, float dz)
    {
        if (dx == 0 && dz == 0)
            return "here";
        var degrees = MathF.Atan2(dx, -dz) * 180f / MathF.PI;
        if (degrees < 0)
            degrees += 360f;
        return Points[(int)MathF.Floor((degrees + 22.5f) / 45f) % 8];
    }
}

/// <summary>Range and readiness of the selected point (feature 3).</summary>
internal enum WatchAdvice : byte
{
    /// <summary>Within 80 y and the 5 s history is complete: a clean appearance would count.</summary>
    Ready,
    /// <summary>Within 80 y, the 5 s history is still filling.</summary>
    Filling,
    /// <summary>Beyond 80 y.</summary>
    MoveCloser
}

internal static class WatchRules
{
    public static WatchAdvice Advice(float distance, bool stoodWithin) =>
        distance > OverlayRates.WatchYalms ? WatchAdvice.MoveCloser : stoodWithin ? WatchAdvice.Ready : WatchAdvice.Filling;

    public static string Text(WatchAdvice advice) => advice switch
    {
        WatchAdvice.Ready => "Ready to watch",
        WatchAdvice.Filling => "Watching",
        _ => "Move closer"
    };

    /// <summary>A death's respawn watch is lost once the player went beyond 100 y of its anchor (server rule).</summary>
    public static bool ContinuityLost(float maxPlayerDistance) => maxPlayerDistance > OverlayRates.RespawnWatchYalms;
}

/// <summary>Whether a row's condition text can be met now (feature 8). Unknown never greys a point.</summary>
internal enum ConditionState : byte
{
    Unknown,
    Available,
    Unavailable
}

/// <summary>
/// Reads the coverage <c>condition</c> display text ("any time", "night", "weather Blizzard/Snow",
/// "during its FATE (1346)", parts joined by " + "). Weather is compared by English name like the exporter (lower
/// case, "fair skies" = fair, "heat waves" = heat, "blizzards" = blizzard). Anything not understood is Unknown.
/// </summary>
internal static class ConditionCheck
{
    public static ConditionState Evaluate(string condition, string? weatherName, bool? night, ICollection<uint>? activeFates)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return ConditionState.Unknown;
        var result = ConditionState.Available;
        foreach (var raw in condition.Split(" + "))
        {
            var part = raw.Trim();
            ConditionState state;
            if (part == "any time")
                state = ConditionState.Available;
            else if (part is "night" or "day")
                state = night is { } isNight ? (isNight == (part == "night") ? ConditionState.Available : ConditionState.Unavailable)
                    : ConditionState.Unknown;
            else if (part.StartsWith("weather ", StringComparison.Ordinal))
                state = Weather(part["weather ".Length..], weatherName);
            else if (part.StartsWith("during its FATE (", StringComparison.Ordinal) && part.EndsWith(')'))
                state = Fate(part["during its FATE (".Length..^1], activeFates);
            else
                state = ConditionState.Unknown;

            if (state == ConditionState.Unavailable)
                return ConditionState.Unavailable;
            if (state == ConditionState.Unknown)
                result = ConditionState.Unknown;
        }

        return result;
    }

    /// <summary>Eorzea night (the exporter's boundary): ET hour before 6 or from 18. One ET hour is 175 s.</summary>
    public static bool IsNight(long unixMs)
    {
        var hour = unixMs / 175_000 % 24;
        return hour is < 6 or >= 18;
    }

    public static string Normalize(string weather)
    {
        var w = weather.Trim().ToLowerInvariant();
        return w switch { "fair skies" => "fair", "heat waves" => "heat", "blizzards" => "blizzard", _ => w };
    }

    private static ConditionState Weather(string list, string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
            return ConditionState.Unknown;
        var now = Normalize(current);
        foreach (var option in list.Replace(',', '/').Split('/'))
            if (option.Trim().Length > 0 && Normalize(option) == now)
                return ConditionState.Available;
        return ConditionState.Unavailable;
    }

    private static ConditionState Fate(string ids, ICollection<uint>? active)
    {
        if (active == null)
            return ConditionState.Unknown;
        var any = false;
        foreach (var token in ids.Split(" or "))
        {
            if (!uint.TryParse(token.Trim(), out var id))
                return ConditionState.Unknown;
            any = true;
            if (active.Contains(id))
                return ConditionState.Available;
        }

        return any ? ConditionState.Unavailable : ConditionState.Unknown;
    }

    /// <summary>The short reason shown on a greyed point.</summary>
    public static string Reason(string condition) =>
        condition.Contains("FATE", StringComparison.Ordinal) ? "FATE inactive" :
        condition.Contains("night", StringComparison.Ordinal) ? "Night only" :
        condition.Contains("weather ", StringComparison.Ordinal) ? "Needs " + WeatherPart(condition) :
        "Unavailable";

    private static string WeatherPart(string condition)
    {
        var from = condition.IndexOf("weather ", StringComparison.Ordinal) + "weather ".Length;
        var end = condition.IndexOf(" + ", from, StringComparison.Ordinal);
        return end < 0 ? condition[from..] : condition[from..end];
    }
}

/// <summary>Where the collector overlay has a point catalogue: the four Eureka field maps only.</summary>
internal static class OverlayZones
{
    /// <summary>The Baldesion Arsenal: map layers 520-527 of Hydatos (territory 827).</summary>
    public static bool IsBaldesionArsenal(uint territory, uint mapId) =>
        territory == ForayZones.EurekaHydatos && mapId is >= 520 and <= 527;

    /// <summary>
    /// True on the field map of Anemos, Pagos, Pyros or Hydatos. Never inside the Baldesion Arsenal (it shares
    /// territory 827) and never in Bozja, Zadnor or the Occult Crescent (no coverage file there).
    /// </summary>
    public static bool IsSupportedFieldMap(uint territory, uint mapId) =>
        CoverageFeed.ZoneName(territory) != null && mapId != 0 && !IsBaldesionArsenal(territory, mapId);
}

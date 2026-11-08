using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>
/// Pure helpers for the pose records: rounding (positions 3 dp, quaternions 4 dp, degrees 2 dp), the skeleton-root
/// angles (ported from XivForays.Imaging GroundTilt.Degrees / TiltDegrees, so both read the same numbers), the
/// customize hash (Imaging's 12-hex customizeHash as a number) and the skeleton path parser.
/// </summary>
internal static class PoseMath
{
    public static float R2(float v) => MathF.Round(v, 2);
    public static float R3(float v) => MathF.Round(v, 3);
    public static float R4(float v) => MathF.Round(v, 4);

    public static float[] Vec(Vector3 v) => [R3(v.X), R3(v.Y), R3(v.Z)];
    public static float[] Quat(Quaternion q) => [R4(q.X), R4(q.Y), R4(q.Z), R4(q.W)];

    /// <summary>Pitch / yaw / roll in degrees (Y-up, yaw about Y), exactly as Imaging's GroundTilt.Degrees.</summary>
    public static (float Pitch, float Yaw, float Roll) Degrees(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        var forward = Vector3.Transform(Vector3.UnitZ, q);
        var yaw = MathF.Atan2(forward.X, forward.Z) * 180 / MathF.PI;
        var pitch = MathF.Asin(Math.Clamp(-forward.Y, -1, 1)) * 180 / MathF.PI;
        var right = Vector3.Transform(Vector3.UnitX, q);
        var roll = MathF.Asin(Math.Clamp(right.Y, -1, 1)) * 180 / MathF.PI;
        return (pitch, yaw, roll);
    }

    /// <summary>Degrees between the rotation's up axis and straight up (Imaging's GroundTilt.TiltDegrees).</summary>
    public static float TiltDegrees(Quaternion q)
    {
        var up = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, Quaternion.Normalize(q)));
        return MathF.Acos(Math.Clamp(up.Y, -1, 1)) * 180 / MathF.PI;
    }

    /// <summary>First 48 bits of SHA-1 (big-endian), the number form of Imaging's 12-hex customizeHash.</summary>
    public static long CustomizeHash(ReadOnlySpan<byte> customize)
    {
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(customize, hash);
        long value = 0;
        for (var i = 0; i < 6; i++)
            value = (value << 8) | hash[i];
        return value;
    }

    /// <summary>
    /// "chara/monster/m0123/skeleton/base/b0001/skl_m0123b0001.sklb" -> [109 ('m'), 123, 1]. Null when the path does
    /// not hold a skl_[cdmw]NNNNbNNNN name. Only the numbers leave the plugin.
    /// </summary>
    public static int[]? ParseSkeleton(ReadOnlySpan<byte> utf8Path)
    {
        if (utf8Path.IsEmpty || utf8Path.Length > 260)
            return null;
        var path = Encoding.UTF8.GetString(utf8Path);
        var at = path.LastIndexOf("skl_", StringComparison.Ordinal);
        if (at < 0 || at + 14 > path.Length)
            return null;
        var kind = path[at + 4];
        if (kind is not ('c' or 'd' or 'm' or 'w') || path[at + 9] != 'b')
            return null;
        if (!int.TryParse(path.AsSpan(at + 5, 4), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) ||
            !int.TryParse(path.AsSpan(at + 10, 4), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var baseId))
            return null;
        return [kind, id, baseId];
    }

    public static byte Form(bool mutated, bool adapted) => (byte)((mutated ? 1 : 0) | (adapted ? 2 : 0));

    /// <summary>Absolute heading change in degrees, wrapped to 0..180.</summary>
    public static float HeadingDeltaDeg(float a, float b)
    {
        var d = MathF.Abs(a - b) * 180 / MathF.PI % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>Heading in degrees, 0..360.</summary>
    public static float HeadingDeg(float radians)
    {
        var d = radians * 180 / MathF.PI % 360;
        return d < 0 ? d + 360 : d;
    }

    /// <summary>
    /// Signed degrees (-180..180) between the facing <paramref name="heading"/> (game convention: 0 faces +Z,
    /// forward = (sin, cos)) at <paramref name="from"/> and the direction to <paramref name="to"/>, on the ground plane.
    /// 0 = facing it.
    /// </summary>
    public static float FacingToDeg(Vector3 from, float heading, Vector3 to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        if (dx * dx + dz * dz < 1e-8f)
            return 0;
        var target = MathF.Atan2(dx, dz);
        var d = (target - heading) * 180 / MathF.PI;
        d %= 360;
        if (d > 180)
            d -= 360;
        else if (d < -180)
            d += 360;
        return d;
    }

    /// <summary>
    /// True when every float in the pose is finite. System.Text.Json throws on NaN/Infinity, which would wedge the
    /// retry batch, so a pose that fails this is dropped in the plugin.
    /// </summary>
    public static bool IsFinite(EnemyPose? pose)
    {
        if (pose == null)
            return true;
        return float.IsFinite(pose.PlayerDistance) && float.IsFinite(pose.HitboxRadius) && float.IsFinite(pose.Rotation) &&
               float.IsFinite(pose.Scale) && float.IsFinite(pose.OverallSpeed) && Finite(pose.SkeletonPitch) &&
               Finite(pose.SkeletonYaw) && Finite(pose.SkeletonRoll) && Finite(pose.SkeletonTilt) &&
               Finite(pose.HomeRotation) && Finite(pose.Position) && Finite(pose.DrawPosition) &&
               Finite(pose.DrawRotation) && Finite(pose.DrawScale) && Finite(pose.SkeletonPosition) &&
               Finite(pose.SkeletonRotation) && Finite(pose.AnimTime) && Finite(pose.HomePosition) &&
               Finite(pose.Attached) && Finite(pose.Timelines);
    }

    public static bool IsFinite(EnemyFormChange change) =>
        IsFinite(change.Before) && IsFinite(change.After) && Finite(change.DrawHeightDelta) && Finite(change.DrawOffsetDelta);

    public static bool IsFinite(EnemyIdleTrack idle) => Finite(idle.Samples);

    public static bool IsFinite(EnemyAggro aggro)
    {
        if (!float.IsFinite(aggro.DistanceToPlayer) || !float.IsFinite(aggro.EnemyFacingToPlayerDeg) ||
            !float.IsFinite(aggro.PlayerFacingToEnemyDeg) || !float.IsFinite(aggro.Dy) ||
            !Finite(aggro.EnemyHomeDistance) || !Finite(aggro.EnemyMovedSinceSettledY) || !Finite(aggro.EnemyCast) ||
            !float.IsFinite(aggro.Player.Speed) || !float.IsFinite(aggro.Player.HpPct) || !Finite(aggro.Player.MpPct))
            return false;
        foreach (var link in aggro.Linked)
            if (!float.IsFinite(link.Distance))
                return false;
        return true;
    }

    private static bool Finite(float? v) => v is not { } f || float.IsFinite(f);

    private static bool Finite(float[]? values)
    {
        if (values == null)
            return true;
        foreach (var v in values)
            if (!float.IsFinite(v))
                return false;
        return true;
    }

    private static bool Finite(float[][]? rows)
    {
        if (rows == null)
            return true;
        foreach (var row in rows)
            if (!Finite(row))
                return false;
        return true;
    }
}

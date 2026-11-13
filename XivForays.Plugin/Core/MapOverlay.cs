using System;
using System.Numerics;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

// Collector overlay (0.0.0.16), feature 9: spawn points drawn over the game's area map window. Dependency-free so the
// coordinate maths and the filters can be unit tested. Read-only: the map is only looked at, never changed.

/// <summary>
/// World to map maths. A zone map is a 2048 x 2048 texture; a world x/z position lands on texture pixel
/// <c>(world + offset) * sizeFactor / 100 + 1024</c>, with the Map sheet's SizeFactor and OffsetX / OffsetY (the
/// same values the map agent holds for the open map). The in-game map coordinate ("X: 21.4") follows from the same
/// numbers: <c>41 / (sizeFactor / 100) * pixel / 2048 + 1</c>.
/// </summary>
internal static class MapMath
{
    public const float TextureSize = 2048f;

    /// <summary>One world axis (x, or z for the map's vertical axis) as a texture pixel.</summary>
    public static float ToTexture(float world, float offset, float sizeFactor) =>
        (world + offset) * (sizeFactor / 100f) + TextureSize / 2f;

    public static Vector2 ToTexture(float worldX, float worldZ, float offsetX, float offsetY, float sizeFactor) =>
        new(ToTexture(worldX, offsetX, sizeFactor), ToTexture(worldZ, offsetY, sizeFactor));

    /// <summary>The in-game map coordinate of one axis (what the map window shows as X / Y).</summary>
    public static float ToMapCoordinate(float world, float offset, float sizeFactor) =>
        41f / (sizeFactor / 100f) * ToTexture(world, offset, sizeFactor) / TextureSize + 1f;

    /// <summary>A texture pixel back to a world axis (inverse of <see cref="ToTexture(float, float, float)"/>).</summary>
    public static float FromTexture(float texture, float offset, float sizeFactor) =>
        (texture - TextureSize / 2f) / (sizeFactor / 100f) - offset;
}

/// <summary>
/// Where the map texture is on screen this frame: the screen position of texture pixel (0, 0) and the screen pixels per
/// texture pixel on each axis, read from the map image node of the open area map (so zoom and pan are followed), plus
/// the visible map rectangle used for culling. Shift is a correction (screen pixels) applied to every point.
/// </summary>
internal readonly record struct MapView(
    Vector2 Origin,
    Vector2 PixelsPerTexel,
    float OffsetX,
    float OffsetY,
    float SizeFactor,
    Vector2 ClipMin,
    Vector2 ClipMax,
    Vector2 Shift)
{
    public bool Valid => SizeFactor > 0 && PixelsPerTexel.X > 0 && PixelsPerTexel.Y > 0 &&
                         float.IsFinite(Origin.X) && float.IsFinite(Origin.Y) && ClipMax.X > ClipMin.X && ClipMax.Y > ClipMin.Y;

    /// <summary>
    /// The view from the map image node: its screen position, its absolute scale (the node transform's diagonal) and
    /// its size. The node shows the whole texture, so one texture pixel is <c>nodeSize * scale / 2048</c> screen pixels.
    /// </summary>
    public static MapView FromNode(float screenX, float screenY, float scaleX, float scaleY, float width, float height,
        float offsetX, float offsetY, float sizeFactor, Vector2 clipMin, Vector2 clipMax) =>
        new(new Vector2(screenX, screenY),
            new Vector2(width * scaleX / MapMath.TextureSize, height * scaleY / MapMath.TextureSize),
            offsetX, offsetY, sizeFactor, clipMin, clipMax, Vector2.Zero);

    /// <summary>A world x/z position on screen.</summary>
    public Vector2 ToScreen(float worldX, float worldZ)
    {
        var texture = MapMath.ToTexture(worldX, worldZ, OffsetX, OffsetY, SizeFactor);
        return Origin + texture * PixelsPerTexel + Shift;
    }

    /// <summary>Yalms of world distance as screen pixels (for circles on the map).</summary>
    public float ScreenPixels(float yalms) => yalms * SizeFactor / 100f * PixelsPerTexel.X;

    /// <summary>Inside the visible map rectangle, with a margin (a marker partly inside is still drawn).</summary>
    public bool Visible(Vector2 screen, float margin = 0f) =>
        screen.X >= ClipMin.X - margin && screen.X <= ClipMax.X + margin &&
        screen.Y >= ClipMin.Y - margin && screen.Y <= ClipMax.Y + margin;

    /// <summary>
    /// The same view moved so that the player lands where the game draws the player arrow. Used when the arrow node
    /// is visible and the node maths is off by more than <paramref name="tolerance"/> pixels (never more than
    /// <paramref name="limit"/>: a larger gap means the arrow reading is wrong, and the node maths is kept).
    /// </summary>
    public MapView AlignedTo(Vector2 playerXz, Vector2 arrowScreen, float tolerance, float limit, out float gap)
    {
        var predicted = ToScreen(playerXz.X, playerXz.Y);
        var delta = arrowScreen - predicted;
        gap = delta.Length();
        if (!float.IsFinite(gap) || gap <= tolerance || gap > limit)
            return this;
        return this with { Shift = Shift + delta };
    }
}

/// <summary>Which spawn points feature 9 draws on the map.</summary>
internal readonly record struct MapFilter(
    bool Confirmed,
    bool ToConfirm,
    bool WalkPast,
    bool NeverSeen,
    bool SpawnsNowOnly,
    bool PendingOnly)
{
    public bool ShowsStatus(CoverageStatus status) => status switch
    {
        CoverageStatus.Confirmed => Confirmed,
        CoverageStatus.ToConfirm => ToConfirm,
        CoverageStatus.WalkPast => WalkPast,
        CoverageStatus.NeverSeen => NeverSeen,
        _ => false
    };

    /// <summary>
    /// One point: its status box is ticked; with "Spawns now only" its condition is not known to be unmet now (an
    /// unknown condition still shows); with "Pending respawn only" it waits for a respawn after a recent kill. The
    /// picked point is always shown.
    /// </summary>
    public bool Shows(CoverageStatus status, ConditionState condition, bool pendingRespawn, bool picked)
    {
        if (picked)
            return true;
        if (PendingOnly && !pendingRespawn)
            return false;
        if (!SpawnsNow.Shows(SpawnsNowOnly, condition, false))
            return false;
        return ShowsStatus(status);
    }
}

/// <summary>
/// The "Spawns now only" rule shared by the map (9), the nearby list (1) and the world markers (5, 6): a point whose
/// condition (night, weather, FATE) is known to be unmet now is left out; an unknown condition stays; the picked point
/// always stays.
/// </summary>
internal static class SpawnsNow
{
    public static bool Shows(bool spawnsNowOnly, ConditionState condition, bool picked) =>
        picked || !spawnsNowOnly || condition != ConditionState.Unavailable;
}

/// <summary>Limits of the map markers: drawn markers per view, and the hover distance of the tooltip.</summary>
internal static class MapRates
{
    public const int DefaultMaxMarkers = 400;
    public const int MinMaxMarkers = 50;
    public const int MaxMaxMarkers = 1500;

    /// <summary>The pointer is over a marker within this many screen pixels.</summary>
    public const float HoverPixels = 8f;

    /// <summary>The player arrow and the computed player point may differ by this much before the view is shifted.</summary>
    public const float AlignTolerancePixels = 3f;

    /// <summary>A larger gap means the arrow reading is not usable.</summary>
    public const float AlignLimitPixels = 200f;
}

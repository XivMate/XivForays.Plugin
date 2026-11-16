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
/// The area map's numbers for one frame, read fresh from the live map window every frame (nothing here is cached):
/// the map image node's on-screen rectangle, the map component's rectangle, the accumulated UI scale (screen pixels
/// per node pixel, which includes the window's own scale and the global UI scale), the zoom (node pixels per texture
/// pixel) and the pan of the map component, the image part (U, V, width, height in texture pixels) the image node
/// shows, and the map component's own map size (node pixels, its MapWidth x MapHeight). The image node is a window onto
/// the 2048 texture, not the whole texture: zoom and pan decide what it shows.
/// </summary>
internal readonly record struct MapFrame(
    Vector2 ImageMin,
    Vector2 ImageMax,
    Vector2 FrameMin,
    Vector2 FrameMax,
    float UiScale,
    float Zoom,
    Vector2 Pan,
    Vector4 Part,
    Vector2 MapSize = default)
{
    /// <summary>A part shape is known (it may still lie outside the texture, see <see cref="PartInTexture"/>).</summary>
    public bool HasPart => Part.Z > 0 && Part.W > 0 && ImageMax.X > ImageMin.X && ImageMax.Y > ImageMin.Y;

    /// <summary>
    /// The part lies on the 2048 texture (a few texels of rounding allowed). Zoomed out past the map window the game
    /// reports parts like 1770/1628 2328x2144, which are not a window onto the texture, and the image part model is then
    /// hundreds of pixels off (live log, 16 Nov).
    /// </summary>
    public bool PartInTexture => HasPart && Part.X >= -PartSlack && Part.Y >= -PartSlack &&
                                 Part.X + Part.Z <= MapMath.TextureSize + PartSlack &&
                                 Part.Y + Part.W <= MapMath.TextureSize + PartSlack;

    private const float PartSlack = 4f;

    /// <summary>The map component's map size and the frame corner are known: the map centre can be placed.</summary>
    public bool HasMapSize => MapSize.X > 0 && MapSize.Y > 0 && UiScale > 0 && float.IsFinite(UiScale) &&
                              FrameMax.X > FrameMin.X && FrameMax.Y > FrameMin.Y;

    /// <summary>
    /// The screen point the map view is centred on: the frame corner plus half the component's map size. The frame
    /// node itself is smaller (706 x 497 node pixels against a 724 x 535 map), so its own centre is 9 x 19 node pixels
    /// (31 screen pixels at scale 1.5) up and left of the real centre.
    /// </summary>
    public Vector2 MapCentre => FrameMin + MapSize / 2f * UiScale;

    /// <summary>The rectangle markers may be drawn in: the map image, inside the map component when both are known.</summary>
    public (Vector2 Min, Vector2 Max) Clip()
    {
        if (FrameMax.X <= FrameMin.X || FrameMax.Y <= FrameMin.Y)
            return (ImageMin, ImageMax);
        return (Vector2.Max(ImageMin, FrameMin), Vector2.Min(ImageMax, FrameMax));
    }
}

/// <summary>
/// The ways the texture can sit in the map window. Model 0 uses the image part (what the image node shows of the
/// texture). Models 1 to 8 put texture pixel 1024 at the centre of the image (1 to 4) or of the frame node (5 to 8),
/// moved by the pan, with the pan added or subtracted and counted in texture pixels or in node pixels. Model 9 puts it
/// at the map centre (<see cref="MapFrame.MapCentre"/>) with the pan subtracted: the one placement that fits all 14 live
/// lines of 16 Nov (Pagos and Hydatos, zoom 0.25 to 2). The gap of every model goes to the log once per map open.
/// </summary>
internal static class MapModels
{
    public const int Count = 10;

    /// <summary>Texture pixel 1024 + pan at the map centre.</summary>
    public const int MapCentre = 9;

    public static string Name(int model) => model switch
    {
        0 => "image part",
        1 => "image centre +pan",
        2 => "image centre -pan",
        3 => "image centre +pan/zoom",
        4 => "image centre -pan/zoom",
        5 => "frame centre +pan",
        6 => "frame centre -pan",
        7 => "frame centre +pan/zoom",
        8 => "frame centre -pan/zoom",
        9 => "map centre -pan",
        _ => "none"
    };

    /// <summary>
    /// The model used before the player arrow has picked one: the map centre when the map size is known, else the image
    /// part when it lies on the texture, else the frame centre with the pan subtracted (about 31 px off, but steady).
    /// </summary>
    public static int Default(in MapFrame frame) => frame.HasMapSize ? MapCentre : frame.PartInTexture ? 0 : 6;
}

/// <summary>
/// Where the map texture is on screen this frame: the screen position of texture pixel (0, 0) and the screen pixels per
/// texture pixel on each axis, plus the visible map rectangle used for clipping and culling. Shift is a correction
/// (screen pixels) applied to every point.
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
                         float.IsFinite(PixelsPerTexel.X) && float.IsFinite(PixelsPerTexel.Y) &&
                         float.IsFinite(Origin.X) && float.IsFinite(Origin.Y) && ClipMax.X > ClipMin.X && ClipMax.Y > ClipMin.Y;

    /// <summary>The view of one model (see <see cref="MapModels"/>) for this frame's map numbers.</summary>
    public static MapView From(in MapFrame frame, int model, float offsetX, float offsetY, float sizeFactor)
    {
        var (clipMin, clipMax) = frame.Clip();
        Vector2 origin, ppt;
        if (model == 0)
        {
            // The image node stretches texture part (U, V, W, H) over its on-screen rectangle.
            var size = frame.ImageMax - frame.ImageMin;
            ppt = frame.HasPart ? new Vector2(size.X / frame.Part.Z, size.Y / frame.Part.W) : Vector2.Zero;
            origin = frame.ImageMin - new Vector2(frame.Part.X, frame.Part.Y) * ppt;
        }
        else if (model == MapModels.MapCentre)
        {
            // Texture pixel t is at the map centre + (t - 1024 - pan) * zoom * UI scale.
            var scale = frame.Zoom * frame.UiScale;
            ppt = frame.HasMapSize ? new Vector2(scale, scale) : Vector2.Zero;
            origin = frame.MapCentre - (frame.Pan + new Vector2(MapMath.TextureSize / 2f)) * scale;
        }
        else
        {
            var centre = model <= 4 ? (frame.ImageMin + frame.ImageMax) / 2f : (frame.FrameMin + frame.FrameMax) / 2f;
            var k = (model - 1) % 4;
            var sign = k is 0 or 2 ? 1f : -1f;
            var pan = k < 2 ? frame.Pan : frame.Zoom > 0 ? frame.Pan / frame.Zoom : Vector2.Zero;
            var scale = frame.Zoom * frame.UiScale;
            ppt = new Vector2(scale, scale);
            // Texture pixel t is at centre + (t - 1024 + sign * pan) * scale.
            origin = centre + (sign * pan - new Vector2(MapMath.TextureSize / 2f)) * scale;
        }

        return new MapView(origin, ppt, offsetX, offsetY, sizeFactor, clipMin, clipMax, Vector2.Zero);
    }

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
    /// is visible and the maths is off by more than <paramref name="tolerance"/> pixels (never more than
    /// <paramref name="limit"/>: a larger gap means the arrow reading is wrong, and the maths is kept).
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

/// <summary>
/// The map self-check: how far each model puts the player from the game's own player arrow, and which model to use.
/// </summary>
internal static class MapCalibration
{
    /// <summary>Fills <paramref name="gaps"/> (one per model, NaN for an invalid view) and returns the nearest model, or -1.</summary>
    public static int Best(in MapFrame frame, float offsetX, float offsetY, float sizeFactor, Vector2 playerXz, Vector2 arrow,
        Span<float> gaps)
    {
        var best = -1;
        var bestGap = float.MaxValue;
        for (var model = 0; model < MapModels.Count && model < gaps.Length; model++)
        {
            var view = MapView.From(frame, model, offsetX, offsetY, sizeFactor);
            var gap = view.Valid ? Vector2.Distance(view.ToScreen(playerXz.X, playerXz.Y), arrow) : float.NaN;
            gaps[model] = gap;
            // An image part off the texture is logged but never picked: it only passes the arrow by chance.
            if (model == 0 && !frame.PartInTexture)
                continue;
            if (float.IsFinite(gap) && gap < bestGap)
            {
                bestGap = gap;
                best = model;
            }
        }

        return best;
    }

    /// <summary>
    /// The model for this frame. The map centre model whenever the map size is known (it fits every live line, zoomed
    /// in and out); else the image part model when the part lies on the texture; the arrow only checks these. Otherwise
    /// the nearest model when the arrow lands within <see cref="MapRates.PickLimitPixels"/>, else the last model picked
    /// (kept while the zone stays), else the default.
    /// </summary>
    public static int Choose(int best, float bestGap, int sticky, in MapFrame frame) =>
        frame.HasMapSize ? MapModels.MapCentre
        : frame.PartInTexture ? 0
        : best >= 0 && bestGap <= MapRates.PickLimitPixels ? best
        : sticky >= 0 ? sticky
        : MapModels.Default(frame);

    /// <summary>
    /// The view for one frame (what the map draws with), and the model kept for the next frame in
    /// <paramref name="sticky"/>. <paramref name="gaps"/> gets every model's gap to the arrow (NaN without an arrow).
    /// A model is kept as sticky, and shifted the last few pixels onto the arrow, only when the arrow confirmed it this
    /// frame (within <see cref="MapRates.PickLimitPixels"/>). A model that only passes near the arrow while the map is
    /// dragged is never snapped onto it and then kept as the pan moves on.
    /// </summary>
    public static MapView Resolve(in MapFrame frame, float offsetX, float offsetY, float sizeFactor, Vector2 playerXz,
        Vector2? arrow, ref int sticky, Span<float> gaps, out int model, out bool confirmed)
    {
        gaps.Fill(float.NaN);
        var best = arrow is { } a ? Best(frame, offsetX, offsetY, sizeFactor, playerXz, a, gaps) : -1;
        model = Choose(best, best >= 0 ? gaps[best] : float.MaxValue, sticky, frame);
        confirmed = arrow != null && model < gaps.Length && float.IsFinite(gaps[model]) && gaps[model] <= MapRates.PickLimitPixels;
        if (confirmed)
            sticky = model;
        var view = MapView.From(frame, model, offsetX, offsetY, sizeFactor);
        return confirmed && arrow is { } at
            ? view.AlignedTo(playerXz, at, MapRates.AlignTolerancePixels, MapRates.PickLimitPixels, out _)
            : view;
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

    /// <summary>A larger gap means the arrow reading is not usable for a shift (the shift also needs a confirmed model).</summary>
    public const float AlignLimitPixels = 60f;

    /// <summary>A model is picked by the arrow only when it puts the player this close to the arrow.</summary>
    public const float PickLimitPixels = 40f;
}

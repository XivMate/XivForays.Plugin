using System.Numerics;
using XivMate.DataGathering.Forays.Dalamud.Core;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>
/// The map view against two live "Collector overlay map view" log lines (16 Nov, UI scale 1.5): Pagos (offset 0/0, the
/// overlay sat on the map) and Hydatos (map agent offset 0/-475, Map sheet offset 0/475, the overlay sat far above the
/// map). The frames are rebuilt from the logged numbers; the player's world x/z is worked back from the logged player
/// point and the model that drew it.
/// </summary>
public class MapLiveLogTests
{
    // Pagos, map 467: image 728x536 at -1343/292 to -251/1096, frame -1343/292 to -284/1037, zoom 2, pan 542.2/390.6,
    // part 1385/1280 364x268, arrow -800/692, player -799/695 by the image part model.
    internal static readonly MapFrame Pagos = new(new Vector2(-1343, 292), new Vector2(-251, 1096),
        new Vector2(-1343, 292), new Vector2(-284, 1037), 1.5f, 2f, new Vector2(542.2f, 390.6f), new Vector4(1385, 1280, 364, 268));

    internal static readonly Vector2 PagosArrow = new(-800, 692);

    // Hydatos, map 515: image 724x537 at -1343/292 to -257/1097, frame -1343/292 to -284/1037, zoom 0.5714,
    // pan -60.2/-407.6, part 330/148 1268x940, arrow -800/692, player -903/-819 by "image centre +pan".
    internal static readonly MapFrame Hydatos = new(new Vector2(-1343, 292), new Vector2(-257, 1097),
        new Vector2(-1343, 292), new Vector2(-284, 1037), 1.5f, 0.5714f, new Vector2(-60.2f, -407.6f), new Vector4(330, 148, 1268, 940));

    internal static readonly Vector2 HydatosArrow = new(-800, 692);

    /// <summary>The Map sheet row of Hydatos (map 515): size factor 100, offset 0/475.</summary>
    internal const float HydatosSheetOffsetY = 475f;

    /// <summary>What the map agent gave on Hydatos: offset 0/-475.</summary>
    private const float HydatosAgentOffsetY = -475f;

    /// <summary>The world x/z that a model of this frame draws at <paramref name="screen"/> (inverse of ToScreen).</summary>
    internal static Vector2 WorldAt(in MapFrame frame, int model, Vector2 screen, float offsetX, float offsetY, float sizeFactor)
    {
        var view = MapView.From(frame, model, offsetX, offsetY, sizeFactor);
        var texel = (screen - view.Origin) / view.PixelsPerTexel;
        return new Vector2(MapMath.FromTexture(texel.X, offsetX, sizeFactor), MapMath.FromTexture(texel.Y, offsetY, sizeFactor));
    }

    internal static Vector2 PagosPlayer => WorldAt(Pagos, 0, new Vector2(-799, 695), 0, 0, 100);

    internal static Vector2 HydatosPlayer => WorldAt(Hydatos, 1, new Vector2(-903, -819), 0, HydatosAgentOffsetY, 100);

    private static float[] Gaps(in MapFrame frame, float offsetY, Vector2 player, Vector2 arrow)
    {
        var gaps = new float[MapModels.Count];
        MapCalibration.Best(frame, 0, offsetY, 100, player, arrow, gaps);
        return gaps;
    }

    [Fact]
    public void ThePagosLineIsReproducedAndTheImagePartIsOnTheArrow()
    {
        // Logged: image part 3, image centre +pan 4013, image centre -pan 3, frame centre -pan 31.
        var gaps = Gaps(Pagos, 0, PagosPlayer, PagosArrow);
        Assert.InRange(gaps[0], 0f, 5f);
        Assert.Equal(4013f, gaps[1], 0.5f * 6);
        Assert.InRange(gaps[2], 0f, 5f);
        Assert.Equal(31f, gaps[6], 3f);
        Assert.Equal(975f, gaps[8], 3f);
    }

    [Fact]
    public void TheHydatosLineIsReproducedWithTheAgentOffset()
    {
        // Logged with the agent's 0/-475: image part 813, image centre +pan 1514, image centre -pan 812, frame centre
        // -pan/zoom 581. The current maths gives the same gaps from the same numbers.
        var gaps = Gaps(Hydatos, HydatosAgentOffsetY, HydatosPlayer, HydatosArrow);
        Assert.Equal(813f, gaps[0], 3f);
        Assert.Equal(1514f, gaps[1], 3f);
        Assert.Equal(812f, gaps[2], 3f);
        Assert.Equal(551f, gaps[4], 3f);
        Assert.Equal(581f, gaps[8], 3f);
        // 813 px at zoom 0.5714 and UI scale 1.5 is 949 texels: twice the 475 offset, so the sign is the other way round.
        Assert.Equal(2f * 475f, gaps[0] / (0.5714f * 1.5f), 5f);
    }

    [Fact]
    public void WithTheSheetOffsetTheHydatosPlayerLandsOnTheArrow()
    {
        var gaps = Gaps(Hydatos, HydatosSheetOffsetY, HydatosPlayer, HydatosArrow);
        Assert.InRange(gaps[0], 0f, 3f);
        Assert.InRange(gaps[2], 0f, 3f); // "image centre -pan" is the same placement while the part is not clamped
        var view = MapView.From(Hydatos, 0, 0, HydatosSheetOffsetY, 100);
        var at = view.ToScreen(HydatosPlayer.X, HydatosPlayer.Y);
        Assert.InRange(at.X, HydatosArrow.X - 3, HydatosArrow.X + 3);
        Assert.InRange(at.Y, HydatosArrow.Y - 3, HydatosArrow.Y + 3);
        // The player is near the middle of Hydatos's map: world about -60/-883 (map coordinate about 14/7).
        Assert.Equal(-60f, HydatosPlayer.X, 2f);
        Assert.Equal(-883f, HydatosPlayer.Y, 2f);
    }

    // ---- drag and zoom replays ----

    private static readonly Vector2 ComponentMapSize = new(724, 535);

    /// <summary>
    /// One frame as the game draws it (the live lines of 16 Nov): texture pixel 1024 + pan sits at the map centre
    /// (frame corner + half the 724 x 535 map at the UI scale), zoom x UI scale screen pixels per texel. The frame node is
    /// 706 x 497 node pixels, as logged. The image node covers the texture's part of the map window; when the texture
    /// does not fill the window (zoomed out) the game's part numbers do not describe the texture (logged 1770/1628
    /// 2328x2144 and the like), mimicked here by a part off the texture.
    /// </summary>
    internal static MapFrame GameFrame(Vector2 frameMin, float zoom, Vector2 pan, float uiScale = 1.5f)
    {
        var frameMax = frameMin + new Vector2(706, 497) * uiScale;
        var mapMax = frameMin + ComponentMapSize * uiScale;
        var centre = frameMin + ComponentMapSize / 2f * uiScale;
        var scale = zoom * uiScale;
        var textureMin = centre - (new Vector2(MapMath.TextureSize / 2f) + pan) * scale;
        var textureMax = textureMin + new Vector2(MapMath.TextureSize) * scale;
        var imageMin = Vector2.Max(textureMin, frameMin);
        var imageMax = Vector2.Min(textureMax, mapMax);
        var shownUv = (imageMin - textureMin) / scale;
        var shownSize = (imageMax - imageMin) / scale;
        var fills = textureMin.X <= frameMin.X && textureMin.Y <= frameMin.Y && textureMax.X >= mapMax.X && textureMax.Y >= mapMax.Y;
        var part = fills
            ? new Vector4(shownUv.X, shownUv.Y, shownSize.X, shownSize.Y)
            : new Vector4(shownUv.X + 1700f, shownUv.Y + 1600f, ComponentMapSize.X / zoom, ComponentMapSize.Y / zoom);
        return new MapFrame(imageMin, imageMax, frameMin, frameMax, uiScale, zoom, pan, part, ComponentMapSize);
    }

    /// <summary>Where the game draws a world point in that frame.</summary>
    internal static Vector2 GameScreen(in MapFrame frame, Vector2 world, float offsetX, float offsetY, float sizeFactor)
    {
        var texel = MapMath.ToTexture(world.X, world.Y, offsetX, offsetY, sizeFactor);
        return frame.FrameMin + ComponentMapSize / 2f * frame.UiScale +
               (texel - new Vector2(MapMath.TextureSize / 2f) - frame.Pan) * frame.Zoom * frame.UiScale;
    }

    private static bool InFrame(in MapFrame frame, Vector2 at) =>
        at.X >= frame.FrameMin.X && at.X <= frame.FrameMax.X && at.Y >= frame.FrameMin.Y && at.Y <= frame.FrameMax.Y;

    /// <summary>
    /// 60 frames: a zoom sweep 0.25 to 2 and back while dragging the pan up to 400 texels each way from the player
    /// (so the arrow leaves the window on some frames), at two window positions.
    /// </summary>
    private static IEnumerable<MapFrame> Replay(Vector2 playerTexelFromCentre)
    {
        for (var i = 0; i < 60; i++)
        {
            var t = i / 59f;
            var zoom = 0.25f + 1.75f * (t < 0.5f ? t * 2f : 2f - t * 2f);
            var drag = new Vector2(MathF.Sin(i / 5f) * 400f, MathF.Cos(i / 7f) * 300f);
            var corner = i < 30 ? new Vector2(-1343, 292) : new Vector2(-1718, 281);
            yield return GameFrame(corner, zoom, playerTexelFromCentre + drag);
        }
    }

    private static void ReplayStaysLocked(Vector2 player, float offsetX, float offsetY, float sizeFactor)
    {
        var texel = MapMath.ToTexture(player.X, player.Y, offsetX, offsetY, sizeFactor) - new Vector2(MapMath.TextureSize / 2f);
        var sticky = -1;
        var gaps = new float[MapModels.Count];
        int frames = 0, zoomedOut = 0, offTexture = 0, arrowGone = 0;
        foreach (var frame in Replay(texel))
        {
            var truth = GameScreen(frame, player, offsetX, offsetY, sizeFactor);
            Vector2? arrow = InFrame(frame, truth) ? truth : null;
            var view = MapCalibration.Resolve(frame, offsetX, offsetY, sizeFactor, player, arrow, ref sticky, gaps, out var model, out _);
            Assert.Equal(MapModels.MapCentre, model);
            Assert.True(view.Valid);
            Assert.InRange(Vector2.Distance(view.ToScreen(player.X, player.Y), truth), 0f, 1f);
            Assert.InRange(view.Shift.Length(), 0f, 1f);
            var far = player + new Vector2(200f, -150f) * (100f / sizeFactor);
            Assert.InRange(Vector2.Distance(view.ToScreen(far.X, far.Y), GameScreen(frame, far, offsetX, offsetY, sizeFactor)), 0f, 1f);
            frames++;
            if (frame.ImageMax.X - frame.ImageMin.X < frame.FrameMax.X - frame.FrameMin.X)
                zoomedOut++;
            if (!frame.PartInTexture)
                offTexture++;
            if (arrow == null)
                arrowGone++;
        }

        Assert.Equal(60, frames);
        Assert.True(zoomedOut > 5, $"zoomed out frames {zoomedOut}");
        Assert.True(offTexture > 5, $"off-texture parts {offTexture}");
        Assert.True(arrowGone > 0, "the arrow never left the window");
        Assert.True(sticky is -1 or MapModels.MapCentre);
    }

    [Fact]
    public void AHydatosDragAndZoomSweepStaysLockedToTheMap() =>
        ReplayStaysLocked(HydatosPlayer, 0, HydatosSheetOffsetY, 100);

    [Fact]
    public void APagosDragAndZoomSweepStaysLockedToTheMap() =>
        ReplayStaysLocked(PagosPlayer, 0, 0, 100);

    [Fact]
    public void ABaldesionArsenalStyleMapStaysLockedToo() =>
        // Map 520: size factor 200, sheet offset 0/-754 (job 1045 read of the Map sheet); a player near the entrance.
        ReplayStaysLocked(new Vector2(10f, 960f), 0, -754, 200);

    [Fact]
    public void TheSimulatedGameFramesMatchTheLiveLines()
    {
        // The simulator itself against the live lines: same arrow (within the 1 to 2 px the log rounds) for each line's
        // zoom and pan, so the replays above stand on the game's numbers.
        foreach (var line in MapLogLines.All)
        {
            var frame = GameFrame(line.Frame.FrameMin, line.Frame.Zoom, line.Frame.Pan);
            Assert.Equal(line.Frame.FrameMax.X, frame.FrameMax.X, 1f);
            Assert.Equal(line.Frame.FrameMax.Y, frame.FrameMax.Y, 1f);
            var at = GameScreen(frame, line.PlayerWorld, line.SheetOffset.X, line.SheetOffset.Y, line.SizeFactor);
            Assert.InRange(Vector2.Distance(at, line.Arrow), 0f, 5f);
        }
    }

    [Fact]
    public void ForcingTheImagePartWasWrongZoomedOut()
    {
        // Round 1 (job 1110) forced the image part model. On the zoomed-out live lines it is about 1085 px off, in
        // Pagos too; the new choice does not use it there.
        foreach (var line in MapLogLines.All)
        {
            if (line.Frame.PartInTexture)
                continue;
            var view = MapView.From(line.Frame, 0, line.SheetOffset.X, line.SheetOffset.Y, line.SizeFactor);
            Assert.True(Vector2.Distance(view.ToScreen(line.PlayerWorld.X, line.PlayerWorld.Y), line.Arrow) > 700f, line.ToString());
            Assert.NotEqual(0, MapCalibration.Choose(0, 0f, 0, line.Frame));
        }

        Assert.Equal(8, MapLogLines.All.Count(l => !l.Frame.PartInTexture));
    }
}

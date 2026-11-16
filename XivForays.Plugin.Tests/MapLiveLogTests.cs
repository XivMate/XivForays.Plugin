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

    /// <summary>
    /// One frame of a drag: the same image rectangle as the logged frame, the given zoom and pan. The image centre shows
    /// texture pixel 1024 + pan (both logged lines), so the part follows the pan.
    /// </summary>
    private static MapFrame DragFrame(in MapFrame logged, float zoom, Vector2 pan)
    {
        var size = (logged.ImageMax - logged.ImageMin) / (zoom * logged.UiScale);
        var uv = new Vector2(MapMath.TextureSize / 2f) + pan - size / 2f;
        return logged with { Zoom = zoom, Pan = pan, Part = new Vector4(uv.X, uv.Y, size.X, size.Y) };
    }

    /// <summary>Where the game draws the player arrow in that frame: the player's texture pixel inside the shown part.</summary>
    private static Vector2 ArrowOf(in MapFrame frame, Vector2 player, float offsetX, float offsetY, float sizeFactor)
    {
        var texel = MapMath.ToTexture(player.X, player.Y, offsetX, offsetY, sizeFactor);
        var ppt = (frame.ImageMax - frame.ImageMin) / new Vector2(frame.Part.Z, frame.Part.W);
        return frame.ImageMin + (texel - new Vector2(frame.Part.X, frame.Part.Y)) * ppt;
    }

    private static bool OnImage(in MapFrame frame, Vector2 at) =>
        at.X >= frame.ImageMin.X && at.X <= frame.ImageMax.X && at.Y >= frame.ImageMin.Y && at.Y <= frame.ImageMax.Y;

    /// <summary>A drag of 40 frames: pan swept by up to 300 texels each way, zoom stepped a few times on the way.</summary>
    private static IEnumerable<MapFrame> Drag(MapFrame logged)
    {
        for (var i = 0; i < 40; i++)
        {
            var zoom = logged.Zoom * (i < 10 ? 1f : i < 20 ? 1.25f : i < 30 ? 0.8f : 1f);
            var pan = logged.Pan + new Vector2(MathF.Sin(i / 6f) * 300f, MathF.Cos(i / 7f) * 250f - 250f);
            yield return DragFrame(logged, zoom, pan);
        }
    }

    private static void ReplayStaysLocked(MapFrame logged, Vector2 player, float offsetX, float offsetY, float sizeFactor)
    {
        var sticky = -1;
        var gaps = new float[MapModels.Count];
        var frames = 0;
        foreach (var frame in Drag(logged))
        {
            var truth = ArrowOf(frame, player, offsetX, offsetY, sizeFactor);
            // The arrow is read only while it is on the map image (panned away: not read).
            Vector2? arrow = OnImage(frame, truth) ? truth : null;
            var view = MapCalibration.Resolve(frame, offsetX, offsetY, sizeFactor, player, arrow, ref sticky, gaps, out var model, out _);
            Assert.Equal(0, model);
            Assert.True(view.Valid);
            // Every frame, arrow read or not: the drawn player is on the game's arrow, with no shift carried.
            Assert.InRange(Vector2.Distance(view.ToScreen(player.X, player.Y), truth), 0f, 3f);
            Assert.InRange(view.Shift.Length(), 0f, 3f);
            // A marker 200 y away stays where the map has that spot.
            var far = player + new Vector2(200f, -150f);
            Assert.InRange(Vector2.Distance(view.ToScreen(far.X, far.Y), ArrowOf(frame, far, offsetX, offsetY, sizeFactor)), 0f, 3f);
            frames++;
        }

        Assert.Equal(40, frames);
        Assert.True(sticky is -1 or 0);
    }

    [Fact]
    public void AHydatosDragAndZoomStaysLockedToTheMap() =>
        ReplayStaysLocked(Hydatos, HydatosPlayer, 0, HydatosSheetOffsetY, 100);

    [Fact]
    public void APagosDragAndZoomStaysLockedToTheMap() =>
        ReplayStaysLocked(Pagos, PagosPlayer, 0, 0, 100);

    [Fact]
    public void ABaldesionArsenalStyleMapGoesThroughTheSamePath() =>
        // Map 520: size factor 200, sheet offset 0/-754 (job 1045 read of the Map sheet); a player near the entrance.
        ReplayStaysLocked(Hydatos with { Zoom = 1f, Pan = new Vector2(0f, 400f) }, new Vector2(10f, 960f), 0, -754, 200);

    [Fact]
    public void TheOldPickerWithTheAgentOffsetHopsAndSnapsDuringADrag()
    {
        // The 0.0.0.16 selection, replayed: nearest model within 40 px is picked and kept, a gap up to 60 px is
        // shifted away. With the agent's 0/-475 a wrong model passes near the arrow on some frames, is snapped onto it,
        // and is kept on the next frames while the pan moves on: the "clicks, then goes the opposite way".
        var sticky = -1;
        var gaps = new float[MapModels.Count];
        var used = new HashSet<int>();
        var worst = 0f;
        foreach (var frame in Drag(Hydatos))
        {
            var truth = ArrowOf(frame, HydatosPlayer, 0, HydatosSheetOffsetY, 100);
            var best = MapCalibration.Best(frame, 0, HydatosAgentOffsetY, 100, HydatosPlayer, truth, gaps);
            var model = best >= 0 && gaps[best] <= MapRates.PickLimitPixels ? best : sticky >= 0 ? sticky : 1;
            if (best >= 0 && gaps[best] <= MapRates.PickLimitPixels)
                sticky = best;
            used.Add(model);
            var view = MapView.From(frame, model, 0, HydatosAgentOffsetY, 100)
                .AlignedTo(HydatosPlayer, truth, MapRates.AlignTolerancePixels, MapRates.AlignLimitPixels, out _);
            // A marker 200 y from the player: where the old view draws it against where the map has it.
            var far = HydatosPlayer + new Vector2(200f, -150f);
            worst = MathF.Max(worst, Vector2.Distance(view.ToScreen(far.X, far.Y), ArrowOf(frame, far, 0, HydatosSheetOffsetY, 100)));
        }

        Assert.True(worst > 100f, $"worst {worst}");
        Assert.DoesNotContain(0, used);
    }
}

using System.Numerics;
using XivMate.DataGathering.Forays.Dalamud.Core;
using Xunit;
using PluginConfiguration = XivMate.DataGathering.Forays.Dalamud.Configuration.Configuration;
#if HAS_LUMINA
using Lumina;
using Lumina.Excel.Sheets;
#endif

namespace XivForays.Plugin.Tests;

/// <summary>Feature 9: world to map maths, the map view projection, the filters and the settings.</summary>
public class MapOverlayTests
{
    [Theory]
    [InlineData(0f, 0f, 100f, 1024f)]
    [InlineData(100f, 0f, 100f, 1124f)]
    [InlineData(-1024f, 0f, 100f, 0f)]
    [InlineData(1024f, 0f, 100f, 2048f)]
    [InlineData(0f, 0f, 200f, 1024f)]
    [InlineData(100f, 0f, 200f, 1224f)]
    [InlineData(100f, -50f, 200f, 1124f)]
    [InlineData(-300f, 20f, 50f, 884f)]
    public void WorldToTexturePixel(float world, float offset, float sizeFactor, float expected)
    {
        Assert.Equal(expected, MapMath.ToTexture(world, offset, sizeFactor), 3);
        Assert.Equal(world, MapMath.FromTexture(expected, offset, sizeFactor), 3);
    }

    [Theory]
    [InlineData(0f, 0f, 100f)]
    [InlineData(250f, 0f, 100f)]
    [InlineData(-400f, 100f, 100f)]
    [InlineData(37.5f, -12f, 200f)]
    [InlineData(-512f, 0f, 400f)]
    public void MapCoordinateMatchesTheUsualFormula(float world, float offset, float sizeFactor)
    {
        // The common map-coordinate formula (0.02 per yalm, 20.48 / scale + 1 at the centre).
        var usual = 0.02f * (world + offset) + 2048f / sizeFactor + 1f;
        Assert.Equal(usual, MapMath.ToMapCoordinate(world, offset, sizeFactor), 1);
    }

    [Fact]
    public void TextureEdgesAreMapCoordinatesOneAndFortyTwoAtScaleOne()
    {
        Assert.Equal(1f, MapMath.ToMapCoordinate(-1024f, 0f, 100f), 3);
        Assert.Equal(42f, MapMath.ToMapCoordinate(1024f, 0f, 100f), 3);
    }

    // A map window: image node 600 x 500 screen pixels at (100, 50), the component frame 10 px inside it, UI scale 1.5.
    private static MapFrame Frame(float zoom = 0.25f, float panX = 0f, float panY = 0f, float uiScale = 1.5f,
        float left = 100f, float top = 50f, float width = 600f, float height = 500f, Vector4 part = default) =>
        new(new Vector2(left, top), new Vector2(left + width, top + height),
            new Vector2(left + 10f, top + 10f), new Vector2(left + width - 10f, top + height - 10f),
            uiScale, zoom, new Vector2(panX, panY), part);

    private static MapView View(MapFrame frame, int model = 1, float offsetX = 0f, float offsetY = 0f, float sizeFactor = 100f) =>
        MapView.From(frame, model, offsetX, offsetY, sizeFactor);

    [Fact]
    public void TheMapCentreIsAtTheImageCentreWithoutPan()
    {
        var view = View(Frame());
        // World (0, 0) at sf 100 is texture (1024, 1024), the middle of the 600 x 500 image at (100, 50).
        Assert.Equal(new Vector2(400f, 300f), view.ToScreen(0f, 0f));
        // +x is right, +z is down; 100 yalms = 100 texels = 100 * 0.25 zoom * 1.5 UI scale screen pixels.
        Assert.Equal(new Vector2(437.5f, 337.5f), view.ToScreen(100f, 100f));
    }

    [Fact]
    public void ResizingAndMovingTheWindowMovesTheMarkers()
    {
        var small = View(Frame());
        var large = View(Frame(left: 300f, top: 120f, width: 900f, height: 700f));
        // The centre follows the image; the scale stays (same zoom): a point keeps its offset from the centre.
        Assert.Equal(new Vector2(750f, 470f), large.ToScreen(0f, 0f));
        Assert.Equal(small.ToScreen(200f, -80f) - small.ToScreen(0f, 0f), large.ToScreen(200f, -80f) - large.ToScreen(0f, 0f));
        // A larger window scale (or UI scale) spreads the points.
        var scaled = View(Frame(uiScale: 3f));
        Assert.Equal(2f * (small.ToScreen(200f, -80f) - small.ToScreen(0f, 0f)), scaled.ToScreen(200f, -80f) - scaled.ToScreen(0f, 0f));
    }

    [Fact]
    public void ZoomScalesAroundTheCentre()
    {
        var far = View(Frame(zoom: 0.25f));
        var near = View(Frame(zoom: 1f));
        Assert.Equal(far.ToScreen(0f, 0f), near.ToScreen(0f, 0f));
        Assert.Equal(150f, near.ToScreen(100f, 0f).X - near.ToScreen(0f, 0f).X, 3);
        Assert.Equal(37.5f, far.ToScreen(100f, 0f).X - far.ToScreen(0f, 0f).X, 3);
        Assert.Equal(150f, near.ScreenPixels(100f), 3);
    }

    [Fact]
    public void PanMovesEveryPointByThePanTimesTheScale()
    {
        var still = View(Frame(zoom: 0.5f));
        // Pan in texture pixels, added (model 1) or subtracted (model 2): 40 texels * 0.5 * 1.5 = 30 screen pixels.
        var added = View(Frame(zoom: 0.5f, panX: 40f, panY: -20f), 1);
        var subtracted = View(Frame(zoom: 0.5f, panX: 40f, panY: -20f), 2);
        Assert.Equal(still.ToScreen(10f, 10f) + new Vector2(30f, -15f), added.ToScreen(10f, 10f));
        Assert.Equal(still.ToScreen(10f, 10f) - new Vector2(30f, -15f), subtracted.ToScreen(10f, 10f));
        // Pan in node pixels (models 3 and 4): divided by the zoom first.
        var nodePan = View(Frame(zoom: 0.5f, panX: 20f, panY: -10f), 3);
        Assert.Equal(added.ToScreen(10f, 10f), nodePan.ToScreen(10f, 10f));
    }

    [Fact]
    public void TheImagePartModelStretchesTheShownTexturePart()
    {
        // The image shows texels 512..1712 x 600..1600 (1200 x 1000) over 600 x 500 screen pixels: 0.5 px per texel.
        var view = View(Frame(part: new Vector4(512f, 600f, 1200f, 1000f)), 0);
        Assert.True(view.Valid);
        Assert.Equal(new Vector2(100f + 512f * 0.5f, 50f + 424f * 0.5f), view.ToScreen(0f, 0f));
        Assert.False(View(Frame(), 0).Valid);
    }

    [Fact]
    public void PerZoneScaleAndOffsetApply()
    {
        var a = View(Frame(zoom: 0.5f), offsetX: -20f, offsetY: 15f, sizeFactor: 100f);
        var b = View(Frame(zoom: 0.5f), offsetX: -20f, offsetY: 15f, sizeFactor: 200f);
        Assert.Equal(new Vector2(400f, 300f), a.ToScreen(20f, -15f));
        Assert.Equal(new Vector2(400f, 300f), b.ToScreen(20f, -15f));
        Assert.Equal(37.5f, a.ToScreen(70f, -15f).X - a.ToScreen(20f, -15f).X, 3);
        Assert.Equal(75f, b.ToScreen(70f, -15f).X - b.ToScreen(20f, -15f).X, 3);
        Assert.Equal(37.5f, a.ScreenPixels(50f), 3);
        Assert.Equal(75f, b.ScreenPixels(50f), 3);
    }

    [Fact]
    public void ClipsToTheMapImageInsideTheFrame()
    {
        var view = View(Frame());
        Assert.Equal(new Vector2(110f, 60f), view.ClipMin);
        Assert.Equal(new Vector2(690f, 540f), view.ClipMax);
        Assert.True(view.Visible(new Vector2(110f, 60f)));
        Assert.False(view.Visible(new Vector2(105f, 300f)));
        Assert.True(view.Visible(new Vector2(107f, 300f), 4f));
        Assert.False(view.Visible(new Vector2(300f, 550f), 4f));
        // Without a frame the image rectangle is the clip.
        var noFrame = new MapFrame(new Vector2(100f, 50f), new Vector2(700f, 550f), Vector2.Zero, Vector2.Zero, 1.5f, 0.25f, Vector2.Zero, default);
        Assert.Equal(new Vector2(700f, 550f), View(noFrame).ClipMax);
    }

    [Fact]
    public void InvalidViewsAreRejected()
    {
        Assert.True(View(Frame()).Valid);
        Assert.False(View(Frame(), sizeFactor: 0f).Valid);
        Assert.False(View(Frame(zoom: 0f)).Valid);
        Assert.False(View(Frame(uiScale: float.NaN)).Valid);
        Assert.False(View(Frame(width: 0f)).Valid);
    }

    [Fact]
    public void TheArrowPicksTheModelThatFits()
    {
        // The game draws the arrow where model 2 (pan subtracted) puts the player: the check picks model 2, gap 0.
        var frame = Frame(zoom: 0.5f, panX: 120f, panY: 60f);
        var player = new Vector2(150f, -90f);
        var arrow = View(frame, 2).ToScreen(player.X, player.Y);
        var gaps = new float[MapModels.Count];
        var best = MapCalibration.Best(frame, 0f, 0f, 100f, player, arrow, gaps);
        Assert.Equal(2, best);
        Assert.Equal(0f, gaps[2], 3);
        Assert.True(gaps[1] > 50f);
        Assert.True(float.IsNaN(gaps[0]));
        Assert.Equal(2, MapCalibration.Choose(best, gaps[2], -1, frame));
        // A far arrow does not pick: the last pick stays, else the default.
        Assert.Equal(5, MapCalibration.Choose(1, 500f, 5, frame));
        Assert.Equal(MapModels.Default(frame), MapCalibration.Choose(-1, float.MaxValue, -1, frame));
    }

    [Fact]
    public void AlignmentShiftsOnlyWithinLimits()
    {
        var view = View(Frame());
        var player = new Vector2(0f, 0f);
        var predicted = view.ToScreen(0f, 0f);
        Assert.Equal(view, view.AlignedTo(player, predicted + new Vector2(2f, 0f), 3f, 60f, out var small));
        Assert.Equal(2f, small, 3);
        var moved = view.AlignedTo(player, predicted + new Vector2(30f, -40f), 3f, 60f, out var gap);
        Assert.Equal(50f, gap, 3);
        Assert.Equal(predicted + new Vector2(30f, -40f), moved.ToScreen(0f, 0f));
        Assert.Equal(view, view.AlignedTo(player, predicted + new Vector2(500f, 0f), 3f, 60f, out _));
    }

    [Fact]
    public void DefaultFilterShowsTheNeededStatusesOnly()
    {
        var filter = new PluginConfiguration().OverlayFeatures.MapFilter;
        Assert.False(filter.ShowsStatus(CoverageStatus.Confirmed));
        Assert.True(filter.ShowsStatus(CoverageStatus.ToConfirm));
        Assert.True(filter.ShowsStatus(CoverageStatus.WalkPast));
        Assert.True(filter.ShowsStatus(CoverageStatus.NeverSeen));
        Assert.False(filter.ShowsStatus(CoverageStatus.Unknown));
        Assert.True(filter.SpawnsNowOnly);
        Assert.False(filter.PendingOnly);
    }

    [Fact]
    public void SpawnsNowIsOnByDefaultForTheListAndTheWorldMarkers()
    {
        var config = new PluginConfiguration { OverlayEnabled = true, OverlayNearbyList = true };
        Assert.True(config.OverlayFeatures.ListSpawnsNow);
        Assert.True(config.OverlayFeatures.WorldSpawnsNow);
        config.OverlayListSpawnsNow = false;
        config.OverlayWorldSpawnsNow = false;
        Assert.False(config.OverlayFeatures.ListSpawnsNow);
        Assert.False(config.OverlayFeatures.WorldSpawnsNow);
        // The master switch and the list switch gate them.
        Assert.False(new PluginConfiguration().OverlayFeatures.ListSpawnsNow);
        Assert.False(new PluginConfiguration().OverlayFeatures.WorldSpawnsNow);
        Assert.False(new PluginConfiguration { OverlayEnabled = true }.OverlayFeatures.ListSpawnsNow);
    }

    [Fact]
    public void SpawnsNowRuleKeepsThePickedPointAndUnknownConditions()
    {
        Assert.False(SpawnsNow.Shows(true, ConditionState.Unavailable, false));
        Assert.True(SpawnsNow.Shows(true, ConditionState.Unavailable, true));
        Assert.True(SpawnsNow.Shows(true, ConditionState.Unknown, false));
        Assert.True(SpawnsNow.Shows(true, ConditionState.Available, false));
        Assert.True(SpawnsNow.Shows(false, ConditionState.Unavailable, false));
        // The map filter uses the same rule, the picked point included.
        var filter = new PluginConfiguration().OverlayFeatures.MapFilter;
        Assert.True(filter.Shows(CoverageStatus.ToConfirm, ConditionState.Unavailable, false, true));
    }

    [Fact]
    public void OldConfigThatStoredTheOldDefaultGetsSpawnsNowOn()
    {
        // A 0.0.0.16 config saved before the change: no overlay settings version, the map filter stored as false.
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginConfiguration>(
            "{\"OverlayEnabled\":true,\"OverlayMap\":true,\"OverlayMapSpawnsNow\":false}")!;
        Assert.False(old.OverlayMapSpawnsNow);
        Assert.True(old.MigrateOverlay());
        Assert.True(old.OverlayMapSpawnsNow);
        Assert.Equal(PluginConfiguration.CurrentOverlayVersion, old.OverlaySettingsVersion);
        Assert.True(old.OverlayListSpawnsNow); // keys it never had take the new defaults
        Assert.True(old.OverlayWorldSpawnsNow);
        // A choice made after the migration is kept.
        old.OverlayMapSpawnsNow = false;
        Assert.False(old.MigrateOverlay());
        Assert.False(old.OverlayMapSpawnsNow);
        var saved = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginConfiguration>(Newtonsoft.Json.JsonConvert.SerializeObject(old))!;
        Assert.False(saved.MigrateOverlay());
        Assert.False(saved.OverlayMapSpawnsNow);
        // A fresh config is on and lands on the current version.
        var fresh = new PluginConfiguration();
        fresh.MigrateOverlay();
        Assert.True(fresh.OverlayMapSpawnsNow);
        Assert.Equal(PluginConfiguration.CurrentOverlayVersion, fresh.OverlaySettingsVersion);
    }

    [Fact]
    public void SpawnsNowOnlyHidesUnmetConditionsButNotUnknownOnes()
    {
        var filter = new MapFilter(false, true, true, true, true, false);
        Assert.False(filter.Shows(CoverageStatus.ToConfirm, ConditionState.Unavailable, false, false));
        Assert.True(filter.Shows(CoverageStatus.ToConfirm, ConditionState.Available, false, false));
        Assert.True(filter.Shows(CoverageStatus.ToConfirm, ConditionState.Unknown, false, false));
        Assert.False(filter.Shows(CoverageStatus.Confirmed, ConditionState.Available, false, false));
        // Without the filter an unmet condition is still drawn (faded by the drawing).
        Assert.True(new MapFilter(false, true, true, true, false, false).Shows(CoverageStatus.ToConfirm, ConditionState.Unavailable, false, false));
    }

    [Fact]
    public void PendingOnlyKeepsJustTheRespawnsBeingWaitedFor()
    {
        var filter = new MapFilter(false, true, true, true, false, true);
        Assert.False(filter.Shows(CoverageStatus.ToConfirm, ConditionState.Available, false, false));
        Assert.True(filter.Shows(CoverageStatus.ToConfirm, ConditionState.Available, true, false));
        // The status boxes still apply.
        Assert.False(filter.Shows(CoverageStatus.Confirmed, ConditionState.Available, true, false));
        // Both filters together.
        var both = filter with { SpawnsNowOnly = true };
        Assert.False(both.Shows(CoverageStatus.WalkPast, ConditionState.Unavailable, true, false));
    }

    [Fact]
    public void ThePickedPointIsAlwaysShown()
    {
        var filter = new MapFilter(false, false, false, false, true, true);
        Assert.True(filter.Shows(CoverageStatus.Confirmed, ConditionState.Unavailable, false, true));
    }

    [Fact]
    public void MapSettingsAreOffByDefaultAndClamped()
    {
        var config = new PluginConfiguration();
        Assert.False(config.OverlayFeatures.Map);
        config.OverlayMap = true;
        Assert.False(config.OverlayFeatures.Map); // master switch off
        config.OverlayEnabled = true;
        Assert.True(config.OverlayFeatures.Map);
        Assert.True(config.OverlayFeatures.NeedsScan);
        Assert.False(config.OverlayFeatures.DrawsWorld); // the map alone draws nothing in the world
        Assert.Equal(MapRates.DefaultMaxMarkers, config.OverlayFeatures.MapMaxMarkers);
        config.OverlayMapMaxMarkers = 5;
        Assert.Equal(MapRates.MinMaxMarkers, config.OverlayFeatures.MapMaxMarkers);
        config.OverlayMapMaxMarkers = 100_000;
        Assert.Equal(MapRates.MaxMaxMarkers, config.OverlayFeatures.MapMaxMarkers);
    }

    [Fact]
    public void TheCapCoversAFullZone()
    {
        // About 1,200 points per zone: the buffer holds all of them; the view cap draws the nearest.
        Assert.True(MapRates.MaxMaxMarkers >= 1200);
        Assert.InRange(MapRates.DefaultMaxMarkers, MapRates.MinMaxMarkers, MapRates.MaxMaxMarkers);
    }

#if HAS_LUMINA
    private const string SteamSqpack = @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY XIV Online\game\sqpack";

    private static readonly Lazy<GameData?> Game = new(() =>
    {
        var path = Environment.GetEnvironmentVariable("XIV_SQPACK");
        if (string.IsNullOrWhiteSpace(path))
            path = SteamSqpack;
        return Directory.Exists(path) ? new GameData(path, new LuminaOptions { PanicOnSheetChecksumMismatch = false }) : null;
    });

    [SkippableFact]
    public void EurekaFieldMapsPutTheZoneOnTheTexture()
    {
        var game = Game.Value;
        Skip.If(game == null, "No local game install (set XIV_SQPACK).");
        var territories = game!.GetExcelSheet<TerritoryType>()!;
        foreach (var id in new uint[] { ForayZones.EurekaAnemos, ForayZones.EurekaPagos, ForayZones.EurekaPyros, ForayZones.EurekaHydatos })
        {
            var map = territories.GetRow(id).Map.Value;
            Assert.True(map.SizeFactor > 0);
            // The world centre (minus the offset) is the texture centre, and the texture spans 2048 / scale yalms.
            Assert.Equal(1024f, MapMath.ToTexture(-map.OffsetX, map.OffsetX, map.SizeFactor), 2);
            var span = MapMath.TextureSize / (map.SizeFactor / 100f);
            Assert.Equal(2048f, MapMath.ToTexture(-map.OffsetX + span / 2f, map.OffsetX, map.SizeFactor), 1);
            // The map coordinate of the texture's left edge is 1.0, as on the in-game map.
            Assert.Equal(1f, MapMath.ToMapCoordinate(-map.OffsetX - span / 2f, map.OffsetX, map.SizeFactor), 2);
        }
    }
#endif
}

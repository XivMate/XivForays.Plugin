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

    private static MapView View(float screenX = 100f, float screenY = 50f, float scale = 0.5f, float nodeSize = 2048f,
        float offsetX = 0f, float offsetY = 0f, float sizeFactor = 100f) =>
        MapView.FromNode(screenX, screenY, scale, scale, nodeSize, nodeSize, offsetX, offsetY, sizeFactor,
            new Vector2(100f, 50f), new Vector2(700f, 650f));

    [Fact]
    public void ProjectionFollowsTheMapImageNode()
    {
        var view = View();
        // World (0, 0) at sf 100 is texture (1024, 1024): half-scale node at (100, 50).
        Assert.Equal(new Vector2(612f, 562f), view.ToScreen(0f, 0f));
        // +z is down on the map, +x is right.
        Assert.Equal(new Vector2(662f, 612f), view.ToScreen(100f, 100f));
    }

    [Fact]
    public void ZoomAndPanMoveTheMarkersWithTheNode()
    {
        var zoomed = View(screenX: -400f, screenY: -300f, scale: 1.5f);
        // Texture (1024, 1024) * 1.5 = 1536 from the node origin.
        Assert.Equal(new Vector2(1136f, 1236f), zoomed.ToScreen(0f, 0f));
        var panned = View(screenX: 140f, screenY: 20f);
        Assert.Equal(View().ToScreen(10f, 10f) + new Vector2(40f, -30f), panned.ToScreen(10f, 10f));
    }

    [Fact]
    public void ANodeSmallerThanTheTextureStillCoversTheWholeMap()
    {
        // A 1024 node at scale 1 shows the 2048 texture at half size: same as a 2048 node at 0.5.
        var small = MapView.FromNode(100f, 50f, 1f, 1f, 1024f, 1024f, 0f, 0f, 100f, new Vector2(100f, 50f), new Vector2(700f, 650f));
        Assert.Equal(View().ToScreen(-200f, 300f), small.ToScreen(-200f, 300f));
    }

    [Fact]
    public void PerZoneScaleAndOffsetApply()
    {
        // Two zones with the same node: a larger size factor spreads the same yalms over more pixels.
        var a = View(sizeFactor: 100f, offsetX: -20f, offsetY: 15f);
        var b = View(sizeFactor: 200f, offsetX: -20f, offsetY: 15f);
        Assert.Equal(new Vector2(612f, 562f), a.ToScreen(20f, -15f));
        Assert.Equal(new Vector2(612f, 562f), b.ToScreen(20f, -15f));
        Assert.Equal(25f, a.ToScreen(70f, -15f).X - a.ToScreen(20f, -15f).X, 3);
        Assert.Equal(50f, b.ToScreen(70f, -15f).X - b.ToScreen(20f, -15f).X, 3);
        Assert.Equal(25f, a.ScreenPixels(50f), 3);
        Assert.Equal(50f, b.ScreenPixels(50f), 3);
    }

    [Fact]
    public void CullsToTheVisibleMapRectangle()
    {
        var view = View();
        Assert.True(view.Visible(new Vector2(100f, 50f)));
        Assert.True(view.Visible(new Vector2(700f, 650f)));
        Assert.False(view.Visible(new Vector2(99f, 300f)));
        Assert.True(view.Visible(new Vector2(97f, 300f), 4f));
        Assert.False(view.Visible(new Vector2(300f, 660f), 4f));
    }

    [Fact]
    public void InvalidViewsAreRejected()
    {
        Assert.True(View().Valid);
        Assert.False(View(sizeFactor: 0f).Valid);
        Assert.False(View(scale: 0f).Valid);
        Assert.False(MapView.FromNode(float.NaN, 0f, 1f, 1f, 2048f, 2048f, 0f, 0f, 100f, Vector2.Zero, Vector2.One).Valid);
        Assert.False(MapView.FromNode(0f, 0f, 1f, 1f, 2048f, 2048f, 0f, 0f, 100f, Vector2.One, Vector2.One).Valid);
    }

    [Fact]
    public void AlignmentShiftsOnlyWithinLimits()
    {
        var view = View();
        var player = new Vector2(0f, 0f);
        var predicted = view.ToScreen(0f, 0f);
        Assert.Equal(view, view.AlignedTo(player, predicted + new Vector2(2f, 0f), 3f, 200f, out var small));
        Assert.Equal(2f, small, 3);
        var moved = view.AlignedTo(player, predicted + new Vector2(30f, -40f), 3f, 200f, out var gap);
        Assert.Equal(50f, gap, 3);
        Assert.Equal(predicted + new Vector2(30f, -40f), moved.ToScreen(0f, 0f));
        Assert.Equal(view, view.AlignedTo(player, predicted + new Vector2(500f, 0f), 3f, 200f, out _));
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

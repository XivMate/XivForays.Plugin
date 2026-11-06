#if HAS_LUMINA
using Lumina;
using Lumina.Excel.Sheets;
using XivMate.DataGathering.Forays.Dalamud.Core;
using Xunit;
using Xunit.Abstractions;

namespace XivForays.Plugin.Tests;

/// <summary>
/// Checks the zone gate against the real TerritoryType and Map sheets. Needs a local game install:
/// set XIV_SQPACK to the game\sqpack folder, or the default Steam path is used. Skipped when absent (CI).
/// </summary>
public class GameSheetTerritoryTests(ITestOutputHelper output)
{
    private const string SteamSqpack = @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY XIV Online\game\sqpack";

    private static readonly Lazy<GameData?> Game = new(() =>
    {
        var path = Environment.GetEnvironmentVariable("XIV_SQPACK");
        if (string.IsNullOrWhiteSpace(path))
            path = SteamSqpack;
        return Directory.Exists(path) ? new GameData(path, new LuminaOptions { PanicOnSheetChecksumMismatch = false }) : null;
    });

    // Territory -> (internal level name, English place name). Bg paths identify the content unambiguously.
    private static readonly Dictionary<uint, (string Name, string Place)> Expected = new()
    {
        [732] = ("z3fa", "Eureka Anemos"),
        [763] = ("z3fb", "Eureka Pagos"),
        [795] = ("z3fc", "Eureka Pyros"),
        [827] = ("z3fd", "Eureka Hydatos"),
        [920] = ("n4b4", "Bozjan Southern Front"),
        [975] = ("n4b6", "Zadnor"),
        [936] = ("n4b5", "Delubrum Reginae"),
        [937] = ("n4b5_2", "Delubrum Reginae"),
        [1252] = ("o6b1", "South Horn"),
        [1346] = ("o6b2", "North Horn"),
    };

    [SkippableFact]
    public void GateIdsAreTheForayTerritoriesInTheGameSheets()
    {
        var game = Game.Value;
        Skip.If(game == null, "No local game install (set XIV_SQPACK).");
        var territories = game!.GetExcelSheet<TerritoryType>()!;
        var maps = game.GetExcelSheet<Map>()!;

        Assert.Equal(Expected.Keys.Order(), ForayZones.All.Order());
        foreach (var (id, (name, place)) in Expected)
        {
            var row = territories.GetRow(id);
            var placeName = row.PlaceName.Value.Name.ExtractText();
            output.WriteLine($"{id}\t{row.Name.ExtractText()}\t{placeName}\tdefault map {row.Map.RowId}\t{row.ContentFinderCondition.ValueNullable?.Name.ExtractText()}");
            Assert.Equal(name, row.Name.ExtractText());
            Assert.Equal(place, placeName);
            Assert.True(ForayZones.IsRecordable(id, row.Map.RowId, row.Map.Value.TerritoryType.RowId),
                $"default map of {id} must be recordable");
        }

        // Every map layer of a gated zone is recordable: BA, the Dalriada, Castrum Lacus Litore, Delubrum floors, Forked Tower.
        foreach (var map in maps.Where(m => ForayZones.IsForayTerritory(m.TerritoryType.RowId)))
        {
            output.WriteLine($"  map {map.RowId}\tterritory {map.TerritoryType.RowId}\t{map.PlaceName.ValueNullable?.Name.ExtractText()} / {map.PlaceNameSub.ValueNullable?.Name.ExtractText()}");
            Assert.True(ForayZones.IsRecordable(map.TerritoryType.RowId, map.RowId, map.TerritoryType.RowId));
        }

        var delubrumMap = territories.GetRow(ForayZones.DelubrumReginaeSavage).Map.Value;
        Assert.True(ForayZones.IsRecordable(ForayZones.DelubrumReginaeSavage, delubrumMap.RowId, delubrumMap.TerritoryType.RowId));

        string MapPlace(Map m) => m.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
        Assert.Contains(maps, m => m.TerritoryType.RowId == 975 && MapPlace(m) == "The Dalriada");
        Assert.Contains(maps, m => m.TerritoryType.RowId == 1252 && MapPlace(m) == "Tower of Blood");
        Assert.Contains(maps, m => m.TerritoryType.RowId == 1346 && MapPlace(m) == "Tower of Magic");
        Assert.True(maps.Count(m => m.TerritoryType.RowId == 827) > 1, "Hydatos must have BA map layers");
    }

    [SkippableFact]
    public void EurekaOrthosAndOtherNamesakesAreNotGated()
    {
        var game = Game.Value;
        Skip.If(game == null, "No local game install (set XIV_SQPACK).");
        var territories = game!.GetExcelSheet<TerritoryType>()!;

        var orthos = territories.Where(t => t.PlaceName.ValueNullable?.Name.ExtractText() == "Eureka Orthos").ToList();
        Assert.NotEmpty(orthos);
        foreach (var t in orthos)
        {
            output.WriteLine($"excluded {t.RowId}\t{t.Name.ExtractText()}\tEureka Orthos");
            Assert.False(ForayZones.IsForayTerritory(t.RowId));
        }

        // Completeness: every territory with a foray intended use (Eureka 41, Bozja/Zadnor 48, Delubrum 52/53,
        // Occult Crescent 61) is gated. Quest battles (29) and cutscene copies (15) that reuse the same level
        // files, e.g. 955 "Fit for a Queen" and 964 in Delubrum's level, are deliberately not gated.
        uint[] forayUses = [41, 48, 52, 53, 61];
        foreach (var t in territories.Where(t => forayUses.Contains(t.TerritoryIntendedUse.RowId)))
            Assert.True(ForayZones.IsForayTerritory(t.RowId), $"{t.RowId} {t.Bg.ExtractText()} has a foray intended use but is not gated");
        foreach (var id in ForayZones.All)
            Assert.Contains(territories.GetRow(id).TerritoryIntendedUse.RowId, forayUses);
    }
}
#endif

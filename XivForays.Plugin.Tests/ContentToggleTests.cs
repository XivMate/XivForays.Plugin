using Newtonsoft.Json;
using XivMate.DataGathering.Forays.Dalamud.Core;
using Xunit;
using PluginConfiguration = XivMate.DataGathering.Forays.Dalamud.Configuration.Configuration;

namespace XivForays.Plugin.Tests;

/// <summary>The per-content recording toggles (Record in: Eureka / Bozja / Occult Crescent) and the gate they feed.</summary>
public class ContentToggleTests
{
    public enum Content { Eureka, Bozja, OccultCrescent }

    private static readonly (uint Territory, Content Content)[] Zones =
    [
        (ForayZones.EurekaAnemos, Content.Eureka),
        (ForayZones.EurekaPagos, Content.Eureka),
        (ForayZones.EurekaPyros, Content.Eureka),
        (ForayZones.EurekaHydatos, Content.Eureka),
        (ForayZones.BozjanSouthernFront, Content.Bozja),
        (ForayZones.Zadnor, Content.Bozja),
        (ForayZones.DelubrumReginae, Content.Bozja),
        (ForayZones.DelubrumReginaeSavage, Content.Bozja),
        (ForayZones.SouthHorn, Content.OccultCrescent),
        (ForayZones.NorthHorn, Content.OccultCrescent)
    ];

    public static TheoryData<uint, Content, Content> EveryZoneTimesEveryToggle()
    {
        var data = new TheoryData<uint, Content, Content>();
        foreach (var (territory, content) in Zones)
            foreach (var toggle in Enum.GetValues<Content>())
                data.Add(territory, content, toggle);
        return data;
    }

    [Fact]
    public void TheTableCoversTheWholeGate() =>
        Assert.Equal(ForayZones.All.Order(), Zones.Select(z => z.Territory).Order());

    [Theory]
    [MemberData(nameof(EveryZoneTimesEveryToggle))]
    public void UntickingOneContentClosesOnlyItsOwnZones(uint territory, Content zoneContent, Content unticked)
    {
        var config = new PluginConfiguration
        {
            RecordEureka = unticked != Content.Eureka,
            RecordBozja = unticked != Content.Bozja,
            RecordOccultCrescent = unticked != Content.OccultCrescent
        };

        Assert.Equal(zoneContent != unticked, config.RecordsTerritory(territory));
        Assert.Equal(zoneContent != unticked, ForayZones.IsContentEnabled(territory,
            config.RecordEureka, config.RecordBozja, config.RecordOccultCrescent));
    }

    [Theory]
    [MemberData(nameof(EveryZoneTimesEveryToggle))]
    public void TickingOnlyOneContentOpensOnlyItsOwnZones(uint territory, Content zoneContent, Content ticked)
    {
        var config = new PluginConfiguration
        {
            RecordEureka = ticked == Content.Eureka,
            RecordBozja = ticked == Content.Bozja,
            RecordOccultCrescent = ticked == Content.OccultCrescent
        };

        Assert.Equal(zoneContent == ticked, config.RecordsTerritory(territory));
        Assert.False(config.RecordsNothing);
    }

    [Fact]
    public void AllTickedRecordsEveryZoneAndAllUntickedRecordsNone()
    {
        var on = new PluginConfiguration();
        var off = new PluginConfiguration { RecordEureka = false, RecordBozja = false, RecordOccultCrescent = false };
        foreach (var territory in ForayZones.All)
        {
            Assert.True(on.RecordsTerritory(territory));
            Assert.False(off.RecordsTerritory(territory));
        }

        Assert.False(on.RecordsNothing);
        Assert.True(off.RecordsNothing);
    }

    [Theory]
    [InlineData(129u)] // Limsa Lominsa Lower Decks
    [InlineData(1099u)] // Eureka Orthos is a deep dungeon, never gated in
    [InlineData(0u)]
    public void NonForayTerritoriesAreNeverEnabled(uint territory)
    {
        Assert.False(new PluginConfiguration().RecordsTerritory(territory));
        Assert.False(ForayZones.IsContentEnabled(territory, true, true, true));
    }

    [Fact]
    public void ANewConfigurationTicksAllThree()
    {
        var config = new PluginConfiguration();
        Assert.True(config.RecordEureka);
        Assert.True(config.RecordBozja);
        Assert.True(config.RecordOccultCrescent);
    }

    // A 0.0.0.14 config as Dalamud saved it (Newtonsoft, TypeNameHandling.Objects): no Record* keys.
    private const string SavedBy014 = """
        {
          "$type": "XivMate.DataGathering.Forays.Dalamud.Configuration.Configuration, XivMate.DataGathering.Forays.Dalamud",
          "Version": 0,
          "SystemConfiguration": {
            "$type": "XivMate.DataGathering.Forays.Dalamud.Configuration.SystemConfiguration, XivMate.DataGathering.Forays.Dalamud",
            "ApiUrl": "https://example.invalid/api/",
            "ApiKey": ""
          },
          "CanCrowdsourceData": true,
          "CapturePass": false,
          "LegacyEnemySnapshots": false
        }
        """;

    [Fact]
    public void AnExistingSavedConfigWithoutTheKeysLoadsAllTicked()
    {
        // TypeNameHandling.None: the test assembly cannot resolve the plugin's $type, the members are what matter.
        var config = JsonConvert.DeserializeObject<PluginConfiguration>(SavedBy014,
            new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None })!;

        Assert.True(config.RecordEureka);
        Assert.True(config.RecordBozja);
        Assert.True(config.RecordOccultCrescent);
        Assert.False(config.RecordsNothing);
    }

    [Fact]
    public void AnUntickedContentSurvivesASaveAndLoad()
    {
        var saved = JsonConvert.SerializeObject(new PluginConfiguration { RecordBozja = false });
        var config = JsonConvert.DeserializeObject<PluginConfiguration>(saved)!;

        Assert.True(config.RecordEureka);
        Assert.False(config.RecordBozja);
        Assert.True(config.RecordOccultCrescent);
    }
}

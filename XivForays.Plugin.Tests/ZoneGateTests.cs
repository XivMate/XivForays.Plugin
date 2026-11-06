using XivMate.DataGathering.Forays.Dalamud.Core;
using Xunit;

namespace XivForays.Plugin.Tests;

public class ZoneGateTests
{
    [Fact]
    public void GateIsExactlyTheForayTerritories()
    {
        uint[] expected = [732, 763, 795, 827, 920, 975, 936, 937, 1252, 1346];
        Assert.Equal(expected.Order(), ForayZones.All.Order());
    }

    [Theory]
    [InlineData(1099u)] [InlineData(1100u)] [InlineData(1101u)] [InlineData(1102u)] [InlineData(1103u)]
    [InlineData(1104u)] [InlineData(1105u)] [InlineData(1106u)] [InlineData(1107u)] [InlineData(1108u)]
    [InlineData(1124u)]
    public void EurekaOrthosIsExcluded(uint territory)
    {
        Assert.False(ForayZones.IsForayTerritory(territory));
        Assert.False(ForayZones.IsEureka(territory));
        Assert.False(ForayZones.IsRecordable(territory, 820, territory));
    }

    [Theory]
    // Default maps.
    [InlineData(732u, 414u, 732u, true)]
    [InlineData(920u, 606u, 920u, true)]
    [InlineData(975u, 665u, 975u, true)]
    [InlineData(1252u, 967u, 1252u, true)]
    [InlineData(1346u, 1135u, 1346u, true)]
    // Other map layers of the same zone: BA (827), Castrum Lacus Litore (920), the Dalriada (975), Forked Tower.
    [InlineData(827u, 520u, 827u, true)]
    [InlineData(827u, 527u, 827u, true)]
    [InlineData(920u, 616u, 920u, true)]
    [InlineData(975u, 673u, 975u, true)]
    [InlineData(1252u, 968u, 1252u, true)]
    [InlineData(1346u, 1136u, 1346u, true)]
    // Delubrum: normal and Savage share map rows owned by 936.
    [InlineData(936u, 641u, 936u, true)]
    [InlineData(937u, 641u, 936u, true)]
    [InlineData(937u, 641u, 937u, false)]
    // Loading (map 0), a map from another zone, a non-foray zone.
    [InlineData(827u, 0u, 827u, false)]
    [InlineData(827u, 414u, 732u, false)]
    [InlineData(129u, 15u, 129u, false)]
    public void RecordableAcceptsEveryMapLayerOfTheZoneOnly(uint territory, uint map, uint mapTerritory, bool expected) =>
        Assert.Equal(expected, ForayZones.IsRecordable(territory, map, mapTerritory));

    [Theory]
    [InlineData(920u, true)]
    [InlineData(975u, true)]
    [InlineData(1252u, true)]
    [InlineData(1346u, true)]
    [InlineData(936u, false)]
    [InlineData(937u, false)]
    [InlineData(827u, false)]
    public void CriticalEngagementsAreSampledInBozjaFieldsAndTheHorns(uint territory, bool expected) =>
        Assert.Equal(expected, ForayZones.HasCriticalEngagements(territory));

    [Fact]
    public void ZoneFamiliesDoNotOverlap()
    {
        foreach (var territory in ForayZones.All)
        {
            var families = new[] { ForayZones.IsEureka(territory), ForayZones.IsBozja(territory), ForayZones.IsOccultCrescent(territory) };
            Assert.Equal(1, families.Count(x => x));
        }
    }
}

using XivMate.DataGathering.Forays.Dalamud.Core;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>The calculated weather against the website's forecast (web/src/eorzea.ts weatherAt) values.</summary>
public class CalculatedWeatherTests
{
    // Hydatos as the site lists it: Fair 12, Showers 22, Gloom 22, Thunderstorms 22, Snow 22 (ids are arbitrary here).
    private static readonly (byte, byte)[] Hydatos = [(1, 12), (2, 22), (3, 22), (4, 22), (5, 22)];
    private static readonly (byte, byte)[] Pagos = [(1, 10), (2, 18), (3, 18), (4, 18), (5, 18), (6, 18)];

    // Time hash and the Hydatos weather the site gives at the same instant, cross-checked by running the site code.
    [Theory]
    [InlineData(1763060000000L, 3)] // target 55, Gloom
    [InlineData(1700000000000L, 3)] // target 53, Gloom
    [InlineData(1800000000123L, 5)] // target 78, Snow
    [InlineData(1793940205000L, 5)] // target 82, Snow
    [InlineData(1763064000000L, 5)] // target 91, Snow
    [InlineData(1763100000000L, 4)] // target 58, Thunderstorms
    public void HydatosMatchesTheSiteForecast(long ms, byte expected) => Assert.Equal(expected, CalculatedWeather.Pick(ms, Hydatos));

    [Fact]
    public void PagosTargetFiftyFiveIsTheFourthEntry() => Assert.Equal(4, CalculatedWeather.Pick(1763060000000L, Pagos));

    [Fact]
    public void ThePickAgreesWithTheNamedForecastEverywhere()
    {
        string[] names = ["", "Fair", "Showers", "Gloom", "Thunderstorms", "Snow"];
        for (var ms = 1_700_000_000_000L; ms < 1_700_000_000_000L + 400 * EorzeaClock.WeatherPeriodMs; ms += EorzeaClock.WeatherPeriodMs)
            Assert.Equal(EorzeaClock.WeatherAt(ForayZones.EurekaHydatos, ms), names[CalculatedWeather.Pick(ms, Hydatos)]);
    }

    [Fact]
    public void TheWeatherIsConstantInsideAnEightHourEtPeriod()
    {
        var start = 1763060000000L / EorzeaClock.WeatherPeriodMs * EorzeaClock.WeatherPeriodMs;
        var first = CalculatedWeather.Pick(start, Hydatos);
        Assert.Equal(first, CalculatedWeather.Pick(start + EorzeaClock.WeatherPeriodMs - 1, Hydatos));
    }

    [Fact]
    public void AnEmptyOrZeroTableGivesNoWeather()
    {
        Assert.Equal(0, CalculatedWeather.Pick(1763060000000L, ReadOnlySpan<(byte, byte)>.Empty));
        Assert.Equal(0, CalculatedWeather.Pick(1763060000000L, [(1, 0), (2, 0)]));
    }
}

#if HAS_LUMINA
public class CalculatedWeatherSheetTests
{
    private const string SteamSqpack = @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY XIV Online\game\sqpack";

    // Reads the real Hydatos rate table from the game sheets and checks it against the site's weather names.
    [Fact]
    public void TheHydatosSheetTableGivesTheSiteForecast()
    {
        var path = Environment.GetEnvironmentVariable("XIV_SQPACK");
        if (string.IsNullOrWhiteSpace(path))
            path = SteamSqpack;
        if (!Directory.Exists(path))
            return;
        var game = new Lumina.GameData(path, new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false });
        var territory = game.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!.GetRow(ForayZones.EurekaHydatos);
        var rates = game.GetExcelSheet<Lumina.Excel.Sheets.WeatherRate>()!.GetRow(territory.WeatherRate.RowId);
        var table = new List<(byte, byte)>();
        for (var i = 0; i < rates.Weather.Count && i < rates.Rate.Count; i++)
            table.Add(((byte)rates.Weather[i].RowId, rates.Rate[i]));
        var weather = game.GetExcelSheet<Lumina.Excel.Sheets.Weather>()!;
        for (var ms = 1_700_000_000_000L; ms < 1_700_000_000_000L + 400 * EorzeaClock.WeatherPeriodMs; ms += EorzeaClock.WeatherPeriodMs)
        {
            var name = weather.GetRow(CalculatedWeather.Pick(ms, table.ToArray())).Name.ExtractText();
            Assert.Equal(ConditionCheck.Normalize(EorzeaClock.WeatherAt(ForayZones.EurekaHydatos, ms)!), ConditionCheck.Normalize(name));
        }
    }
}
#endif

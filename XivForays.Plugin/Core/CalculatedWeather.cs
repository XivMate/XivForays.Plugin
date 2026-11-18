using System;
namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>
/// The weather the game's own forecast gives for a zone, calculated from the UTC time and the zone's weather rate
/// table (TerritoryType.WeatherRate, read from the game sheets at runtime). The client's displayed weather can be
/// overridden (for example while the Ovni NM is up in Hydatos); the calculated weather cannot.
/// </summary>
internal static class CalculatedWeather
{
    /// <summary>
    /// The weather id at a time: the first table entry whose cumulative rate is above the 0 to 99 time hash.
    /// Entries with a zero rate or a zero weather id are skipped. Zero when the table is empty or has no match.
    /// </summary>
    public static byte Pick(long unixMs, ReadOnlySpan<(byte WeatherId, byte Rate)> table)
    {
        var target = EorzeaClock.WeatherTarget(unixMs);
        var cumulative = 0;
        foreach (var (weatherId, rate) in table)
        {
            if (rate == 0)
                continue;
            cumulative += rate;
            if (target < cumulative)
                return weatherId;
        }

        return 0;
    }
}

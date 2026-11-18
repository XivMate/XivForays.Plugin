using System;
using System.Globalization;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

// Collector overlay: Eorzea time and the Eureka weather forecast, the same maths as the website (web/src/eorzea.ts),
// dependency-free so it can be unit tested. Used only to say when a night or weather enemy can be up again.

/// <summary>A run of time when a condition holds (unix ms). Start is in the past when <see cref="Active"/>.</summary>
internal readonly record struct ConditionWindow(long Start, long End, bool Active);

internal static class EorzeaClock
{
    public const long EtHourMs = 175_000;
    public const long EtDayMs = 24 * EtHourMs;
    public const long WeatherPeriodMs = 8 * EtHourMs;
    private const long HalfDayMs = 12 * EtHourMs;
    private const long HorizonMs = 32L * 86_400_000;

    private static readonly (string Weather, int Rate)[] Anemos = [("Fair", 30), ("Gales", 30), ("Showers", 30), ("Snow", 10)];
    private static readonly (string Weather, int Rate)[] Pagos =
        [("Fair", 10), ("Fog", 18), ("Heat", 18), ("Snow", 18), ("Thunder", 18), ("Blizzard", 18)];
    private static readonly (string Weather, int Rate)[] Pyros =
        [("Fair", 10), ("Heat", 18), ("Thunder", 18), ("Blizzard", 18), ("Umbral Wind", 18), ("Snow", 18)];
    private static readonly (string Weather, int Rate)[] Hydatos =
        [("Fair", 12), ("Showers", 22), ("Gloom", 22), ("Thunderstorms", 22), ("Snow", 22)];

    /// <summary>The weather table of a Eureka field zone, or null elsewhere.</summary>
    public static (string Weather, int Rate)[]? Rates(uint territory) => territory switch
    {
        ForayZones.EurekaAnemos => Anemos,
        ForayZones.EurekaPagos => Pagos,
        ForayZones.EurekaPyros => Pyros,
        ForayZones.EurekaHydatos => Hydatos,
        _ => null
    };

    public static int EtHour(long unixMs) => (int)(((unixMs / EtHourMs) % 24 + 24) % 24);

    /// <summary>Night is ET 18:00 to 06:00 (the exporter's boundary).</summary>
    public static bool IsNight(long unixMs) => EtHour(unixMs) is < 6 or >= 18;

    /// <summary>The standard weather hash (0 to 99) of the 8 ET hour period holding this time.</summary>
    public static int WeatherTarget(long unixMs)
    {
        var seconds = unixMs / 1000;
        var bell = seconds / 175;
        var increment = (bell + 8 - bell % 8) % 24;
        var basis = unchecked((uint)(seconds / 4200 * 100 + increment));
        var step1 = unchecked((basis << 11) ^ basis);
        return (int)(((step1 >> 8) ^ step1) % 100);
    }

    /// <summary>The forecast weather ("Fair", "Snow" ...) of a Eureka zone at a time, or null outside Eureka.</summary>
    public static string? WeatherAt(uint territory, long unixMs)
    {
        if (Rates(territory) is not { } rates)
            return null;
        var target = WeatherTarget(unixMs);
        var cumulative = 0;
        foreach (var (weather, rate) in rates)
        {
            cumulative += rate;
            if (target < cumulative)
                return weather;
        }

        return null;
    }

    private static long PeriodStart(long ms) => FloorDiv(ms - 6 * EtHourMs, HalfDayMs) * HalfDayMs + 6 * EtHourMs;

    private static long SegmentStart(long ms) => Math.Max(FloorDiv(ms, WeatherPeriodMs) * WeatherPeriodMs, PeriodStart(ms));

    private static long SegmentEnd(long ms) => Math.Min((FloorDiv(ms, WeatherPeriodMs) + 1) * WeatherPeriodMs, PeriodStart(ms) + HalfDayMs);

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>
    /// The current or next window when the condition holds (port of the site's conditionWindow): adjacent qualifying
    /// segments are merged, the search reaches 32 days. Null when it never holds or the text is not understood.
    /// </summary>
    public static ConditionWindow? Window(uint territory, string condition, long unixMs)
    {
        if (Rates(territory) is not { } rates || !SpawnCondition.TryParse(condition, out var parsed) || parsed.AnyTime)
            return null;
        var any = false;
        var all = true;
        foreach (var (weather, rate) in rates)
        {
            if (rate <= 0)
                continue;
            foreach (var night in new[] { false, true })
            {
                var ok = parsed.Holds(weather, night);
                any |= ok;
                all &= ok;
            }
        }

        if (!any)
            return null;
        if (all)
            return new ConditionWindow(long.MinValue, long.MaxValue, true);

        bool Matches(long at) => parsed.Holds(WeatherAt(territory, at)!, IsNight(at));
        var active = Matches(unixMs);
        var cursor = unixMs;
        while (!Matches(cursor) && cursor < unixMs + HorizonMs)
            cursor = SegmentEnd(cursor);
        if (!Matches(cursor))
            return null;
        var start = active ? SegmentStart(unixMs) : cursor;
        if (active)
            while (start > unixMs - HorizonMs && Matches(start - 1))
                start = SegmentStart(start - 1);
        var end = SegmentEnd(cursor);
        while (end < unixMs + HorizonMs && Matches(end))
            end = SegmentEnd(end);
        return new ConditionWindow(start, end, active);
    }

    /// <summary>Whole minutes until a time, at least 1 (the site's minutesUntil).</summary>
    public static int MinutesUntil(long boundaryMs, long nowMs) => (int)Math.Max(1, (boundaryMs - nowMs + 59_999) / 60_000);

    /// <summary>"18:00": the Eorzea clock at a time.</summary>
    public static string EtText(long unixMs)
    {
        var minute = (int)(((unixMs * 60 / EtHourMs) % 1440 + 1440) % 1440);
        return (minute / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minute % 60).ToString("00", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A coverage condition text ("any time", "night", "day", "weather Blizzard/Snow", "during its FATE (1351)", parts
/// joined by " + ") as time and weather requirements. A FATE part cannot be forecast.
/// </summary>
internal readonly record struct SpawnCondition(bool AnyTime, bool? Night, string[]? Weathers, bool Fate)
{
    public static bool TryParse(string condition, out SpawnCondition parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(condition))
            return false;
        bool? night = null;
        string[]? weathers = null;
        var fate = false;
        var anyTime = true;
        foreach (var raw in condition.Split(" + "))
        {
            var part = raw.Trim();
            if (part == "any time")
                continue;
            anyTime = false;
            if (part is "night" or "day")
                night = part == "night";
            else if (part.StartsWith("weather ", StringComparison.Ordinal))
                weathers = part["weather ".Length..].Replace(',', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            else if (part.StartsWith("during its FATE (", StringComparison.Ordinal))
                fate = true;
            else
                return false;
        }

        parsed = new SpawnCondition(anyTime, night, weathers, fate);
        return true;
    }

    /// <summary>Time and weather can be forecast (no FATE part, at least one of night/day/weather).</summary>
    public bool Forecastable => !AnyTime && !Fate && (Night != null || Weathers != null);

    public bool Holds(string weather, bool night)
    {
        if (Night is { } wantNight && wantNight != night)
            return false;
        if (Weathers == null)
            return true;
        var now = ConditionCheck.Normalize(weather);
        foreach (var option in Weathers)
            if (ConditionCheck.Normalize(option) == now)
                return true;
        return false;
    }

    /// <summary>"Ashkin"-style group words for the unknown timer text: "night only", "weather only".</summary>
    public string Kind => Night == true && Weathers != null ? "night and weather" : Night == true ? "night only" :
        Night == false ? "day only" : Weathers != null ? "weather only" : Fate ? "FATE" : "any time";
}

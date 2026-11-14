using System;
using System.Collections.Generic;
using System.Globalization;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

// Collector overlay: per enemy respawn estimates measured from the collected data, and the condition-aware timer text
// (night and weather enemies say when they can be back instead of counting down). Dependency-free for unit tests.

/// <summary>A measured respawn: the median seconds from death to the next appearance of the same spot, and the sample count.</summary>
internal readonly record struct RespawnSample(int MedianSeconds, int Count);

/// <summary>
/// What the overlay knows about an enemy's respawn. <see cref="Count"/> is the sample count of this enemy, negative
/// for the class value of its condition (night-only or weather-only enemies together), 0 for the ordinary default.
/// </summary>
internal readonly record struct RespawnEstimate(int Seconds, int Count, bool Known)
{
    /// <summary>A timer can be shown (measured, or the ordinary default for an any-time enemy).</summary>
    public bool HasTimer => Known && Seconds > 0;
}

internal static class RespawnTable
{
    /// <summary>Fewer samples than this are not trusted on their own for one enemy.</summary>
    public const int MinSamples = 5;

    /// <summary>Night-only enemies, all measured ones together (Anemos, Pagos, Pyros): median 146 s, n 248.</summary>
    public static readonly RespawnSample NightClass = new(146, 248);

    /// <summary>Weather-only enemies, all measured ones together (Anemos, Pagos, Pyros): median 191 s, n 233.</summary>
    public static readonly RespawnSample WeatherClass = new(191, 233);

    // Measured from a death to the next appearance of the same layout in the same visit, while the enemy's condition
    // held, counting only clean pairs (the respawn was seen spawning, or watched within 100 y). Every layout stayed
    // within 1 s of its enemy's median. Ordinary enemies: median 51 s (p25 50, p75 51), n 3269. Hydatos: no kills in
    // the data yet. Keyed by bnpc id.
    private static readonly Dictionary<uint, RespawnSample> Measured = new()
    {
        // Anemos: Val Revenant: median 146 s, n 9
        [8640] = new(146, 9),
        // Anemos: Duskfall Skeleton: median 146 s, n 8
        [8670] = new(146, 8),
        [8672] = new(146, 8),
        // Anemos: Crystal Wolf: median 145 s, n 4
        [8653] = new(145, 4),
        // Anemos: Skeleton Noble: median 145 s, n 3
        [8478] = new(145, 3),
        [8636] = new(145, 3),
        // Anemos: Blizzard Sprite: median 191 s, n 6
        [8669] = new(191, 6),
        // Anemos: Typhoon Sprite: median 190 s, n 3
        [8675] = new(190, 3),
        [8678] = new(190, 3),
        [8687] = new(190, 3),
        [8715] = new(190, 3),
        // Pagos: Zombie Brobinyak: median 146 s, n 37
        [9069] = new(146, 37),
        [9070] = new(146, 37),
        [9071] = new(146, 37),
        [9072] = new(146, 37),
        // Pagos: Haunt: median 146 s, n 11
        [9082] = new(146, 11),
        [9083] = new(146, 11),
        // Pagos: Val Corpse: median 146 s, n 11
        [9085] = new(146, 11),
        [9086] = new(146, 11),
        [9088] = new(146, 11),
        // Pagos: Demon of the Incunable: median 146 s, n 9
        [9073] = new(146, 9),
        [9074] = new(146, 9),
        [9075] = new(146, 9),
        // Pagos: Snowmelt Sprite: median 191 s, n 28
        [9092] = new(191, 28),
        [9100] = new(191, 28),
        [9104] = new(191, 28),
        [9108] = new(191, 28),
        // Pagos: Snowstorm Sprite: median 191 s, n 27
        [9090] = new(191, 27),
        [9102] = new(191, 27),
        [9106] = new(191, 27),
        [9110] = new(191, 27),
        // Pagos: Ember Sprite: median 191 s, n 17
        [9093] = new(191, 17),
        [9097] = new(191, 17),
        [9101] = new(191, 17),
        // Pagos: Thunderstorm Sprite: median 191 s, n 17
        [9091] = new(191, 17),
        [9107] = new(191, 17),
        // Pyros: Dark Troubadour: median 146 s, n 42
        [9410] = new(146, 42),
        [9411] = new(146, 42),
        [9412] = new(146, 42),
        // Pyros: Pyros Wolf: median 146 s, n 42
        [9401] = new(146, 42),
        [9402] = new(146, 42),
        [9403] = new(146, 42),
        // Pyros: Pyros Parthenope: median 146 s, n 20
        [9413] = new(146, 20),
        [9414] = new(146, 20),
        // Pyros: Dark Parthenope: median 146 s, n 16
        [9415] = new(146, 16),
        // Pyros: Pyros Bhoot: median 146 s, n 14
        [9404] = new(146, 14),
        [9405] = new(146, 14),
        [9406] = new(146, 14),
        // Pyros: Pyros Wraith: median 146 s, n 10
        [9407] = new(146, 10),
        [9409] = new(146, 10),
        // Pyros: Delphyne: median 146 s, n 7
        [9416] = new(146, 7),
        [9418] = new(146, 7),
        // Pyros: Val Wraith: median 146 s, n 5
        [9408] = new(146, 5),
        // Pyros: Thunderstorm Sprite: median 191 s, n 59
        [9381] = new(191, 59),
        [9382] = new(191, 59),
        [9386] = new(191, 59),
        [9391] = new(191, 59),
        [9396] = new(191, 59),
        [9399] = new(191, 59),
        // Pyros: Snowstorm Sprite: median 191 s, n 36
        [9392] = new(191, 36),
        [9395] = new(191, 36),
        [9397] = new(191, 36),
        // Pyros: Ember Sprite: median 191 s, n 20
        [9383] = new(191, 20),
        [9384] = new(191, 20),
        [9388] = new(191, 20),
        // Pyros: Typhoon Sprite: median 191 s, n 20
        [9387] = new(191, 20),
        [9393] = new(191, 20),
        [9394] = new(191, 20),
        [9400] = new(191, 20),
    };

    public static IReadOnlyDictionary<uint, RespawnSample> Table => Measured;

    /// <summary>
    /// The estimate of one enemy: its measured median when there are enough samples; else the class value of a
    /// night-only or weather-only enemy in a zone where the class was measured; else the ordinary default for an
    /// any-time enemy; else unknown.
    /// </summary>
    public static RespawnEstimate For(uint bnpcId, string condition, uint territory) => For(bnpcId, condition, territory, Measured);

    internal static RespawnEstimate For(uint bnpcId, string condition, uint territory, IReadOnlyDictionary<uint, RespawnSample> table)
    {
        if (table.TryGetValue(bnpcId, out var sample) && sample.Count >= MinSamples)
            return new RespawnEstimate(sample.MedianSeconds, sample.Count, true);
        if (!SpawnCondition.TryParse(condition, out var parsed) || parsed.AnyTime)
            return new RespawnEstimate(OverlayRates.RespawnEstimateSeconds, 0, true);
        var measuredZone = territory is ForayZones.EurekaAnemos or ForayZones.EurekaPagos or ForayZones.EurekaPyros;
        if (!parsed.Fate && measuredZone)
        {
            if (parsed.Night == true && parsed.Weathers == null)
                return new RespawnEstimate(NightClass.MedianSeconds, -NightClass.Count, true);
            if (parsed.Night == null && parsed.Weathers != null)
                return new RespawnEstimate(WeatherClass.MedianSeconds, -WeatherClass.Count, true);
        }

        if (parsed.Fate && parsed.Night == null && parsed.Weathers == null)
            return new RespawnEstimate(OverlayRates.RespawnEstimateSeconds, 0, true);
        return new RespawnEstimate(0, 0, false);
    }
}

/// <summary>The respawn line of one death: a countdown, a "back at night" / "needs weather" text, or unknown.</summary>
internal static class RespawnTimer
{
    /// <summary>
    /// The text for a death. <paramref name="diedUnixMs"/> and <paramref name="nowUnixMs"/> are wall clock ms (the
    /// forecast needs them). When the condition does not hold now the next window is shown instead of a countdown.
    /// </summary>
    public static string Text(uint territory, string condition, RespawnEstimate estimate, long diedUnixMs, long nowUnixMs,
        out int remainingSeconds)
    {
        remainingSeconds = -1;
        if (SpawnCondition.TryParse(condition, out var parsed) && parsed.Forecastable)
        {
            var window = EorzeaClock.Window(territory, condition, nowUnixMs);
            if (window is { Active: false } next)
                return WaitText(parsed, next.Start, nowUnixMs);
            // The respawn waits for the condition: when it came back after the death, count from its return.
            if (window is { Active: true } current && current.Start > diedUnixMs && current.Start != long.MinValue)
                diedUnixMs = current.Start;
        }

        if (!estimate.HasTimer)
            return "timer unknown (" + (parsed.Forecastable ? parsed.Kind : "no data") + ")";
        remainingSeconds = RespawnText.Remaining(diedUnixMs, nowUnixMs, estimate.Seconds);
        return RespawnText.Text(remainingSeconds);
    }

    /// <summary>"Back at night (ET 18:00, in ~12 min)" or "Needs Blizzard/Snow (next in ~40 min)".</summary>
    public static string WaitText(SpawnCondition parsed, long startUnixMs, long nowUnixMs)
    {
        var minutes = EorzeaClock.MinutesUntil(startUnixMs, nowUnixMs).ToString(CultureInfo.InvariantCulture);
        if (parsed.Weathers == null)
            return (parsed.Night == true ? "Back at night (ET " : "Back by day (ET ") + EorzeaClock.EtText(startUnixMs) + ", in ~" + minutes + " min)";
        return "Needs " + string.Join("/", parsed.Weathers) + (parsed.Night == true ? " at night" : parsed.Night == false ? " by day" : "") +
               " (next in ~" + minutes + " min)";
    }

    /// <summary>The condition holds now, or cannot be forecast (any time, FATE, unknown text).</summary>
    public static bool HoldsNow(uint territory, string condition, long nowUnixMs) =>
        !SpawnCondition.TryParse(condition, out var parsed) || !parsed.Forecastable ||
        EorzeaClock.Window(territory, condition, nowUnixMs) is not { Active: false };

    /// <summary>The basis of the estimate in the window: "n 14" (this enemy), "class, n 248" or "default".</summary>
    public static string Basis(RespawnEstimate estimate) =>
        !estimate.Known ? "" : estimate.Count > 0 ? "n " + estimate.Count.ToString(CultureInfo.InvariantCulture) :
        estimate.Count < 0 ? "class, n " + (-estimate.Count).ToString(CultureInfo.InvariantCulture) : "default";
}

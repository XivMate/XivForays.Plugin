namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>A minimum gap between runs of one piece of work, driven by a caller-owned clock value.</summary>
internal static class Throttle
{
    /// <summary>True (and the clock is moved to <paramref name="nowMs"/>) when never run or at least <paramref name="intervalMs"/> has passed.</summary>
    public static bool Due(long nowMs, ref long lastMs, int intervalMs)
    {
        if (lastMs != 0 && nowMs - lastMs < intervalMs)
            return false;
        lastMs = nowMs;
        return true;
    }
}

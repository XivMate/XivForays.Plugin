using System.Numerics;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>Where a respawn anchor came from, best first.</summary>
internal enum AnchorSource : byte
{
    /// <summary>The matched coverage row (exact, confirmed, to confirm or never seen).</summary>
    Row = 0,

    /// <summary>A matched walk-past row: its spot is approximate.</summary>
    RowApproximate = 1,

    /// <summary>No row: the enemy's home (the game's DefaultPosition, kept from when the instance first appeared).</summary>
    Home = 2,

    /// <summary>No row and no home: where it died. Approximate, drawn as a hollow ring.</summary>
    DeathSpot = 3
}

/// <summary>A resolved respawn anchor.</summary>
internal readonly record struct ResolvedAnchor(Vector3 Position, bool HeightKnown, AnchorSource Source)
{
    public bool Approximate => Source is AnchorSource.RowApproximate or AnchorSource.DeathSpot;

    /// <summary>The flag text of an approximate anchor, "" otherwise.</summary>
    public string Note => SpawnAnchor.Note(Source);
}

/// <summary>
/// The one place that decides where a killed enemy will respawn, for the respawn countdown (box 2), the pack watch
/// rings, best spot and safe area (box 7) and the map layer: (1) the matched coverage row, (2) the enemy's home,
/// (3) only when neither exists, the death spot, flagged approximate. Never the corpse when (1) or (2) exists.
/// </summary>
internal static class SpawnAnchor
{
    public static ResolvedAnchor Resolve(CoveragePoint? row, Vector3? home, Vector3 deathSpot)
    {
        var validHome = home is { } h && Valid(h) ? home : null;
        if (row != null)
        {
            // A row whose height is unreliable keeps its x/z; the height only places the drawing.
            var y = row.HeightKnown ? row.Y : validHome?.Y ?? deathSpot.Y;
            return new ResolvedAnchor(new Vector3(row.X, y, row.Z), row.HeightKnown,
                row.Status == CoverageStatus.WalkPast ? AnchorSource.RowApproximate : AnchorSource.Row);
        }

        if (validHome is { } at)
            return new ResolvedAnchor(at, true, AnchorSource.Home);
        return new ResolvedAnchor(deathSpot, true, AnchorSource.DeathSpot);
    }

    public static string Note(AnchorSource source) => source switch
    {
        AnchorSource.RowApproximate => "approximate (walk-past spot)",
        AnchorSource.DeathSpot => "approximate (death spot)",
        _ => ""
    };

    private static bool Valid(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && v != Vector3.Zero;
}

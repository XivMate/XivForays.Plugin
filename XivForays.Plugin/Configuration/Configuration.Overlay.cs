using System;
using XivMate.DataGathering.Forays.Dalamud.Core;

namespace XivMate.DataGathering.Forays.Dalamud.Configuration;

/// <summary>
/// Collector overlay settings (0.0.0.16). Everything is off by default; a saved config from 0.0.0.15 or older has no
/// such keys, so the initializers below apply and the overlay stays off. Dependency-free so it can be unit tested.
/// </summary>
public partial class Configuration
{
    /// <summary>Master switch. Off = no downloads, no window, no drawing, no extra scan work.</summary>
    public bool OverlayEnabled { get; set; } = false;

    /// <summary>1. Nearby needed list window.</summary>
    public bool OverlayNearbyList { get; set; } = false;

    /// <summary>1. The game points to the row picked in the list: a marker, a ground guide and an edge arrow.</summary>
    public bool OverlayPointer { get; set; } = true;

    /// <summary>2. Respawn countdown on spots of enemies that just died.</summary>
    public bool OverlayRespawn { get; set; } = false;

    /// <summary>3. Range and readiness of the selected or pinned point.</summary>
    public bool OverlayRange { get; set; } = false;

    /// <summary>4. Instant "Confirmed locally" feedback.</summary>
    public bool OverlayLocalConfirm { get; set; } = false;

    /// <summary>5. Status markers over visible enemies.</summary>
    public bool OverlayEnemyMarkers { get; set; } = false;

    /// <summary>5. Also mark enemies whose point is already Confirmed.</summary>
    public bool OverlayShowConfirmed { get; set; } = false;

    /// <summary>6. Markers on empty spawn points still needed.</summary>
    public bool OverlayEmptyPoints { get; set; } = false;

    /// <summary>7. Pack watch: the spawn points of a killed pack, the spot that keeps all of them in range.</summary>
    public bool OverlayPackWatch { get; set; } = false;

    /// <summary>7. Pack watch keep-within radius (yalms, 50 to 80, default 60: a margin under the 80 y rule).</summary>
    public float OverlayPackRadius { get; set; } = PackWatch.DefaultRadius;

    /// <summary>Single-point ground circles (80 y watch, 70 y photo) around the selected point, under Pack watch.</summary>
    public bool OverlayRings { get; set; } = false;

    /// <summary>8. Spawn condition text, greyed when known unavailable.</summary>
    public bool OverlayConditions { get; set; } = false;

    /// <summary>9. Spawn points on the in-game area map.</summary>
    public bool OverlayMap { get; set; } = false;

    public bool OverlayMapConfirmed { get; set; } = false;
    public bool OverlayMapToConfirm { get; set; } = true;
    public bool OverlayMapWalkPast { get; set; } = true;
    public bool OverlayMapNeverSeen { get; set; } = true;

    /// <summary>9. Hide points whose condition (night, weather, FATE) is known to be unmet now.</summary>
    public bool OverlayMapSpawnsNow { get; set; } = false;

    /// <summary>9. Only points of recently killed enemies that wait for their respawn.</summary>
    public bool OverlayMapPendingOnly { get; set; } = false;

    /// <summary>9. Markers drawn per map view at most (the nearest to the player first).</summary>
    public int OverlayMapMaxMarkers { get; set; } = MapRates.DefaultMaxMarkers;

    /// <summary>World markers are drawn within this distance of the player (yalms, 20 to 150).</summary>
    public float OverlayDisplayYalms { get; set; } = OverlayRates.DefaultDisplayYalms;

    public float OverlayMarkerScale { get; set; } = 1f;
    public float OverlayOpacity { get; set; } = 0.9f;
    public int OverlayMaxMarkers { get; set; } = OverlayRates.DefaultMaxMarkers;
    public int OverlayListRows { get; set; } = OverlayRates.MaxListRows;
    public bool OverlayHideInCombat { get; set; } = false;

    /// <summary>Quick "Hide overlay": hides the drawing and the window but keeps the settings and the data.</summary>
    public bool OverlayHidden { get; set; } = false;

    /// <summary>The settings in force, with every value clamped to its range.</summary>
    internal OverlayFeatures OverlayFeatures => new(
        OverlayEnabled,
        OverlayEnabled && OverlayNearbyList,
        OverlayEnabled && OverlayNearbyList && OverlayPointer,
        OverlayEnabled && OverlayRespawn,
        OverlayEnabled && OverlayRange,
        OverlayEnabled && OverlayLocalConfirm,
        OverlayEnabled && OverlayEnemyMarkers,
        OverlayEnabled && OverlayEnemyMarkers && OverlayShowConfirmed,
        OverlayEnabled && OverlayEmptyPoints,
        OverlayEnabled && OverlayPackWatch,
        Math.Clamp(float.IsFinite(OverlayPackRadius) ? OverlayPackRadius : PackWatch.DefaultRadius, PackWatch.MinRadius, PackWatch.MaxRadius),
        OverlayEnabled && OverlayRings,
        OverlayEnabled && OverlayConditions,
        Math.Clamp(float.IsFinite(OverlayDisplayYalms) ? OverlayDisplayYalms : OverlayRates.DefaultDisplayYalms, 20f, OverlayRates.MaxDisplayYalms),
        Math.Clamp(float.IsFinite(OverlayMarkerScale) ? OverlayMarkerScale : 1f, 0.5f, 2f),
        Math.Clamp(float.IsFinite(OverlayOpacity) ? OverlayOpacity : 0.9f, 0.2f, 1f),
        Math.Clamp(OverlayMaxMarkers, 8, OverlayRates.DefaultMaxMarkers),
        Math.Clamp(OverlayListRows, OverlayRates.MinListRows, OverlayRates.MaxListRows),
        OverlayHideInCombat,
        OverlayHidden,
        OverlayEnabled && OverlayMap,
        new MapFilter(OverlayMapConfirmed, OverlayMapToConfirm, OverlayMapWalkPast, OverlayMapNeverSeen, OverlayMapSpawnsNow,
            OverlayMapPendingOnly),
        Math.Clamp(OverlayMapMaxMarkers, MapRates.MinMaxMarkers, MapRates.MaxMaxMarkers));
}

/// <summary>The overlay settings as one value (master switch already applied to each feature).</summary>
internal readonly record struct OverlayFeatures(
    bool Enabled,
    bool NearbyList,
    bool Pointer,
    bool Respawn,
    bool Range,
    bool LocalConfirm,
    bool EnemyMarkers,
    bool ShowConfirmed,
    bool EmptyPoints,
    bool PackWatch,
    float PackRadius,
    bool Rings,
    bool Conditions,
    float DisplayYalms,
    float MarkerScale,
    float Opacity,
    int MaxMarkers,
    int ListRows,
    bool HideInCombat,
    bool Hidden,
    bool Map,
    MapFilter MapFilter,
    int MapMaxMarkers)
{
    /// <summary>The coverage file is downloaded only when the master switch is on.</summary>
    public bool NeedsDownload => Enabled;

    /// <summary>The enemy scan feeds the overlay only when a feature uses live enemies.</summary>
    public bool NeedsScan => Enabled && (NearbyList || Respawn || Range || LocalConfirm || EnemyMarkers || EmptyPoints || PackWatch || Rings || Map);

    /// <summary>Something is drawn into the game view.</summary>
    public bool DrawsWorld => Enabled && (Pointer || Respawn || LocalConfirm || EnemyMarkers || EmptyPoints || PackWatch || Rings);
}

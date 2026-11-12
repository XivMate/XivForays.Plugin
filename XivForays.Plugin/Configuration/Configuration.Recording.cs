using XivMate.DataGathering.Forays.Dalamud.Core;

namespace XivMate.DataGathering.Forays.Dalamud.Configuration;

/// <summary>
/// Per-content recording toggles (0.0.0.15). Dependency-free so the default-true load can be unit tested.
/// A saved config from 0.0.0.14 or older has no such keys, so the initializers below apply and every content stays on.
/// </summary>
public partial class Configuration
{
    /// <summary>Record in Eureka (Anemos, Pagos, Pyros, Hydatos incl. the Baldesion Arsenal). On by default.</summary>
    public bool RecordEureka { get; set; } = true;

    /// <summary>Record in Bozja (Southern Front, Zadnor, Delubrum Reginae and Savage). On by default.</summary>
    public bool RecordBozja { get; set; } = true;

    /// <summary>Record in the Occult Crescent (South Horn, North Horn). On by default.</summary>
    public bool RecordOccultCrescent { get; set; } = true;

    /// <summary>True when the content this territory belongs to is ticked. False for non-foray territories.</summary>
    public bool RecordsTerritory(uint territory) =>
        ForayZones.IsContentEnabled(territory, RecordEureka, RecordBozja, RecordOccultCrescent);

    /// <summary>True when all three contents are unticked, so nothing is recorded anywhere.</summary>
    public bool RecordsNothing => !RecordEureka && !RecordBozja && !RecordOccultCrescent;
}

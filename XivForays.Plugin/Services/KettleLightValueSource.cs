using XivMate.DataGathering.Forays.Dalamud.Core;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

/// <summary>
/// Where the precise kettle value is read from (read-only). The value's location is not documented in FFXIVClientStructs
/// (see docs/KETTLE_LIGHT.md), so no address or offset is guessed: until the maintainer names the field this source
/// returns null and the kind 20 records come from the kettle notices alone (row id, grade, link), with before/after
/// absent. Replace <see cref="Read"/> with a plain field read of the named field; nothing else needs to change.
/// </summary>
internal sealed class KettleLightValueSource
{
    /// <summary>The kettle value now, or null when it cannot be read. Called at most once per signal tick, in Pagos and Pyros only.</summary>
    public uint? Read() => null;
}

using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using XivMate.DataGathering.Forays.Dalamud.Core;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

/// <summary>
/// The zone gate. Territory IDs (any client language) from <see cref="ForayZones"/>; any map layer that
/// belongs to the zone (Baldesion Arsenal, the Dalriada, Delubrum floors, Forked Tower ...) is accepted.
/// Outside a gated territory every check is a single cached boolean, so no other work happens.
/// The per-content toggles (Record in: Eureka / Bozja / Occult Crescent) are read live from the configuration on
/// every check, so ticking or unticking takes effect at once without a zone change.
/// </summary>
public sealed class ForayService : IDisposable
{
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly Plugin plugin;
    private uint territoryId;
    private bool forayTerritory;
    private uint cachedMapId = uint.MaxValue;
    private bool cachedMapRecordable;

    public Guid VisitId { get; private set; } = Guid.NewGuid();

    public ForayService(IClientState clientState, ICondition condition, IDataManager dataManager, Plugin plugin)
    {
        this.plugin = plugin;
        this.clientState = clientState;
        this.condition = condition;
        this.dataManager = dataManager;
        OnTerritoryChanged(clientState.TerritoryType);
        clientState.TerritoryChanged += OnTerritoryChanged;
    }

    internal static bool IsEureka(uint territory) => ForayZones.IsEureka(territory);
    internal static bool IsOccultCrescent(uint territory) => ForayZones.IsOccultCrescent(territory);

    /// <summary>True when the user has ticked the content (Eureka, Bozja or Occult Crescent) this territory belongs to.</summary>
    public bool IsContentEnabled(uint territory) => plugin.Configuration.RecordsTerritory(territory);

    public bool IsInRecordableTerritory()
    {
        if (!forayTerritory || !IsContentEnabled(territoryId) || !clientState.IsLoggedIn || territoryId != clientState.TerritoryType ||
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return false;

        var mapId = clientState.MapId;
        if (mapId != cachedMapId)
        {
            cachedMapId = mapId;
            cachedMapRecordable = mapId != 0 &&
                                  dataManager.GetExcelSheet<Map>().TryGetRow(mapId, out var map) &&
                                  ForayZones.IsRecordable(territoryId, mapId, map.TerritoryType.RowId);
        }

        return cachedMapRecordable;
    }

    private void OnTerritoryChanged(uint territory)
    {
        territoryId = territory;
        forayTerritory = ForayZones.IsForayTerritory(territory);
        cachedMapId = uint.MaxValue;
        cachedMapRecordable = false;
        VisitId = Guid.NewGuid();
    }

    public void Dispose() => clientState.TerritoryChanged -= OnTerritoryChanged;
}

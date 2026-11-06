using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

/// <summary>TerritoryType lookups for the debug window. The zone gate lives in <see cref="Core.ForayZones"/>.</summary>
public class TerritoryService(IDataManager dataManager)
{
    public TerritoryType? GetTerritoryForId(uint territoryId) =>
        dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var row) ? row : null;
}

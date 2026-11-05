using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

public class TerritoryService : IDisposable
{
    private readonly IDataManager dataManager;
    private ImmutableList<TerritoryType>? forayIds;
    public ImmutableList<TerritoryType> ForayIds => forayIds ??= GetForayIds();

    private ImmutableList<TerritoryType> GetForayIds()
    {
        var forayNames = new[] { "Eureka", "Zadnor", "Bozjan Southern Front","South Horn" };
        return dataManager.GameData.GetExcelSheet<TerritoryType>()!
                          .Where(p => forayNames.Any(forayName => p.PlaceName.ToString()?.Contains(forayName) ?? false))
                          .ToImmutableList();
    }

    public TerritoryService(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public TerritoryType? GetTerritoryForId(uint territoryId) =>
        dataManager.GameData.GetExcelSheet<TerritoryType>()
                   ?.FirstOrDefault((territory) => territory.RowId == territoryId)!;

    public void Dispose()
    {
        forayIds = null;
    }
}

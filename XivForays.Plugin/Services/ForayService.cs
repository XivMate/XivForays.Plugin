using System;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

public class ForayService : IDisposable
{
    private readonly TerritoryService territoryService;
    private readonly Configuration.Configuration configuration;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private TerritoryType? lastTerritory;

    public ForayService(
        Plugin plugin,
        TerritoryService territoryService, IClientState clientState, IPluginLog log)
    {
        this.territoryService = territoryService;
        configuration = plugin.Configuration;

        this.clientState = clientState;
        this.log = log;
        clientState.TerritoryChanged += OnTerritoryChanged;

        if (clientState.IsLoggedIn)
            this.OnTerritoryChanged(clientState.TerritoryType);
    }

    public bool IsInRecordableTerritory()
    {
        return clientState.IsLoggedIn && lastTerritory != null && IsForayTerritory(lastTerritory) &&
               lastTerritory?.Map.Value.RowId == clientState.MapId;
    }

    private bool IsForayTerritory(TerritoryType? territoryType)
    {
        var forayNames = new[] { "Eureka", "Zadnor", "Bozjan Southern Front","South Horn" };
        return territoryType != null &&
               forayNames.Any(p => territoryType?.PlaceName.Value.Name.ExtractText().Contains(p) ?? false);
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        lastTerritory = territoryService.GetTerritoryForId(territoryId);
    }

    public void Dispose()
    {
        territoryService.Dispose();
        clientState.TerritoryChanged -= OnTerritoryChanged;
        lastTerritory = null;
    }
}

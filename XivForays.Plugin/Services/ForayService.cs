using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

public sealed class ForayService : IDisposable
{
    private readonly TerritoryService territoryService;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private uint territoryId;
    private uint expectedMapId;

    public Guid VisitId { get; private set; } = Guid.NewGuid();

    public ForayService(TerritoryService territoryService, IClientState clientState, ICondition condition)
    {
        this.territoryService = territoryService;
        this.clientState = clientState;
        this.condition = condition;
        OnTerritoryChanged(clientState.TerritoryType);
        clientState.TerritoryChanged += OnTerritoryChanged;
    }

    internal static bool IsEureka(uint territory) => territory is 732 or 763 or 795 or 827;
    internal static bool IsOccultCrescent(uint territory) => territory is 1252 or 1346;

    public bool IsInRecordableTerritory()
    {
        if (!clientState.IsLoggedIn || territoryId != clientState.TerritoryType ||
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return false;

        return expectedMapId != 0 && expectedMapId == clientState.MapId;
    }

    private void OnTerritoryChanged(uint territory)
    {
        territoryId = territory;
        expectedMapId = IsEureka(territory) || IsOccultCrescent(territory) || territory is 920 or 975
            ? territoryService.GetTerritoryForId(territory)?.Map.RowId ?? 0
            : 0;
        VisitId = Guid.NewGuid();
    }

    public void Dispose() => clientState.TerritoryChanged -= OnTerritoryChanged;
}

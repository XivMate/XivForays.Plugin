using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Newtonsoft.Json;
using XivMate.DataGathering.Forays.Dalamud.Extensions;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

public class EnemyTrackingService : IDisposable // Add IDisposable
{
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly TerritoryService territoryService;
    private readonly ForayService forayService;

    private Guid _instanceGuid = Guid.NewGuid();

    private readonly Dictionary<ulong, EnemyPosition> _enemies = new();
    private bool _disposed = false; // Add this line

    // Dictionary to track which NPC IDs have been observed in combat
    private readonly Dictionary<ulong, bool> _inCombatHistory = new();

    public EnemyTrackingService(
        IClientState clientState,
        IObjectTable objectTable,
        IPluginLog log,
        TerritoryService territoryService,
        ForayService forayService)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.log = log;
        this.territoryService = territoryService;
        this.forayService = forayService;

        this.clientState.TerritoryChanged += OnTerritoryChanged;
        OnTerritoryChanged(clientState.TerritoryType);
    }

    /// <summary>
    /// Handles territory changes and generates a new instance GUID when entering a new territory
    /// </summary>
    private void OnTerritoryChanged(uint territoryId)
    {
        if (!clientState.IsLoggedIn)
        {
            return;
        }

        if (forayService.IsInRecordableTerritory())
        {
            var territory = territoryService.GetTerritoryForId(territoryId);
            var territoryName = territory?.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
            _instanceGuid = Guid.NewGuid();
            _inCombatHistory.Clear();
            _enemies.Clear();

            log.Info(
                $"[ETS] Foray territory: {territoryName}, local guid: {_instanceGuid}, local territory {clientState.TerritoryType}, map {clientState.MapId}");
        }
    }

    /// <summary>
    /// Get the current instance GUID for territory tracking
    /// </summary>
    public Guid GetInstanceGuid() => _instanceGuid;

    /// <summary>
    /// Checks if the current territory is a recordable foray territory
    /// </summary>
    public bool IsInRecordableTerritory() => forayService.IsInRecordableTerritory() && clientState.IsLoggedIn &&
                                             objectTable.LocalPlayer != null;

    /// <summary>
    /// Gets the current territory ID
    /// </summary>
    public int GetTerritoryId() => Convert.ToInt32(clientState.TerritoryType);

    /// <summary>
    /// Gets the current map ID
    /// </summary>
    public int GetMapId() => (int)clientState.MapId;

    /// <summary>
    /// Update the tracked enemies and return the current state
    /// </summary>
    public Dictionary<ulong, EnemyPosition> UpdateAndGetEnemies()
    {
        if (!IsInRecordableTerritory())
        {
            return new Dictionary<ulong, EnemyPosition>();
        }

        try
        {
            ProcessActiveEnemies();
            RemoveOutdatedEnemies();
            return GetCurrentEnemies();
        }
        catch (Exception ex)
        {
            log.Error($"Error in UpdateAndGetEnemies: {ex.Message}, {ex.StackTrace}");
            log.Error(ex.InnerException?.Message);
            log.Error(JsonConvert.SerializeObject(ex));

            return new Dictionary<ulong, EnemyPosition>();
        }
    }

    /// <summary>
    /// Get a copy of the current enemy dictionary
    /// </summary>
    public Dictionary<ulong, EnemyPosition> GetCurrentEnemies()
    {
        return new Dictionary<ulong, EnemyPosition>(_enemies);
    }

    /// <summary>
    /// Find combat-active enemies (those that have been in combat)
    /// </summary>
    public List<EnemyPosition> GetCombatActiveEnemies()
    {
        return _enemies.Values
                       .Where(e => e.HasBeenInCombat)
                       .ToList();
    }

    private int? GetLevel(IBattleChara battleChara)
    {
        try
        {
            unsafe
            {
                var bChara = (BattleChara*)battleChara.Address;
                return bChara->ForayInfo.Level;
            }
        }
        catch (Exception e)
        {
            log.Error($"Error getting foray level: {e.Message}");
            return battleChara.Level;
        }
    }

    private int? GetElement(IBattleChara battleChara)
    {
        try
        {
            unsafe
            {
                var bChara = (BattleChara*)battleChara.Address;
                return bChara->ForayInfo.Element;
            }
        }
        catch (Exception e)
        {
            log.Error($"Error getting element: {e.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Processes active enemies from the object table
    /// </summary>
    private void ProcessActiveEnemies()
    {
        // Create a list to track currently found enemies

        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc battleNpc) continue;

            // Only interested in enemies (not friendly NPCs or players)
            if (battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant) continue;

            // Skip if already dead
            if (battleNpc.CurrentHp <= 0) continue;

            var npcId = battleNpc.GameObjectId;
            // Check for adaptation and mutation status effects
            bool isAdapted = false;
            bool isMutated = false;
            string elementType = "Unknown";
            bool isInCombat = false;

            // Check status effects and combat state
            if (battleNpc is IBattleChara battleChara)
            {
                // Check if the NPC is in combat
                isInCombat = battleChara.TargetObject != null || battleChara.CurrentHp < battleChara.MaxHp;

                // Update combat history if the NPC is in combat
                if (isInCombat && _inCombatHistory.TryAdd(npcId, true))
                {
                    log.Debug($"NPC {battleNpc.Name} (ID: {npcId}) observed in combat for the first time");
                }

                elementType = GetElement(battleChara).GetValueOrDefault(-1).ToString();

                // Process status effects
                if (battleChara.StatusList != null)
                {
                    unsafe
                    {
                        var status =
                            battleChara.StatusList.FirstOrDefault(s =>
                                                                      s.GameData.Value.Name.ToString() ==
                                                                      "Adaptation" ||
                                                                      s.GameData.Value.Name.ToString() == "Mutation");

                        if (status != null)
                        {
                            if (status.GameData.Value.Name == "Adaption") // ID for Adaptation
                            {
                                isAdapted = true;
                            }
                            else if (status.GameData.Value.Name == "Mutation")
                            {
                                isMutated = true;
                            }
                        }
                    }
                }
            }

            // Check if this NPC has been in combat before
            var hasBeenInCombat = _inCombatHistory.ContainsKey(npcId) && _inCombatHistory[npcId];
            var now = DateTime.UtcNow.ToUnixTime();
            // Update existing record or create new one
            if (_enemies.TryGetValue(npcId, out var enemyPosition))
            {
                // Update position and status
                enemyPosition.X = battleNpc.Position.X;
                enemyPosition.Y = battleNpc.Position.Y;
                enemyPosition.Z = battleNpc.Position.Z;
                enemyPosition.IsAdapted = isAdapted;
                enemyPosition.IsMutated = isMutated;
                enemyPosition.IsInCombat = isInCombat;
                enemyPosition.HasBeenInCombat = hasBeenInCombat;
                enemyPosition.TimeStamp = now;
                enemyPosition.Element = elementType;
            }
            else
            {
                // Create new record
                var newEnemy = new EnemyPosition
                {
                    MobIngameId = battleNpc.BaseId,
                    MobName = battleNpc.Name.ToString(),
                    Level = (byte)GetLevel(battleNpc).GetValueOrDefault(0),
                    X = battleNpc.Position.X,
                    Y = battleNpc.Position.Y,
                    Z = battleNpc.Position.Z,
                    IsAdapted = isAdapted,
                    IsMutated = isMutated,
                    IsInCombat = isInCombat,
                    HasBeenInCombat = hasBeenInCombat,
                    TimeStamp = now,
                    Element = elementType,
                    TerritoryId = Convert.ToInt32(clientState.TerritoryType),
                    MapId = (int)clientState.MapId,
                    InstanceId = _instanceGuid
                };

                _enemies.Add(npcId, newEnemy);
                log.Verbose(
                    $"Added new enemy: {newEnemy.MobName} (ID: {newEnemy.MobIngameId}, Level: {newEnemy.Level}, In Combat: {newEnemy.IsInCombat})");
            }
        }
    }

    /// <summary>
    /// Removes enemies that haven't been seen in a while
    /// </summary>
    private void RemoveOutdatedEnemies()
    {
        //Clear ANYTHING not in current objectTable
        var outdatedEnemies = _enemies.Keys
                                      .Where(id => !objectTable.Any(obj => obj is IBattleNpc battleNpc &&
                                                                           battleNpc.GameObjectId == id))
                                      .ToList();
        log.Verbose($"Found {outdatedEnemies.Count} outdated enemies to remove of {_enemies.Count} total enemies");
        foreach (var enemyId in outdatedEnemies)
        {
            var outdatedEnemy = _enemies[enemyId];
            if (_enemies.Remove(enemyId))
            {
                log.Verbose($"Removed outdated enemy: {outdatedEnemy.MobName} (ID: {outdatedEnemy.MobIngameId})");
            }
        }
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        Dispose(true); // Call the new Dispose method
        GC.SuppressFinalize(this);
    }

    // Add the protected virtual Dispose method
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Unsubscribe from events
            clientState.TerritoryChanged -= OnTerritoryChanged;

            // Clear collections
            _enemies.Clear();
            _inCombatHistory.Clear();
        }

        _disposed = true;
    }
}

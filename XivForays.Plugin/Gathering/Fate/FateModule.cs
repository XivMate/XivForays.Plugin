using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using XivMate.DataGathering.Forays.Dalamud.Extensions;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Gathering.Fate;

public class FateModule(
    IClientState clientState,
    IObjectTable objectTable,
    IFateTable fateTable,
    IFramework framework,
    SchedulerService schedulerService,
    TerritoryService territoryService,
    IPluginLog log,
    ApiService apiService,
    ForayService forayService)
    : IModule
{
    private bool isInRecordableTerritory;
    private Guid instanceGuid = Guid.NewGuid();
    private readonly Dictionary<uint, Models.Fate> fates = new();
    private readonly ConcurrentQueue<Models.Fate> fateQueue = new();

    private bool enabled = false;
    public bool Enabled => enabled;
    private bool _disposed = false; // Add this line

    public IEnumerable<Models.Fate> ActiveFates => fates.Values.ToList();

    /// <summary>
    /// Loads configuration and enables/disables the module accordingly
    /// </summary>
    public void LoadConfig(Configuration.Configuration configuration)
    {
        if (configuration.CanCrowdsourceData && !enabled)
        {
            clientState.TerritoryChanged += OnTerritoryChanged;
            schedulerService.ScheduleOnFrameworkThread(FateTick, 500);
            schedulerService.ScheduleOnNewThread(FateUpload, 2500);
            OnTerritoryChanged(clientState.TerritoryType);
            enabled = true;
            fates.Clear();
        }
        else if (enabled && !configuration.CanCrowdsourceData)
        {
            clientState.TerritoryChanged -= OnTerritoryChanged;
            schedulerService.CancelScheduledTask(FateTick);
            schedulerService.CancelScheduledTask(FateUpload);
            enabled = false;
        }
    }

    /// <summary>
    /// Handles territory changes and determines if the current territory is recordable
    /// </summary>
    private void OnTerritoryChanged(uint territoryId)
    {
        if (!clientState.IsLoggedIn)
        {
            return;
        }

        var territory = territoryService.GetTerritoryForId(territoryId);
        log.Debug($"Current territory id is: ", territoryId);
        log.Debug("Territory Placename is : ", territory?.PlaceName.ValueNullable);
        if (territory?.PlaceName.ValueNullable.HasValue ?? false)
        {
            string territoryName = territory.Value.PlaceName.Value.Name.ToString();
            if (forayService.IsInRecordableTerritory())
            {
                log.Info($"Foray territory: {territoryName}");
                instanceGuid = Guid.NewGuid();
                isInRecordableTerritory = true;
            }
            else
            {
                log.Info($"Not Foray territory: {territoryName}");
                isInRecordableTerritory = false;
            }
        }
        else
        {
            isInRecordableTerritory = false;
        }
    }

    /// <summary>
    /// Processes fate information on each tick
    /// </summary>
    private void FateTick()
    {
        if (objectTable.LocalPlayer == null || !isInRecordableTerritory)
        {
            return;
        }

        try
        {
            ProcessActiveFates();
            RemoveNonExistentFates();
        }
        catch (Exception ex)
        {
            log.Error($"Error in FateTick: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes active fates from the fate table
    /// </summary>
    private void ProcessActiveFates()
    {
        foreach (var fate in fateTable)
        {
            if (fates.TryGetValue(fate.FateId, out var modelFate))
            {
                if (fate.State is FateState.Ended or FateState.Failed)
                {
                    RemoveFate(modelFate);
                }
            }
            else if (fate.State is FateState.Running && fate.StartTimeEpoch > 0 && fate.Position != Vector3.Zero)
            {
                // Add new fate
                log.Info(
                    $"New fate found: {fate.Name}, started at: {fate.StartTimeEpoch}, position: {fate.Position}, status: {fate.State}, territory: {fate.TerritoryType.Value.RowId}, map: {clientState.MapId}");

                modelFate = new Models.Fate()
                {
                    Name = fate.Name.ToString(),
                    FateId = fate.FateId,
                    X = fate.Position.X,
                    Y = fate.Position.Y,
                    Z = fate.Position.Z,
                    Radius = fate.Radius,
                    StartedAt = fate.StartTimeEpoch,
                    InstanceId = instanceGuid,
                    TerritoryId = Convert.ToInt32(fate.TerritoryType.Value.RowId),
                    MapId = (int)clientState.MapId,
                    LevelId = fate.Level
                };

                fates.Add(fate.FateId, modelFate);
            }
        }
    }

    /// <summary>
    /// Removes fates that no longer exist in the fate table
    /// </summary>
    private void RemoveNonExistentFates()
    {
        var fatesToRemove = fates.Keys
                                 .Where(fateId => fateTable.All(existingFate => existingFate.FateId != fateId))
                                 .ToList();

        foreach (var fateId in fatesToRemove)
        {
            if (fates.TryGetValue(fateId, out var removedFate))
            {
                RemoveFate(removedFate);
            }
        }
    }

    /// <summary>
    /// Removes a fate from tracking and adds it to the upload queue
    /// </summary>
    private void RemoveFate(Models.Fate modelFate)
    {
        if (fates.Remove(modelFate.FateId))
        {
            modelFate.EndedAt = DateTime.UtcNow.ToUnixTime();

            if (modelFate.TerritoryId == clientState.TerritoryType)
            {
                fateQueue.Enqueue(modelFate);
            }

            log.Info($"Fate ended: {modelFate.Name}, at: {modelFate.EndedAt}");
            log.Info(
                $"#1 - Fate ended territoryid {modelFate.TerritoryId}, client territory: {clientState.TerritoryType}, client map: {clientState.MapId}");
            log.Info(JsonConvert.SerializeObject(modelFate));
        }
    }

    /// <summary>
    /// Uploads fates from the queue to the API
    /// </summary>
    private void FateUpload()
    {
        if (fateQueue.TryDequeue(out var fate))
        {
            try
            {
                apiService.UploadFate(fate).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                fateQueue.Enqueue(fate);
                log.Warning($"Error uploading {fate.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Disposes resources used by the module
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Unsubscribe from events and cancel tasks
            clientState.TerritoryChanged -= OnTerritoryChanged;
            schedulerService.CancelScheduledTask(FateTick);
            schedulerService.CancelScheduledTask(FateUpload);
            // Clear collections if they contain disposable items or to release memory
            fates.Clear();
            while (fateQueue.TryDequeue(out _)) { }
        }

        // Unmanaged resources would be disposed here if any

        _disposed = true;
    }
}

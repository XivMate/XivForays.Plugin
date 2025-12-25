using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Gathering.Fate;

/// <summary>
/// Represents a completed fate with all associated reward information
/// </summary>
public class FateCompletionData
{
    public string FateName { get; set; } = string.Empty;
    public uint FateId { get; set; }
    public int Experience { get; set; }
    public int Tomestones { get; set; }
    public List<ItemReward> Items { get; set; } = new();
    public string CompletionLevel { get; set; } = string.Empty; // Bronze/Silver/Gold
    public bool WasSuccessful { get; set; }
    public DateTime CompletionTime { get; set; }
    public bool HasAddonData { get; set; }
    public bool HasInventoryData { get; set; }
    public bool HasChatData { get; set; }
}

/// <summary>
/// Represents an item reward from fate completion
/// </summary>
public class ItemReward
{
    public uint ItemId { get; set; }
    public int Quantity { get; set; }
    public string ItemName { get; set; } = string.Empty;
}

/// <summary>
/// Temporary data structure to correlate information from different sources
/// </summary>
public class PendingFateCompletion
{
    public DateTime StartTime { get; set; }
    public List<InventoryEventArgs> InventoryChanges { get; set; } = new();
    public FateCompletionData? AddonData { get; set; }
    public List<string> ChatMessages { get; set; } = new();
    public IFate? EndedFate { get; set; }
}

public class RandomTestingModule(
    IClientState clientState,
    IFateTable fateTable,
    IFramework framework,
    SchedulerService schedulerService,
    TerritoryService territoryService,
    IPluginLog log,
    ApiService apiService,
    IGameGui gameGui,
    IGameInventory gameInventory,
    IDataManager dataManager,
    IChatGui chatGui)
    : IModule
{
    private readonly List<FateCompletionData> completedFates = new();
    private readonly TimeSpan correlationWindow = TimeSpan.FromSeconds(10); // Window to correlate events
    private readonly Dictionary<DateTime, PendingFateCompletion> pendingCompletions = new();

    public void Dispose()
    {
        schedulerService.Dispose();
        territoryService.Dispose();
        chatGui.CheckMessageHandled -= OnHandleMessage;
        gameInventory.InventoryChanged -= OnGameInventoryOnInventoryChanged;
    }

    public bool Enabled { get; } = false;

    public void LoadConfig(Configuration.Configuration configuration)
    {
        chatGui.CheckMessageHandled += OnHandleMessage;
        gameInventory.InventoryChanged += OnGameInventoryOnInventoryChanged;
    }

    void OnGameInventoryOnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        unsafe
        {
            var now = DateTime.UtcNow;
            log.Info($"----------------------------------------");
            log.Info($"Inventory changed: {string.Join(", ", events.Select(p => $"[{p.Item.ItemId},{p.Item.Quantity}]"))}");

            // Check for ended fates
            var endedFate = GetSingleEndedFate();

            // Process FateReward addon if available
            var addonData = ProcessFateRewardAddon();

            // Create or update pending completion
            var pending = GetOrCreatePendingCompletion(now);
            pending.InventoryChanges.AddRange(events);
            pending.EndedFate = endedFate;

            if (addonData != null)
            {
                pending.AddonData = addonData;
                log.Info($"[Fate Completion] Addon data captured: {addonData.FateName}, XP: {addonData.Experience}, Level: {addonData.CompletionLevel}");
            }

            // Log inventory details
            ProcessInventoryEvents(events);

            // Try to finalize any pending completions
            TryFinalizePendingCompletions();

            log.Info($"----------------------------------------");
        }
    }

    private void OnHandleMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool ishandled)
    {
        if (message.TextValue.Contains("EXP chain") ||
            message.TextValue.Contains("You obtain") ||
            message.TextValue.Contains("You were unable to obtain") ||
            message.TextValue.Contains("Unable to obtain") ||
            message.TextValue.ToLower().Contains("defeats the") ||
            message.TextValue.ToLower().Contains("you defeat the"))
        {
            var now = DateTime.UtcNow;
            log.Info($"----------------------------------------");
            log.Info("OnHandleMessage");
            log.Information($"Type: {type}, sender: {sender.TextValue}, Message: {message.TextValue}, is handled: {ishandled}, timestamp: {timestamp}");

            // Add to pending completion
            var pending = GetOrCreatePendingCompletion(now);
            pending.ChatMessages.Add(message.TextValue);

            // Process chat message for rewards
            ProcessChatRewards(message, pending);

            foreach (var payload in message.Payloads)
            {
                log.Info($"[Random] Payload: {payload.Type} - {payload.Dirty} - {payload}");
            }

            TryFinalizePendingCompletions();
            log.Info($"----------------------------------------");
        }
    }

    private IFate? GetSingleEndedFate()
    {
        var endedFates = fateTable.Where(p => p.State == FateState.Ended).ToArray();

        switch (endedFates.Length)
        {
            case > 1:
                log.Info("[Random-LogEndedFates] Found multiple fates ended");
                return null;
            case 0:
                log.Info($"[Random-LogEndedFates] No fates ended. Options were: {string.Join(", ", fateTable.Select(p => p.Name + " - " + p.State))}");
                return null;
            default:
                var fate = endedFates.Single();
                log.Info($"[Random-LogEndedFates] Fate ended: {fate.Name} - {fate.State}");
                return fate;
        }
    }

    private unsafe FateCompletionData? ProcessFateRewardAddon()
    {
        var addonPtr = gameGui.GetAddonByName("FateReward");
        if (addonPtr.IsNull)
        {
            addonPtr = gameGui.GetAddonByName("FateReward", 0);
        }

        if (addonPtr.IsNull)
        {
            log.Info("[ProcessFateReward] Addon is null");
            return null;
        }

        var atkPtr = (AtkUnitBase*)addonPtr.Address;
        if (!atkPtr->IsVisible)
        {
            log.Info("[ProcessFateReward] UnitBase is not visible");
            return null;
        }

        var addon = (AddonFateReward*)addonPtr.Address;

        // Extract data from the addon
        var completion = new FateCompletionData
        {
            HasAddonData = true,
            CompletionTime = DateTime.UtcNow
        };

        log.Info($"[ProcessFateReward] UnitBase is visible - extracting data...");

        return completion;
    }

    private void ProcessInventoryEvents(IReadOnlyCollection<InventoryEventArgs> events)
    {
        foreach (var item in events)
        {
            try
            {
                // Skip equipment durability changes
                if (item.Item.ContainerType is GameInventoryType.EquippedItems or GameInventoryType.KeyItems)
                    continue;

                var inventory = gameInventory.GetInventoryItems(item.Item.ContainerType);
                if (item.Item.InventorySlot < inventory.Length)
                {
                    var specificItem = inventory[(int)item.Item.InventorySlot];
                    log.Debug($"[IC-I-Loop]Item: {specificItem}, Quantity: {specificItem.Quantity}, Slot: {specificItem.InventorySlot}");
                }

                var baseItem = dataManager.GetExcelSheet<Item>()?.GetRow(item.Item.ItemId);
                log.Debug($"[IC]Item: {item.Item.ItemId}, Type: {item.Type}, Quantity: {item.Item.Quantity}, Name: {baseItem?.Name}");
            }
            catch (Exception e)
            {
                log.Info($"[IC]Error getting item: {e}");
            }
        }
    }

    private void ProcessChatRewards(SeString message, PendingFateCompletion pending)
    {
        // Process "You obtain X items" messages
        if (message.TextValue.Contains("You obtain"))
        {
            foreach (var payload in message.Payloads)
            {
                if (payload is ItemPayload itemPayload)
                {
                    // Extract quantity from the message text
                    var quantity = ExtractQuantityFromMessage(message.TextValue);

                    if (pending.AddonData == null)
                        pending.AddonData = new FateCompletionData();

                    pending.AddonData.Items.Add(new ItemReward
                    {
                        ItemId = itemPayload.ItemId,
                        Quantity = quantity,
                        ItemName = itemPayload.DisplayName ?? "Unknown"
                    });

                    log.Info($"[Chat Reward] Found item: {itemPayload.ItemId} x{quantity} ({itemPayload.Item}) - {itemPayload.DisplayName}");
                }
            }
        }
    }

    private int ExtractQuantityFromMessage(string message)
    {
        // Extract number from "You obtain X item" message
        var words = message.Split(' ');
        for (int i = 0; i < words.Length - 1; i++)
        {
            if (words[i] == "obtain" && int.TryParse(words[i + 1], out int quantity))
            {
                return quantity;
            }
        }
        return 1; // Default quantity
    }

    private PendingFateCompletion GetOrCreatePendingCompletion(DateTime now)
    {
        // Clean up old pending completions
        var cutoff = now - correlationWindow;
        var toRemove = pendingCompletions.Where(kvp => kvp.Key < cutoff).ToList();
        foreach (var kvp in toRemove)
        {
            pendingCompletions.Remove(kvp.Key);
        }

        // Find existing pending completion within the correlation window
        var existing = pendingCompletions.FirstOrDefault(kvp => now - kvp.Key < correlationWindow);
        if (existing.Value != null)
        {
            return existing.Value;
        }

        // Create new pending completion
        var pending = new PendingFateCompletion
        {
            StartTime = now
        };
        pendingCompletions[now] = pending;
        return pending;
    }

    private void TryFinalizePendingCompletions()
    {
        var now = DateTime.UtcNow;
        var toFinalize = new List<KeyValuePair<DateTime, PendingFateCompletion>>();

        foreach (var kvp in pendingCompletions)
        {
            var pending = kvp.Value;

            // Check if we have enough data to finalize
            bool hasInventoryData = pending.InventoryChanges.Any();
            bool hasAddonData = pending.AddonData != null;
            bool hasChatData = pending.ChatMessages.Any();

            // Finalize if we have addon data (most reliable) or if enough time has passed
            if (hasAddonData || (now - pending.StartTime > TimeSpan.FromSeconds(5)))
            {
                var completion = FinalizeCompletion(pending);
                if (completion != null)
                {
                    completedFates.Add(completion);
                    LogCompletedFate(completion);
                }
                toFinalize.Add(kvp);
            }
        }

        // Remove finalized completions
        foreach (var kvp in toFinalize)
        {
            pendingCompletions.Remove(kvp.Key);
        }
    }

    private FateCompletionData? FinalizeCompletion(PendingFateCompletion pending)
    {
        var completion = pending.AddonData ?? new FateCompletionData();

        // Set basic data
        completion.CompletionTime = pending.StartTime;
        completion.HasInventoryData = pending.InventoryChanges.Any();
        completion.HasChatData = pending.ChatMessages.Any();
        completion.HasAddonData = pending.AddonData != null;

        // Use ended fate data if available
        if (pending.EndedFate != null)
        {
            completion.FateName = pending.EndedFate.Name?.ToString() ?? "Unknown";
            completion.FateId = pending.EndedFate.FateId;
        }

        // Correlate inventory changes with rewards
        foreach (var inventoryEvent in pending.InventoryChanges)
        {
            // Skip equipment durability
            if (inventoryEvent.Item.ContainerType is GameInventoryType.EquippedItems or GameInventoryType.KeyItems)
                continue;

            // Check if this item is already in our rewards list
            var existingReward = completion.Items.FirstOrDefault(r => r.ItemId == inventoryEvent.Item.ItemId);
            if (existingReward == null)
            {
                var baseItem = dataManager.GetExcelSheet<Item>()?.GetRow(inventoryEvent.Item.ItemId);
                completion.Items.Add(new ItemReward
                {
                    ItemId = inventoryEvent.Item.ItemId,
                    Quantity = (int)inventoryEvent.Item.Quantity,
                    ItemName = baseItem?.Name.ExtractText() ?? "Unknown"
                });
            }
        }

        // Mark as successful if any items were rewarded
        if (completion.Items.Any())
        {
            completion.WasSuccessful = true;
        }

        return completion.FateName != string.Empty || completion.Items.Any() ? completion : null;
    }

    private void LogCompletedFate(FateCompletionData completion)
    {
        log.Info("=== FATE COMPLETION DETECTED ===");
        log.Info($"Fate: {completion.FateName} (ID: {completion.FateId})");
        log.Info($"Experience: {completion.Experience}");
        log.Info($"Tomestones: {completion.Tomestones}");
        log.Info($"Completion Level: {completion.CompletionLevel}");
        log.Info($"Success: {completion.WasSuccessful}");
        log.Info($"Items ({completion.Items.Count}):");

        foreach (var item in completion.Items)
        {
            log.Info($"  - {item.ItemName} (ID: {item.ItemId}) x{item.Quantity}");
        }

        log.Info($"Data Sources - Addon: {completion.HasAddonData}, Inventory: {completion.HasInventoryData}, Chat: {completion.HasChatData}");
        log.Info($"Completion Time: {completion.CompletionTime:yyyy-MM-dd HH:mm:ss} UTC");
        log.Info("================================");

        // Serialize for API upload or further processing
        var json = JsonConvert.SerializeObject(completion, Formatting.Indented);
        log.Debug($"Fate Completion JSON: {json}");
    }

    // Public method to get completed fates for debugging/UI
    public List<FateCompletionData> GetCompletedFates() => completedFates.ToList();
    public Dictionary<DateTime, PendingFateCompletion> GetPendingCompletions() => new(pendingCompletions);
}

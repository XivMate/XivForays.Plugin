using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;
using XivMate.DataGathering.Forays.Dalamud.Models;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Gathering;

internal sealed partial class ForaySignalModule(
    IChatGui chatGui,
    IFramework framework,
    IObjectTable objects,
    IDataManager data,
    ForayService forayService,
    ObservationService observations,
    SchedulerService scheduler,
    IPluginLog log) : IModule
{
    private Dictionary<ulong, (uint BaseId, Vector3 Position)> treasures = new();
    private Dictionary<ushort, Observation> encounters = new();
    private Guid visitId;

    public bool Enabled { get; private set; }

    public void LoadConfig(Configuration.Configuration configuration)
    {
        if (Enabled == configuration.CanCrowdsourceData)
            return;

        Enabled = configuration.CanCrowdsourceData;
        if (Enabled)
        {
            chatGui.ChatMessage += OnGameNotice;
            scheduler.ScheduleOnFrameworkThread(SignalTick, 1000);
        }
        else
        {
            chatGui.ChatMessage -= OnGameNotice;
            scheduler.CancelScheduledTask(SignalTick);
            treasures.Clear();
            encounters.Clear();
        }
    }

    private void OnGameNotice(IHandleableChatMessage chat)
    {
        if (!Enabled || !framework.IsInFrameworkUpdateThread)
            return;

        // Check the system channel before accessing the message. Only typed numbers leave this method.
        var kind = (XivChatType)chat.LogKind;
        if (chat.SourceKind != XivChatRelationKind.LocalPlayer ||
            kind is not (XivChatType.LootNotice or XivChatType.SystemError))
            return;

        if (objects.LocalPlayer == null || !forayService.IsInRecordableTerritory())
            return;

        var message = chat.OriginalMessage;
        var text = message.ExtractText().Trim();
        if (kind == XivChatType.SystemError)
        {
            var chain = ChainNotice().Match(text);
            if (chain.Success && uint.TryParse(chain.Groups[1].ValueSpan, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var count))
            {
                observations.Record(observations.GetContext() with { Kind = ObservationKind.Chain, ChainNumber = count });
                return;
            }

            if (chat.TargetKind is (XivChatRelationKind.EngagedEnemy or XivChatRelationKind.UnengagedEnemy) &&
                text.StartsWith("You defeat ", StringComparison.Ordinal))
                observations.Record(observations.GetContext() with { Kind = ObservationKind.SelfDefeat });
            return;
        }

        if (!text.StartsWith("You obtain ", StringComparison.Ordinal))
            return;

        var items = SeString.Parse(message.Data.Span).Payloads.OfType<ItemPayload>().Take(2).ToArray();
        if (items.Length != 1 || items[0].ItemId == 0)
            return;

        var quantity = LootQuantity().Match(text);
        uint? amount = quantity.Success && uint.TryParse(quantity.Groups[1].ValueSpan, NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        if (text.StartsWith("You obtain a ", StringComparison.Ordinal) ||
            text.StartsWith("You obtain an ", StringComparison.Ordinal))
            amount = 1;

        observations.Record(observations.GetContext() with
        {
            Kind = ObservationKind.Loot,
            GameId = items[0].ItemId,
            Quantity = amount
        });
    }

    private void SignalTick()
    {
        if (!Enabled || !forayService.IsInRecordableTerritory() || objects.LocalPlayer == null)
            return;

        try
        {
            var context = observations.GetContext();
            if (visitId != context.VisitId)
            {
                visitId = context.VisitId;
                treasures.Clear();
                encounters.Clear();
            }

            SampleTreasure(context);
            if (ForayService.IsOccultCrescent(context.TerritoryId))
                SampleEncounters(context);
        }
        catch (Exception ex)
        {
            log.Warning($"Foray signal sampling failed ({ex.GetType().Name}).");
        }
    }

    private void SampleTreasure(Observation context)
    {
        var current = new Dictionary<ulong, (uint BaseId, Vector3 Position)>();
        foreach (var obj in objects)
        {
            if (obj.BaseId == 0)
                continue;

            var (kind, grade) = ClassifyTreasure(obj);
            if (kind == 0)
                continue;

            var position = obj.Position;
            var id = obj.GameObjectId;
            current[id] = (obj.BaseId, position);
            if (treasures.TryGetValue(id, out var previous) && previous.BaseId == obj.BaseId &&
                Vector3.DistanceSquared(previous.Position, position) < 4)
            {
                // Compare against the last emitted point so slow movement is still sampled.
                current[id] = previous;
                continue;
            }

            observations.Record(context with
            {
                Kind = ObservationKind.Treasure,
                GameId = obj.BaseId,
                TreasureKind = kind,
                TreasureGrade = grade,
                X = position.X,
                Y = position.Y,
                Z = position.Z
            });
        }

        treasures = current;
    }

    private (byte Kind, byte? Grade) ClassifyTreasure(IGameObject obj)
    {
        if (obj.ObjectKind == ObjectKind.Treasure)
        {
            byte? grade = null;
            if (data.GetExcelSheet<Treasure>().TryGetRow(obj.BaseId, out var treasure))
            {
                grade = treasure.SGB.RowId switch
                {
                    1596 => 1,
                    1597 => 2,
                    1598 => 3,
                    _ => null
                };
            }

            return (1, grade);
        }

        if (obj.ObjectKind == ObjectKind.EventObj)
        {
            if (obj.BaseId == 2012936)
                return (1, 3);

            if (data.GetExcelSheet<EObjName>(ClientLanguage.English).TryGetRow(obj.BaseId, out var row))
                return ClassifyTreasureName(row.Singular.ExtractText());
        }
        else if (obj is IBattleNpc npc &&
                 data.GetExcelSheet<BNpcName>(ClientLanguage.English).TryGetRow(npc.NameId, out var row))
        {
            return ClassifyTreasureName(row.Singular.ExtractText());
        }

        return (0, null);
    }

    private static (byte Kind, byte? Grade) ClassifyTreasureName(string name) => name.ToLowerInvariant() switch
    {
        "bronze coffer" or "bronze treasure coffer" => (1, 1),
        "silver coffer" or "silver treasure coffer" => (1, 2),
        "gold coffer" or "gold treasure coffer" => (1, 3),
        "treasure coffer" => (1, null),
        "happy bunny" => (2, null),
        "persistent pot" or "pleading pot" => (3, null),
        "chewed carrot" or "fortune carrot" => (4, null),
        _ => (0, null)
    };

    private unsafe void SampleEncounters(Observation context)
    {
        var container = DynamicEventContainer.GetInstance();
        if (container == null)
            return;

        var current = new Dictionary<ushort, Observation>();
        foreach (ref var encounter in container->Events)
        {
            var id = encounter.DynamicEventId;
            if (id == 0 || encounter.State == DynamicEventState.Inactive)
                continue;

            var marker = encounter.MapMarker;
            var sample = context with
            {
                Kind = ObservationKind.CriticalEncounter,
                GameId = id,
                StartedAt = encounter.StartTimestamp > 0 ? encounter.StartTimestamp : null,
                State = (byte)encounter.State,
                Progress = encounter.Progress,
                SecondsLeft = encounter.SecondsLeft,
                X = marker.Position == Vector3.Zero ? null : marker.Position.X,
                Y = marker.Position == Vector3.Zero ? null : marker.Position.Y,
                Z = marker.Position == Vector3.Zero ? null : marker.Position.Z,
                Radius = marker.Radius > 0 ? marker.Radius : null
            };
            current[id] = sample;
            if (!encounters.TryGetValue(id, out var previous) || previous.State != sample.State ||
                previous.Progress != sample.Progress || previous.StartedAt != sample.StartedAt ||
                previous.X != sample.X || previous.Y != sample.Y || previous.Z != sample.Z ||
                previous.Radius != sample.Radius)
                observations.Record(sample);
        }

        foreach (var (id, previous) in encounters)
        {
            if (!current.ContainsKey(id))
                observations.Record(context with
                {
                    Kind = ObservationKind.CriticalEncounter,
                    GameId = id,
                    StartedAt = previous.StartedAt,
                    State = (byte)DynamicEventState.Inactive,
                    X = previous.X,
                    Y = previous.Y,
                    Z = previous.Z,
                    Radius = previous.Radius
                });
        }

        encounters = current;
    }

    [GeneratedRegex(@"^EXP chain #([0-9]+)!", RegexOptions.CultureInvariant)]
    private static partial Regex ChainNotice();

    [GeneratedRegex(@"^You obtain ([0-9]+) ", RegexOptions.CultureInvariant)]
    private static partial Regex LootQuantity();

    public void Dispose() => LoadConfig(new Configuration.Configuration());
}

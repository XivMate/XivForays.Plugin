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
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Gathering;

internal sealed partial class ForaySignalModule(
    IChatGui chatGui,
    IFramework framework,
    IObjectTable objects,
    IDataManager data,
    IPartyList partyList,
    IFateTable fateTable,
    ForayService forayService,
    ObservationService observations,
    PlayerContextSampler playerContext,
    EnemyTrackingService enemies,
    FateRewardWindow fateRewards,
    SchedulerService scheduler,
    IPluginLog log) : IModule
{
    /// <summary>How often an unresolved defeat notice looks for its enemy again.</summary>
    private const int DefeatResolveMs = 100;

    private readonly NoticePairing pairing = new();
    private readonly DropLinker linker = new();
    private readonly Lazy<long?> gameVersion = new(() =>
        LogNotices.GameVersionNumber(data.GameData.Repositories.TryGetValue("ffxiv", out var repo) ? repo.Version : null));
    private Dictionary<ulong, (uint BaseId, Vector3 Position)> treasures = new();
    private Dictionary<ushort, Observation> encounters = new();
    private Guid visitId;
    private long lastHeartbeat;
    private bool lootListening;

    public bool Enabled { get; private set; }

    public void LoadConfig(Configuration.Configuration configuration)
    {
        if (Enabled == configuration.CanCrowdsourceData)
            return;

        Enabled = configuration.CanCrowdsourceData;
        if (Enabled)
        {
            chatGui.ChatMessage += OnGameNotice;
            chatGui.LogMessage += OnLogMessage;
            lootListening = true;
            scheduler.ScheduleOnFrameworkThread(SignalTick, StreamRates.SignalScanMs);
            scheduler.ScheduleOnFrameworkThread(ResolveTick, DefeatResolveMs);
        }
        else
        {
            chatGui.ChatMessage -= OnGameNotice;
            chatGui.LogMessage -= OnLogMessage;
            lootListening = false;
            pairing.Clear();
            linker.Clear();
            fateRewards.Clear();
            scheduler.CancelScheduledTask(SignalTick);
            scheduler.CancelScheduledTask(ResolveTick);
            treasures.Clear();
            encounters.Clear();
            lastHeartbeat = 0;
            playerContext.Reset();
        }
    }

    /// <summary>
    /// Language-independent notices (task 2026-11-08-1353): the LogMessage row id and its integer parameters. Read-only:
    /// the message is never prevented or changed. Names of the source/target are compared in memory only.
    /// </summary>
    private void OnLogMessage(ILogMessage message)
    {
        if (!Enabled || !framework.IsInFrameworkUpdateThread)
            return;
        var player = objects.LocalPlayer;
        if (player == null || !forayService.IsInRecordableTerritory())
            return;

        try
        {
            int? Int(int index) => message.TryGetIntParameter(index, out var value) ? value : null;
            var row = message.LogMessageId;
            var now = Environment.TickCount64;
            if (LogNotices.Chain(row, Int) is { } chain)
            {
                pairing.Structured((int)ObservationKind.Chain, now);
                RecordChain(chain.Number, chain.SecondsLeft);
                return;
            }

            if (LogNotices.Loot(row, Int) is { } loot)
            {
                pairing.Structured((int)ObservationKind.Loot, now);
                RecordLoot(loot.ItemId, loot.Quantity, row, player.Position);
                return;
            }

            if (row == LogNotices.DefeatRow)
            {
                var source = message.SourceEntity;
                var target = message.TargetEntity;
                var self = source is { IsPlayer: true } && source.HomeWorldId == player.HomeWorld.RowId &&
                           source.Name.ExtractText() == player.Name.TextValue;
                // The party member is compared here in memory only; the record carries the category.
                var party = !self && source is { IsPlayer: true } && IsPartyMember(source);
                var by = LogNotices.DefeatBy(row, self, party, target?.IsPlayer ?? true);
                if (by == null)
                    return;
                if (by == (byte)ObjectCategory.Self)
                    pairing.Structured((int)ObservationKind.SelfDefeat, now);
                RecordDefeat(by.Value, target == null ? null : LogNotices.EnemyNameId(target.ObjStrId));
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Foray notice reading failed ({ex.GetType().Name}).");
        }
    }

    private void RecordChain(uint number, uint? secondsLeft)
    {
        var chainRecord = observations.GetContext() with
        {
            Kind = ObservationKind.Chain, ChainNumber = number, ChainSecondsLeft = secondsLeft
        };
        linker.Chain(chainRecord, Emit);
        playerContext.NoteChain(number, chainRecord.ObservedAtMs);
    }

    /// <summary>A defeat notice (kind 13): the enemy is looked up now and, if not found yet, by <see cref="ResolveTick"/>.</summary>
    private void RecordDefeat(byte by, uint? enemyNameId)
    {
        var defeat = observations.GetContext() with { Kind = ObservationKind.SelfDefeat, DefeatBy = by };
        linker.Defeat(defeat, enemyNameId, enemies.IdentifyDefeat, Emit);
    }

    /// <summary>
    /// A loot notice (kind 9) with its link: the latest defeat (via the linker), the player's chain, the notice row,
    /// and the FATE id when a FATE reward window is open (then never a kill link).
    /// </summary>
    private void RecordLoot(uint itemId, uint? quantity, uint? noticeRow, Vector3 playerPosition)
    {
        var context = observations.GetContext();
        var chain = playerContext.ChainAt(context.ObservedAtMs);
        fateRewards.Observe(context.VisitId, CurrentFates(context.TerritoryId), context.ObservedAtMs);
        var fate = fateRewards.Open(context.VisitId, context.ObservedAtMs, playerPosition.X, playerPosition.Z);
        linker.Loot(context with
        {
            Kind = ObservationKind.Loot,
            GameId = itemId,
            Quantity = quantity,
            NoticeRow = noticeRow,
            ChainNumber = chain.Number,
            ChainAgeMs = chain.AgeMs
        }, fate, Emit);
    }

    private List<FateSnapshot> CurrentFates(uint territory)
    {
        var list = new List<FateSnapshot>();
        foreach (var fate in fateTable)
            if (fate.TerritoryType.RowId == territory)
                list.Add(new FateSnapshot(fate.FateId, (byte)fate.State, fate.Progress, fate.StartTimeEpoch,
                    fate.Position.X, fate.Position.Y, fate.Position.Z, fate.Radius));
        return list;
    }

    private bool IsPartyMember(ILogMessageEntity source)
    {
        var name = source.Name.ExtractText();
        foreach (var member in partyList)
            if (member.World.RowId == source.HomeWorldId && member.Name.TextValue == name)
                return true;
        return false;
    }

    /// <summary>Records only while the record's content (Eureka, Bozja, Occult Crescent) is ticked.</summary>
    private void Emit(Observation observation)
    {
        if (forayService.IsContentEnabled(observation.TerritoryId))
            observations.Record(observation);
    }

    private void ResolveTick()
    {
        if (!Enabled || linker.PendingCount == 0)
            return;
        try
        {
            linker.Tick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), enemies.IdentifyDefeat, Emit);
        }
        catch (Exception ex)
        {
            log.Warning($"Foray defeat linking failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// The English chat-text fallback (system and loot channels of the local player only; never player chat). It
    /// records only notices the structured LogMessage path did not take (see <see cref="NoticePairing"/>).
    /// </summary>
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
        var now = Environment.TickCount64;
        if (kind == XivChatType.SystemError)
        {
            if (EnglishNotices.Chain(text) is { } count)
            {
                if (pairing.FallbackRecords((int)ObservationKind.Chain, now))
                    RecordChain(count, null);
                return;
            }

            if (chat.TargetKind is (XivChatRelationKind.EngagedEnemy or XivChatRelationKind.UnengagedEnemy) &&
                EnglishNotices.IsSelfDefeat(text) && pairing.FallbackRecords((int)ObservationKind.SelfDefeat, now))
                RecordDefeat((byte)ObjectCategory.Self, null);
            return;
        }

        if (!EnglishNotices.IsLoot(text))
            return;

        var items = SeString.Parse(message.Data.Span).Payloads.OfType<ItemPayload>().Take(2).ToArray();
        if (items.Length != 1 || items[0].ItemId == 0 || !pairing.FallbackRecords((int)ObservationKind.Loot, now))
            return;

        RecordLoot(items[0].ItemId, EnglishNotices.LootQuantity(text), null, objects.LocalPlayer!.Position);
    }

    private void SignalTick()
    {
        if (!Enabled || !forayService.IsInRecordableTerritory())
            return;

        var player = objects.LocalPlayer;
        if (player == null)
            return;

        try
        {
            var context = observations.GetContext();
            if (visitId != context.VisitId)
            {
                linker.Flush(Emit);
                visitId = context.VisitId;
                treasures.Clear();
                encounters.Clear();
                lastHeartbeat = 0;
            }

            // Coverage heartbeat: own position + context, grouped by VisitId (no name, no content ID).
            var now = Environment.TickCount64;
            if (Heartbeat.IsDue(now, lastHeartbeat))
            {
                lastHeartbeat = now;
                observations.Record(Heartbeat.Create(context, player.Position, gameVersion.Value, lootListening));
            }

            SampleTreasure(context);
            if (ForayZones.HasCriticalEngagements(context.TerritoryId))
                SampleEncounters(context);
            // Kind 19 player context: on change (at most every 5 s) or every 60 s.
            playerContext.Tick(context, observations.Record);
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
        // Bozja and Zadnor CEs live in the Bozja director's DynamicEventContainer; the Horns use the
        // Occult Crescent director's. Same struct, so the sampling below is shared.
        // Task 2026-11-08-1353: read-only, no game function calls. The active content director (static address) is the
        // zone's public content director; its type field is checked before the cast.
        DynamicEventContainer* container = null;
        var events = EventFramework.Instance();
        var director = events == null ? null : events->DirectorModule.ActiveContentDirector;
        if (director == null)
            return;
        var type = ((PublicContentDirector*)director)->Type;
        if (ForayZones.IsBozja(context.TerritoryId) && type == PublicContentDirectorType.Bozja)
            container = &((PublicContentBozja*)director)->DynamicEventContainer;
        else if (ForayZones.IsOccultCrescent(context.TerritoryId) && type == PublicContentDirectorType.OccultCrescent)
            container = &((PublicContentOccultCrescent*)director)->DynamicEventContainer;
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



    public void Dispose() => LoadConfig(new Configuration.Configuration());
}

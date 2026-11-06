using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

public sealed class EnemyTrackingService(IObjectTable objectTable, IDataManager dataManager, ForayService forayService)
{
    private readonly Lazy<HashSet<uint>> adaptationIds = new(() => FindStatuses(dataManager, "Adaptation", "Adaption"));
    private readonly Lazy<HashSet<uint>> mutationIds = new(() => FindStatuses(dataManager, "Mutation"));
    private Dictionary<ulong, TrackedEnemy> tracked = new();
    private Guid visitId;

    public bool IsInRecordableTerritory() => forayService.IsInRecordableTerritory() && objectTable.LocalPlayer != null;

    public Dictionary<ulong, EnemyPosition> GetVisibleEnemies()
    {
        var positions = new Dictionary<ulong, EnemyPosition>();
        if (!IsInRecordableTerritory())
            return positions;

        foreach (var enemy in objectTable.OfType<IBattleNpc>())
        {
            if (IsEnemy(enemy) && enemy.CurrentHp > 0)
                positions[enemy.GameObjectId] = Read(enemy, Guid.Empty, 0, 0,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null).Position;
        }

        return positions;
    }

    internal void Reset() => tracked.Clear();

    internal List<EnemyPosition> Sample(Observation context, bool takeSnapshot, Action<Observation> record)
    {
        if (visitId != context.VisitId)
        {
            Reset();
            visitId = context.VisitId;
        }

        var next = new Dictionary<ulong, TrackedEnemy>();
        var snapshot = new List<EnemyPosition>();
        foreach (var enemy in objectTable.OfType<IBattleNpc>())
        {
            if (!IsEnemy(enemy))
                continue;

            var id = enemy.GameObjectId;
            tracked.TryGetValue(id, out var previous);
            if (enemy.CurrentHp == 0 && previous?.Position.MobIngameId != enemy.BaseId)
                continue;

            if (previous != null && previous.Position.MobIngameId != enemy.BaseId)
            {
                record(ToObservation(previous, context, ObservationKind.EnemyDisappeared));
                previous = null;
            }

            if (enemy.CurrentHp > 0 && previous is { IsDead: true })
            {
                record(ToObservation(previous, context, ObservationKind.EnemyDisappeared));
                previous = null;
            }

            var current = Read(enemy, context.VisitId, context.TerritoryId, context.MapId,
                context.ObservedAtMs / 1000, previous);
            next[id] = current;
            if (current.IsDead)
            {
                if (previous is { IsDead: false })
                    record(ToObservation(current, context, ObservationKind.EnemyDied));
                continue;
            }

            if (takeSnapshot)
                snapshot.Add(current.Position);

            if (previous == null)
                record(ToObservation(current, context, ObservationKind.EnemyAppeared));
            else if (takeSnapshot || previous.ForayLevel != current.ForayLevel ||
                     previous.Position.IsAdapted != current.Position.IsAdapted ||
                     previous.Position.IsMutated != current.Position.IsMutated)
                record(ToObservation(current, context, ObservationKind.EnemySample));
        }

        foreach (var (id, enemy) in tracked)
        {
            if (!next.ContainsKey(id))
                record(ToObservation(enemy, context, ObservationKind.EnemyDisappeared));
        }

        tracked = next;
        return snapshot;
    }

    private static bool IsEnemy(IBattleNpc enemy) =>
        enemy.BattleNpcKind == BattleNpcSubKind.Combatant && enemy.BaseId != 0 && enemy.Address != IntPtr.Zero;

    private unsafe TrackedEnemy Read(IBattleNpc enemy, Guid visit, uint territory, uint map, long now,
        TrackedEnemy? previous)
    {
        var dead = enemy.CurrentHp == 0;
        var native = (BattleChara*)enemy.Address;
        var forayLevel = native->ForayInfo.Level;
        var element = native->ForayInfo.Element;
        var inCombat = !dead && (enemy.TargetObject != null || enemy.CurrentHp < enemy.MaxHp);
        var adapted = dead ? previous?.Position.IsAdapted == true :
            enemy.StatusList.Any(status => adaptationIds.Value.Contains(status.StatusId));
        var mutated = dead ? previous?.Position.IsMutated == true :
            enemy.StatusList.Any(status => mutationIds.Value.Contains(status.StatusId));
        var name = dataManager.GetExcelSheet<BNpcName>().TryGetRow(enemy.NameId, out var row)
            ? row.Singular.ExtractText()
            : string.Empty;
        var displayName = enemy.Name.TextValue;
        if (name.Equals(displayName, StringComparison.OrdinalIgnoreCase))
            name = displayName;
        byte? level = forayLevel > 0 ? forayLevel : dead ? previous?.ForayLevel : null;
        var position = new EnemyPosition
        {
            MobIngameId = enemy.BaseId,
            MobName = name,
            Level = level ?? 0,
            X = enemy.Position.X,
            Y = enemy.Position.Y,
            Z = enemy.Position.Z,
            IsAdapted = adapted,
            IsMutated = mutated,
            TimeStamp = now,
            Element = element.ToString(CultureInfo.InvariantCulture),
            IsInCombat = inCombat,
            HasBeenInCombat = inCombat || previous?.Position.HasBeenInCombat == true,
            TerritoryId = (int)territory,
            MapId = (int)map,
            InstanceId = visit
        };

        return new TrackedEnemy(previous?.Id ?? Guid.NewGuid(), position, dead, enemy.Level, level, element);
    }

    private static Observation ToObservation(TrackedEnemy enemy, Observation context, ObservationKind kind) =>
        context with
        {
            Kind = kind,
            GameId = enemy.Position.MobIngameId,
            EnemyTrackId = enemy.Id,
            X = enemy.Position.X,
            Y = enemy.Position.Y,
            Z = enemy.Position.Z,
            EnemyElementalLevel = ForayService.IsEureka(context.TerritoryId) ? enemy.ForayLevel : null,
            EnemyKnowledgeLevel = ForayService.IsOccultCrescent(context.TerritoryId) ? enemy.ForayLevel : null,
            EnemyCombatLevel = enemy.CombatLevel,
            ElementId = ForayService.IsEureka(context.TerritoryId) ? enemy.ElementId : null,
            IsAdapted = enemy.Position.IsAdapted,
            IsMutated = enemy.Position.IsMutated,
            IsInCombat = enemy.Position.IsInCombat
        };

    private static HashSet<uint> FindStatuses(IDataManager dataManager, params string[] names) =>
        dataManager.GetExcelSheet<Status>(ClientLanguage.English)
            .Where(row => names.Contains(row.Name.ExtractText(), StringComparer.OrdinalIgnoreCase))
            .Select(row => row.RowId).ToHashSet();

    private sealed record TrackedEnemy(Guid Id, EnemyPosition Position, bool IsDead, byte CombatLevel,
        byte? ForayLevel, byte ElementId);
}

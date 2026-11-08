using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

public sealed class EnemyTrackingService(
    IObjectTable objectTable, IDataManager dataManager, ForayService forayService, IClientState clientState,
    PoseCaptureService poseCapture)
{
    private readonly Lazy<HashSet<uint>> adaptationIds = new(() => FindStatuses(dataManager, "Adaptation", "Adaption"));
    private readonly Lazy<HashSet<uint>> mutationIds = new(() => FindStatuses(dataManager, "Mutation"));
    private Dictionary<ulong, TrackedEnemy> tracked = new();
    // Position of each enemy's last appear/sample record, for the movement threshold.
    private Dictionary<ulong, (float X, float Y, float Z)> lastRecorded = new();
    private Guid visitId;

    public bool IsInRecordableTerritory() => forayService.IsInRecordableTerritory() && objectTable.LocalPlayer != null;

    public Dictionary<ulong, EnemyPosition> GetVisibleEnemies()
    {
        var positions = new Dictionary<ulong, EnemyPosition>();
        if (!IsInRecordableTerritory())
            return positions;

        // Debug view only: the same territory/map the upload context (ObservationService.GetContext) uses.
        var territory = (uint)clientState.TerritoryType;
        var map = clientState.MapId;
        foreach (var enemy in objectTable.OfType<IBattleNpc>())
        {
            if (IsEnemy(enemy) && enemy.CurrentHp > 0)
                positions[enemy.GameObjectId] = Read(enemy, forayService.VisitId, territory, map,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null).Position;
        }

        return positions;
    }

    internal void Reset()
    {
        tracked.Clear();
        lastRecorded.Clear();
        poseCapture.Reset();
    }

    /// <summary>
    /// Scans visible enemies. Records appear/die/disappear and EnemySample only when level, adaptation,
    /// mutation or model changes, the enemy moved <see cref="StreamRates.EnemyMoveThreshold"/>, or
    /// <paramref name="periodicSample"/> is set. Returns the legacy snapshot when <paramref name="takeSnapshot"/>.
    /// </summary>
    internal List<EnemyPosition> Sample(Observation context, bool takeSnapshot, bool periodicSample,
        Action<Observation> record)
    {
        if (visitId != context.VisitId)
        {
            Reset();
            visitId = context.VisitId;
        }

        poseCapture.BeginScan(context);
        var next = new Dictionary<ulong, TrackedEnemy>();
        var nextRecorded = new Dictionary<ulong, (float X, float Y, float Z)>();
        var snapshot = new List<EnemyPosition>();
        foreach (var enemy in objectTable.OfType<IBattleNpc>())
        {
            if (!IsEnemy(enemy))
                continue;

            var id = enemy.GameObjectId;
            tracked.TryGetValue(id, out var previous);
            if (enemy.CurrentHp == 0 && previous?.Position.MobIngameId != enemy.BaseId)
                continue;

            Guid? replacedTrackId = null;
            if (previous != null && previous.Position.MobIngameId != enemy.BaseId)
            {
                record(ToObservation(previous, context, ObservationKind.EnemyDisappeared));
                // A live bnpcId change on the same object: pose capture records it as a form change.
                if (!previous.IsDead)
                    replacedTrackId = previous.Id;
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
                {
                    var (claim, claimType, lastTarget) = poseCapture.Died(enemy, current.Id, current.LayoutId,
                        current.Home ?? current.FirstPosition, PoseMath.Form(current.Position.IsMutated, current.Position.IsAdapted));
                    var died = ToObservation(current, context, ObservationKind.EnemyDied) with
                    {
                        Claim = claim,
                        ClaimType = claimType,
                        LastTargetCategory = lastTarget
                    };
                    record(died);
                    poseCapture.Note(died);
                }

                continue;
            }

            poseCapture.Observe(enemy, current.Id, replacedTrackId, current.Position.IsMutated, current.Position.IsAdapted,
                current.LayoutId, context, record);

            if (takeSnapshot)
                snapshot.Add(current.Position);

            var here = (current.Position.X, current.Position.Y, current.Position.Z);
            var hasLast = lastRecorded.TryGetValue(id, out var last);
            nextRecorded[id] = hasLast ? last : here;
            if (previous == null)
            {
                var appeared = ToObservation(current, context, ObservationKind.EnemyAppeared) with
                {
                    Respawn = poseCapture.Appeared(current.Position.MobIngameId, current.LayoutId,
                        current.Home ?? current.FirstPosition, PoseMath.Form(current.Position.IsMutated, current.Position.IsAdapted))
                };
                record(appeared);
                if (appeared.Respawn != null)
                    poseCapture.Note(appeared);
                nextRecorded[id] = here;
            }
            else if (periodicSample || previous.ForayLevel != current.ForayLevel ||
                     previous.Position.IsAdapted != current.Position.IsAdapted ||
                     previous.Position.IsMutated != current.Position.IsMutated ||
                     previous.ModelCharaId != current.ModelCharaId || previous.ModelScale != current.ModelScale ||
                     !hasLast || StreamRates.MovedEnough(here.X - last.X, here.Y - last.Y, here.Z - last.Z))
            {
                record(ToObservation(current, context, ObservationKind.EnemySample));
                nextRecorded[id] = here;
            }
        }

        foreach (var (id, enemy) in tracked)
        {
            if (!next.ContainsKey(id))
                record(ToObservation(enemy, context, ObservationKind.EnemyDisappeared));
        }

        poseCapture.EndScan(context, record);
        tracked = next;
        lastRecorded = nextRecorded;
        return snapshot;
    }

    private static bool IsEnemy(IBattleNpc enemy) =>
        enemy.BattleNpcKind == BattleNpcSubKind.Combatant && enemy.BaseId != 0 && enemy.Address != IntPtr.Zero;

    private unsafe TrackedEnemy Read(IBattleNpc enemy, Guid visit, uint territory, uint map, long now,
        TrackedEnemy? previous)
    {
        var dead = enemy.CurrentHp == 0;
        var native = (BattleChara*)enemy.Address;
        var character = (Character*)enemy.Address;
        var forayLevel = native->ForayInfo.Level;
        // Model and scale identify mutated/transformed forms for the imaging work. Keep the last live value on death.
        int? modelCharaId = dead ? previous?.ModelCharaId : character->ModelContainer.ModelCharaId;
        float? modelScale = dead ? previous?.ModelScale : character->Scale;
        if (modelScale is { } scale && (!float.IsFinite(scale) || scale < 0))
            modelScale = null;
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

        // Pose capture v2 raw ids on kinds 1-4. Keep the last live values on death.
        var layoutId = dead && previous != null ? previous.LayoutId : character->GameObject.LayoutId;
        var fateId = dead && previous != null ? previous.FateId : character->GameObject.FateId;
        var homeRaw = (Vector3)character->GameObject.DefaultPosition;
        Vector3? home = dead && previous != null ? previous.Home
            : homeRaw == Vector3.Zero || !float.IsFinite(homeRaw.X) || !float.IsFinite(homeRaw.Y) || !float.IsFinite(homeRaw.Z)
                ? null : homeRaw;
        return new TrackedEnemy(previous?.Id ?? Guid.NewGuid(), position, dead, enemy.Level, level, element,
            modelCharaId, modelScale, layoutId, fateId, home, previous?.FirstPosition ?? enemy.Position);
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
            IsInCombat = enemy.Position.IsInCombat,
            ModelCharaId = enemy.ModelCharaId,
            ModelScale = enemy.ModelScale,
            LayoutId = enemy.LayoutId == 0 ? null : enemy.LayoutId,
            FateId = enemy.FateId == 0 ? null : enemy.FateId,
            HomePosition = enemy.Home is { } home ? PoseMath.Vec(home) : null
        };

    private static HashSet<uint> FindStatuses(IDataManager dataManager, params string[] names) =>
        dataManager.GetExcelSheet<Status>(ClientLanguage.English)
            .Where(row => names.Contains(row.Name.ExtractText(), StringComparer.OrdinalIgnoreCase))
            .Select(row => row.RowId).ToHashSet();

    private sealed record TrackedEnemy(Guid Id, EnemyPosition Position, bool IsDead, byte CombatLevel,
        byte? ForayLevel, byte ElementId, int? ModelCharaId, float? ModelScale, uint LayoutId, ushort FateId,
        Vector3? Home, Vector3 FirstPosition);
}

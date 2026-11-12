using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativePartyMember = FFXIVClientStructs.FFXIV.Client.Game.Group.PartyMember;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

/// <summary>
/// Kind 19 PlayerContext (task 2026-11-08-1300), sampled on the existing 1 s signal tick and recorded on change
/// (at most every 5 s) or every 60 s. READ-ONLY: static-address structs (PlayerState, UIState, Framework,
/// EventFramework, GroupManager party members) and the object table / party list; no game function calls, no hooks.
/// Group members are job / level / role / foray level / effective foray level / synced only; own statuses are
/// allow-listed.
/// </summary>
public sealed class PlayerContextSampler(IObjectTable objectTable, IPartyList partyList, IDataManager dataManager, IPluginLog log)
{
    private readonly Lazy<HashSet<uint>> allowedStatusIds = new(() =>
        dataManager.GetExcelSheet<Status>(ClientLanguage.English)
            .Where(row => ContextRules.IsAllowedStatusName(row.Name.ExtractText()))
            .Select(row => row.RowId).ToHashSet());

    private readonly Lazy<Dictionary<uint, byte>> roles = new(() =>
        dataManager.GetExcelSheet<ClassJob>().ToDictionary(row => row.RowId, row => row.Role));

    private PlayerContext? last;
    private Observation? lastTop;
    private long lastMs;
    private Guid visitId;
    private uint? lastChain;
    private long? lastChainAtMs;

    internal int Recorded { get; private set; }
    internal string LastLine { get; private set; } = "";

    /// <summary>The last group rows as text (numbers only), for the Debug tab: "job/level/role el eff/cur sync".</summary>
    internal string LastGroupLine { get; private set; } = "";

    internal void Reset()
    {
        last = null;
        lastTop = null;
        lastChain = null;
        lastChainAtMs = null;
        visitId = Guid.Empty;
    }

    /// <summary>From the "EXP chain #n" game notice (system channel only).</summary>
    internal void NoteChain(uint number, long atMs)
    {
        lastChain = number;
        lastChainAtMs = atMs;
    }

    /// <summary>
    /// The player's chain now (kinds 3 and 9): the last chain notice number while the UIState chain timer runs, 0 when
    /// no chain runs (see <see cref="ChainNow"/>).
    /// </summary>
    internal unsafe (uint? Number, uint? AgeMs) ChainAt(long nowMs)
    {
        var ui = UIState.Instance();
        if (ui == null)
            return (null, null);
        var remaining = ui->Chain.RemainingTime;
        return ChainNow.At(lastChain, lastChainAtMs, float.IsFinite(remaining) && remaining > 0, nowMs);
    }

    internal void Tick(Observation context, Action<Observation> record)
    {
        if (visitId != context.VisitId)
        {
            Reset();
            visitId = context.VisitId;
        }

        var current = Read(context);
        if (current == null)
            return;
        LastGroupLine = ContextLog.GroupLine(current.Group);
        var now = Environment.TickCount64;
        var changed = ContextRules.Diff(last, current, lastTop, context);
        var reason = ContextRules.Due(now, lastMs, last != null, changed);
        if (reason == 0)
            return;

        var observation = context with
        {
            Kind = ObservationKind.PlayerContext,
            Situation = current with { Reason = reason, Changed = changed }
        };
        last = current;
        lastTop = context;
        lastMs = now;
        record(observation);
        Recorded++;
        LastLine = ContextLog.Line(observation);
        log.Information(LastLine);
    }

    private unsafe PlayerContext? Read(Observation context)
    {
        var local = objectTable.LocalPlayer;
        var state = PlayerState.Instance();
        if (local == null || local.Address == IntPtr.Zero || state == null)
            return null;
        var native = (NativeCharacter*)local.Address;

        long? eorzea = null, utc = null;
        var framework = Framework.Instance();
        if (framework != null)
        {
            eorzea = framework->ClientTime.EorzeaTime;
            utc = framework->UtcTime.Timestamp;
        }

        float[]? chain = null;
        uint? publicInstance = null;
        var ui = UIState.Instance();
        if (ui != null)
        {
            var remaining = ui->Chain.RemainingTime;
            if (float.IsFinite(remaining) && remaining > 0)
                chain = [PoseMath.R2(remaining), PoseMath.R2(float.IsFinite(ui->Chain.MaxTime) ? ui->Chain.MaxTime : 0)];
            publicInstance = ui->PublicInstance.InstanceId == 0 ? null : ui->PublicInstance.InstanceId;
        }

        long? instanceStart = null;
        uint? directorRow = null;
        EurekaContext? eureka = null;
        uint[]? bozja = null;
        OccultContext? occult = null;
        var events = EventFramework.Instance();
        var director = events == null ? null : events->DirectorModule.ActiveContentDirector;
        if (director != null)
        {
            var common = (Director*)director;
            instanceStart = common->DirectorStartTimestamp == 0 ? null : common->DirectorStartTimestamp;
            directorRow = common->ContentId == 0 ? null : common->ContentId;
            // Inside a gated foray zone the active content director is its public content director; the type
            // field is checked too, so no struct is read under the wrong layout.
            var type = ((PublicContentDirector*)director)->Type;
            if (ForayZones.IsEureka(context.TerritoryId) && type == PublicContentDirectorType.Eureka)
            {
                var e = (PublicContentEureka*)director;
                eureka = new EurekaContext
                {
                    MaxElementalLevel = e->MaxElementalLevel,
                    Experience = e->State.CurrentExperience,
                    ExperienceNeeded = e->State.NeededExperience,
                    Board = [e->State.Fire, e->State.Ice, e->State.Wind, e->State.Earth, e->State.Lightning, e->State.Water],
                    Magicite = e->State.Magicite,
                    MagiaAether = e->State.MagiaAether,
                    MagiaAetherCharge = e->State.MagiaAetherCharge
                };
            }
            else if (ForayZones.IsBozja(context.TerritoryId) && type == PublicContentDirectorType.Bozja)
            {
                var b = (PublicContentBozja*)director;
                bozja = [b->State.CurrentExperience, b->State.NeededExperience];
            }
            else if (ForayZones.IsOccultCrescent(context.TerritoryId) && type == PublicContentDirectorType.OccultCrescent)
            {
                var o = (PublicContentOccultCrescent*)director;
                if (o->StateLoaded)
                {
                    var levels = new List<int>();
                    foreach (var level in o->State.SupportJobLevels)
                        levels.Add(level);
                    occult = new OccultContext
                    {
                        Knowledge = o->State.CurrentKnowledge,
                        KnowledgeNeeded = o->State.NeededKnowledge,
                        KnowledgeLevelSync = o->State.KnowledgeLevelSync,
                        SupportJob = o->State.CurrentSupportJob,
                        SupportJobLevels = levels.ToArray(),
                        Silver = o->State.Silver,
                        Gold = o->State.Gold
                    };
                }
            }
        }

        // Group: the OTHER members as numbers only (no names, content ids, object ids, worlds): job, level, role and
        // their foray level / effective level / sync flag, read from the GroupManager PartyMember content key/values
        // (a plain field read; the same data the party list shows in foray zones).
        var group = new List<int[]>();
        var self = local.EntityId;
        Span<(uint, uint)> values = stackalloc (uint, uint)[3];
        foreach (var member in partyList)
        {
            if (member.EntityId == self)
                continue;
            var job = member.ClassJob.RowId;
            var count = 0;
            var party = (NativePartyMember*)member.Address;
            if (party != null)
                foreach (var pair in party->ContentKeyValueData)
                    if (count < values.Length)
                        values[count++] = (pair.Item1, pair.Item2);
            group.Add(ContextRules.MemberRow(context.TerritoryId, (int)job, member.Level,
                roles.Value.TryGetValue(job, out var role) ? role : 0, values[..count]));
        }

        var statuses = new List<int[]>();
        foreach (var status in local.StatusList)
        {
            if (status.StatusId == 0 || !allowedStatusIds.Value.Contains(status.StatusId))
                continue;
            statuses.Add([(int)status.StatusId, status.Param,
                float.IsFinite(status.RemainingTime) ? (int)MathF.Round(status.RemainingTime * 10) : 0]);
        }

        return new PlayerContext
        {
            CaptureRevision = ContextRules.CaptureRevision,
            EorzeaTime = eorzea,
            FrameworkUtc = utc,
            InstanceStartTs = instanceStart,
            DirectorRowId = directorRow,
            PublicInstanceId = publicInstance,
            CurrentWorldId = local.CurrentWorld.RowId == 0 ? null : local.CurrentWorld.RowId,
            ClassJobId = state->CurrentClassJobId,
            Level = state->CurrentLevel,
            SyncedLevel = state->SyncedLevel,
            LevelSynced = state->IsLevelSynced,
            HpPct = local.MaxHp > 0 ? PoseMath.R2(100f * local.CurrentHp / local.MaxHp) : 0,
            MpPct = local.MaxMp > 0 ? PoseMath.R2(100f * local.CurrentMp / local.MaxMp) : null,
            Dead = local.IsDead || local.CurrentHp == 0,
            Mounted = native->Mount.MountId != 0,
            InCombat = (local.StatusFlags & StatusFlags.InCombat) != 0,
            Eureka = eureka,
            Bozja = bozja,
            Occult = occult,
            Chain = chain,
            LastChainNumber = lastChain,
            LastChainAtMs = lastChainAtMs,
            GroupSize = Math.Max(1, partyList.Length),
            Alliance = partyList.IsAlliance,
            Group = ContextRules.SortRows(group),
            Statuses = statuses.OrderBy(s => s[0]).ToArray()
        };
    }
}

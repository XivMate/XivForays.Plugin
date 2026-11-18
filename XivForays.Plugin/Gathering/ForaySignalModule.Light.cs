using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Gathering;

// Kind 22 light gain and kind 23 notice net (job 1830). The light amount is not in the chat message, so the kettle value is
// read from game memory (read only, same path and guard as docs/KETTLE_LIGHT.md) at every 1 s sample and about 1 s after
// each light notice. Everything the record carries is a number; no names, ids or text.
internal sealed partial class ForaySignalModule
{
    /// <summary>A light notice waits this long so the kettle value and EXP have updated and the kill is resolved.</summary>
    private const long GainHoldMs = 1000;

    private readonly GainKills gainKills = new();
    private readonly GainSamples gainSamples = new();
    private readonly NoticeNet noticeNet = new();
    private readonly List<PendingGain> pendingGains = [];

    private readonly KettleLightValueSource gainSource = new(log,
        () => data.GameData.Repositories.TryGetValue("ffxiv", out var repo) ? repo.Version : null, GainDirectorAddress);

    private sealed record PendingGain(long NoticeMs, long DueMs, Observation Context, PlayerContext? Snapshot, uint Row, byte Type, int[] Params);

    private static int[] NoticeParameters(global::Dalamud.Game.Chat.ILogMessage message)
    {
        var list = new List<int>();
        for (var i = 0; i < NoticeNet.MaxParams; i++)
        {
            if (!message.TryGetIntParameter(i, out var value))
                break;
            list.Add(value);
        }

        return list.ToArray();
    }

    /// <summary>The active content director's address when it is the Eureka director, else 0. Read each sample, never cached.</summary>
    private static unsafe ulong GainDirectorAddress()
    {
        var events = EventFramework.Instance();
        var director = events == null ? null : events->DirectorModule.ActiveContentDirector;
        if (director == null)
            return 0;
        return ((PublicContentDirector*)director)->Type == PublicContentDirectorType.Eureka ? (ulong)director : 0;
    }

    private void ClearGains()
    {
        pendingGains.Clear();
        gainKills.Clear();
        gainSamples.Clear();
        noticeNet.Clear();
    }

    /// <summary>A kill record that has been resolved and is being recorded: remembered for the 3 s light link.</summary>
    private void NoteGainKill(Observation observation)
    {
        if (observation.Kind == ObservationKind.SelfDefeat && ForayZones.IsEureka(observation.TerritoryId))
            gainKills.Add(GainKill.From(observation));
    }

    /// <summary>Kind 23: an unmatched LogMessage row with its integer parameters (numbers only), throttled and deduped.</summary>
    private void RecordNotice(uint row, int[] parameters)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!noticeNet.Accept(row, parameters, now))
            return;
        var context = observations.GetContext();
        if (!ForayZones.IsEureka(context.TerritoryId))
            return;
        Emit(context with { Kind = ObservationKind.Notice, NoticeRow = row, NoticeParams = parameters });
    }

    /// <summary>A light LogMessage row: remembered now, finished about 1 s later (<see cref="GainTick"/>).</summary>
    private void RecordGainNotice(uint row, byte type, int[] parameters)
    {
        var context = observations.GetContext();
        if (!LightGainRows.InKettleZone(context.TerritoryId))
            return;
        var snapshot = playerContext.Snapshot(context);
        var chain = playerContext.ChainAt(context.ObservedAtMs);
        context = context with { ChainNumber = chain.Number, ChainAgeMs = chain.AgeMs };
        pendingGains.Add(new PendingGain(context.ObservedAtMs, context.ObservedAtMs + GainHoldMs, context, snapshot, row, type, parameters));
    }

    /// <summary>Called every 1 s signal tick in Eureka: takes the sample and finishes light notices that are due.</summary>
    private void GainTick(Observation context, bool flushAll)
    {
        if (!ForayZones.IsEureka(context.TerritoryId))
        {
            FinishGains(null, true);
            return;
        }

        var kettle = LightGainRows.InKettleZone(context.TerritoryId);
        var value = kettle ? gainSource.Read() : null;
        var snapshot = playerContext.Snapshot(context);
        var sample = new GainSample(context.ObservedAtMs, value?.Yield, value?.Bar, snapshot?.Eureka?.Experience,
            snapshot?.Eureka?.ExperienceNeeded, context.PlayerElementalLevel, context.PlayerEffectiveElementalLevel,
            context.PlayerElementalSynced);
        lightLog.Source = !kettle ? "not in Pagos or Pyros" : gainSource.IsOn ? "kettle value read: on" : "kettle value read: off (notices only)";
        FinishGains(sample, flushAll);
        gainSamples.Add(sample);
    }

    private void FinishGains(GainSample? after, bool all)
    {
        if (pendingGains.Count == 0)
            return;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var due = pendingGains.Where(p => all || nowMs >= p.DueMs).ToList();
        foreach (var pending in due)
        {
            pendingGains.Remove(pending);
            var observation = BuildGain(pending, after);
            Emit(observation);
            var line = LightGainLog.Line(observation);
            lightLog.Add(line);
            log.Information(line);
        }
    }

    /// <summary>Builds the kind 22 record from a finished notice: the 3 s kill link, the before sample and the after sample.</summary>
    private static Observation BuildGain(
        PendingGain pending, GainSample? after, (GainKill Kill, uint GapMs)? kill, GainSample? before)
    {
        var chain = pending.Snapshot;
        var record = pending.Context with
        {
            Kind = ObservationKind.LightGain,
            ObservedAtMs = pending.NoticeMs,
            NoticeRow = pending.Row,
            GainType = pending.Type,
            NoticeParams = pending.Params.Length == 0 ? null : pending.Params,
            Situation = chain == null ? null : chain with { CurrentWorldId = null },
            GainYieldBefore = before?.Yield,
            GainBarBefore = before?.Bar,
            GainYieldAfter = after?.Yield,
            GainBarAfter = after?.Bar,
            GainElementalLevelBefore = before?.ElementalLevel,
            GainEffectiveLevelBefore = before?.EffectiveLevel,
            GainSyncedBefore = before?.Synced,
            GainExperienceBefore = before?.Experience,
            GainExperienceNeededBefore = before?.ExperienceNeeded,
            GainElementalLevelAfter = after?.ElementalLevel,
            GainEffectiveLevelAfter = after?.EffectiveLevel,
            GainSyncedAfter = after?.Synced,
            GainExperienceAfter = after?.Experience,
            GainExperienceNeededAfter = after?.ExperienceNeeded
        };
        if (kill is { } k)
            record = record with
            {
                GameId = k.Kill.GameId,
                IsMutated = k.Kill.IsMutated,
                IsAdapted = k.Kill.IsAdapted,
                Form = k.Kill.Form,
                DefeatBy = k.Kill.DefeatBy,
                DefeatTrackId = k.Kill.TrackId,
                GainKillGapMs = k.GapMs
            };
        return record;
    }

    private Observation BuildGain(PendingGain pending, GainSample? after)
    {
        var kill = gainKills.Find(pending.NoticeMs);
        var before = gainSamples.Before(kill?.Kill.AtMs ?? pending.NoticeMs);
        return BuildGain(pending, after, kill, before);
    }
}

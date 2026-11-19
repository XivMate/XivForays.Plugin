using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Gathering;

// Kind 22 light gain and kind 23 notice net. The light amount is not in the chat message, so the kettle value is
// read from game memory (read only, same path and guard as docs/KETTLE_LIGHT.md) at every 1 s sample and about 1 s after
// each light notice. Everything the record carries is a number; no names, ids or text.
internal sealed partial class ForaySignalModule
{
    /// <summary>A light notice waits this long so the kettle value and EXP have updated and the kill is resolved.</summary>
    private const long GainHoldMs = 1800;

    /// <summary>A repeat of the same light row and parameters this close to a pending one is the same notice.</summary>
    private const long GainDedupeMs = 400;

    private readonly ChainNotices chainNotices = new();

    private readonly GainKills gainKills = new();
    private readonly GainSamples gainSamples = new();
    private readonly NoticeNet noticeNet = new();
    private readonly List<PendingGain> pendingGains = [];

    // One light memory reader only: kind 20 and kind 22 share lightSource; LightTick stores the 1 s reading here.
    private KettleReading? lastLightReading;

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

    private void ClearGains()
    {
        pendingGains.Clear();
        gainKills.Clear();
        gainSamples.Clear();
        chainNotices.Clear();
        noticeNet.Clear();
    }

    /// <summary>An "EXP chain #n" notice: kept by time so a light gain can take the chain number of its own kill.</summary>
    private void NoteChainNotice(uint number, long atMs)
    {
        chainNotices.Add(number, atMs);
        NoteOrder("chain #" + number, atMs);
    }

    private int orderLogged;

    /// <summary>Raw notice order for the first 60 events of a session (numbers only), to confirm the order of light, chain and defeat notices.</summary>
    private void NoteOrder(string what, long atMs)
    {
        if (orderLogged++ < 60)
            lightLog.Add("order t=" + atMs % 1_000_000 + " " + what);
    }

    /// <summary>A defeat notice (own or party): remembered at once, before the enemy is resolved, with the enemy name id.</summary>
    private void NoteDefeatNotice(Observation defeat, uint? nameId)
    {
        if (!ForayZones.IsEureka(defeat.TerritoryId))
            return;
        gainKills.Upsert(GainKill.From(defeat, nameId));
        NoteOrder("defeat by " + defeat.DefeatBy + " nameId " + nameId, defeat.ObservedAtMs);
    }

    /// <summary>A kill record that has been resolved and is being recorded: remembered for the 3 s light link.</summary>
    private void NoteGainKill(Observation observation)
    {
        if (observation.Kind == ObservationKind.SelfDefeat && ForayZones.IsEureka(observation.TerritoryId))
            gainKills.Upsert(GainKill.From(observation));
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
        if (pendingGains.Any(p => p.Row == row && p.Params.AsSpan().SequenceEqual(parameters) &&
                                  Math.Abs(context.ObservedAtMs - p.NoticeMs) <= GainDedupeMs))
            return;
        NoteOrder("light row " + row, context.ObservedAtMs);
        var snapshot = playerContext.Snapshot(context);
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
        var value = kettle ? lastLightReading : null;
        var snapshot = playerContext.Snapshot(context);
        var sample = new GainSample(context.ObservedAtMs, value?.Yield, value?.Bar, snapshot?.Eureka?.Experience,
            snapshot?.Eureka?.ExperienceNeeded, context.PlayerElementalLevel, context.PlayerEffectiveElementalLevel,
            context.PlayerElementalSynced);
        lightLog.Source = !kettle ? "not in Pagos or Pyros" : lightSource.IsOn ? "kettle value read: on" : "kettle value read: off (notices only)";
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
            lightLog.Add(line, MakeRow(observation));
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
        long? killGap = kill is { } kf ? kf.GapMs : null;
        if (kill == null && gainKills.FindAfter(pending.NoticeMs, 1800) is { } later)
        {
            kill = later;
            killGap = -(long)later.GapMs;
        }

        var before = gainSamples.Before(kill?.Kill.AtMs ?? pending.NoticeMs);
        var record = BuildGain(pending, after, kill, before);
        var chain = chainNotices.Find(kill?.Kill.AtMs ?? pending.NoticeMs, pending.NoticeMs);
        lastGainExtra = (killGap, chain?.GapMs, kill?.Kill.NameId, kill?.Kill.Level);
        return record with
        {
            ChainNumber = chain?.Number,
            ChainAgeMs = chain is { } c ? (uint)Math.Abs(c.GapMs) : null
        };
    }

    private (long? KillGap, long? ChainGap, uint? NameId, byte? Level) lastGainExtra;

    private string StatusName(int id) =>
        data.GetExcelSheet<Status>().TryGetRow((uint)id, out var row) ? row.Name.ExtractText() : "";

    /// <summary>The Debug table and CSV row of a finished gain. Names are enemy and status names only.</summary>
    private LightRow MakeRow(Observation o)
    {
        var (killGap, chainGap, nameId, level) = lastGainExtra;
        var enemy = nameId is { } n && data.GetExcelSheet<BNpcName>().TryGetRow(n, out var row) ? row.Singular.ExtractText() : "";
        if (enemy == "" && o.GameId != null)
            enemy = "id " + o.GameId;
        var ids = o.Situation is { } s ? s.Statuses.Select(x => x[0]).ToArray() : [];
        var names = ids.Select(StatusName).Where(x => x is "Elemental EXP Up" or "Elemental Blessing: EXP" or "Elemental Harmony").ToArray();
        var amount = KettleLightChain.Delta(
            o.GainBarBefore is { } bb ? new KettleReading(o.GainYieldBefore ?? 0, bb) : null,
            o.GainBarAfter is { } ba ? new KettleReading(o.GainYieldAfter ?? 0, ba) : null, KettleLightChain.BarCap);
        var wrapped = o.GainYieldBefore != null && o.GainYieldAfter != null && o.GainYieldBefore != o.GainYieldAfter;
        return new LightRow(DateTimeOffset.FromUnixTimeMilliseconds(o.ObservedAtMs), o.ChainNumber, chainGap, o.GainType ?? 0, amount,
            wrapped && amount == null, enemy, o.IsMutated == true, o.IsAdapted == true, o.GameId, level, o.Situation?.GroupSize ?? 0,
            string.Join(" + ", names), string.Join(" ", ids), o.GainBarBefore, o.GainBarAfter, o.GainYieldBefore, o.GainYieldAfter,
            o.GainExperienceBefore, o.GainExperienceAfter, o.GainExperienceNeededAfter, o.GainElementalLevelAfter, killGap, o.NoticeRow ?? 0);
    }
}

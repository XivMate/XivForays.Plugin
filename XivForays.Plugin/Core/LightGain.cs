using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>
/// Kind 22 light gain and kind 23 notice net, dependency-free for tests. The light type is the LogMessage row, so no
/// text is read and the mapping is the same in every client language.
/// </summary>
internal static class LightGainRows
{
    public const uint Feeble = 9020, ExplodingStar = 9026, Full = 9027, ForgeConverted = 3743;

    /// <summary>1 feeble, 2 gentle, 3 bright, 4 brilliant, 5 blinding, 6 newborn star, 7 exploding star, 8 kettle full, 9 forge converted; null for any other row.</summary>
    public static byte? Type(uint row) => row switch
    {
        >= Feeble and <= ExplodingStar => (byte)(row - Feeble + 1),
        Full => 8,
        ForgeConverted => 9,
        _ => null
    };

    /// <summary>The kettle exists in Pagos and Pyros only.</summary>
    public static bool InKettleZone(uint territory) => territory is ForayZones.EurekaPagos or ForayZones.EurekaPyros;
}

/// <summary>One kill (own or party) as the light link needs it.</summary>
internal readonly record struct GainKill(long AtMs, uint? GameId, bool? IsMutated, bool? IsAdapted, byte? Form, byte? DefeatBy, Guid? TrackId,
    uint? NameId = null, byte? Level = null)
{
    public static GainKill From(Observation kill, uint? nameId = null) =>
        new(kill.ObservedAtMs, kill.GameId, kill.IsMutated, kill.IsAdapted, kill.Form, kill.DefeatBy, kill.EnemyTrackId, nameId,
            kill.EnemyElementalLevel is > 0 ? kill.EnemyElementalLevel : kill.EnemyCombatLevel);
}

/// <summary>The recent kills, newest last. The link picks the latest kill within the window before a light notice.</summary>
internal sealed class GainKills
{
    /// <summary>A kill counts when it came at most this long before the light notice.</summary>
    public const long WindowMs = 3000;

    private const int Keep = 16;
    private readonly List<GainKill> kills = [];

    public void Add(GainKill kill)
    {
        kills.Add(kill);
        if (kills.Count > Keep)
            kills.RemoveAt(0);
    }

    /// <summary>Adds the kill, or fills in the entry already noted for the same defeat notice (same time and source).</summary>
    public void Upsert(GainKill kill)
    {
        var i = kills.FindIndex(k => k.AtMs == kill.AtMs && k.DefeatBy == kill.DefeatBy);
        if (i < 0)
        {
            Add(kill);
            return;
        }

        kills[i] = kill with { NameId = kill.NameId ?? kills[i].NameId };
    }

    /// <summary>The earliest kill that came 0 to <paramref name="maxMs"/> after the notice (the defeat notice can trail the light notice).</summary>
    public (GainKill Kill, uint GapMs)? FindAfter(long noticeMs, long maxMs)
    {
        foreach (var k in kills)
        {
            var gap = k.AtMs - noticeMs;
            if (gap is > 0 && gap <= maxMs)
                return (k, (uint)gap);
        }

        return null;
    }

    public void Clear() => kills.Clear();

    /// <summary>The latest kill from 0 to 3000 ms before <paramref name="noticeMs"/>, with the gap; null when there is none.</summary>
    public (GainKill Kill, uint GapMs)? Find(long noticeMs)
    {
        for (var i = kills.Count - 1; i >= 0; i--)
        {
            var gap = noticeMs - kills[i].AtMs;
            if (gap is >= 0 and <= WindowMs)
                return (kills[i], (uint)gap);
        }

        return null;
    }
}

/// <summary>The EXP chain notices ("EXP chain #n", row 549), newest last, so a light gain gets the chain number of its own kill.</summary>
internal sealed class ChainNotices
{
    public const long WindowMs = 3000;
    private const int Keep = 16;
    private readonly List<(uint Number, long AtMs)> notices = [];

    public void Add(uint number, long atMs)
    {
        notices.Add((number, atMs));
        if (notices.Count > Keep)
            notices.RemoveAt(0);
    }

    public void Clear() => notices.Clear();

    /// <summary>The chain notice nearest in time to <paramref name="aroundMs"/> (the kill, else the light notice) within 3000 ms either way; Gap is notice time minus light notice time.</summary>
    public (uint Number, long GapMs)? Find(long aroundMs, long lightNoticeMs)
    {
        (uint Number, long AtMs)? best = null;
        foreach (var n in notices)
            if (Math.Abs(n.AtMs - aroundMs) <= WindowMs && (best == null || Math.Abs(n.AtMs - aroundMs) <= Math.Abs(best.Value.AtMs - aroundMs)))
                best = n;
        return best is { } b ? (b.Number, b.AtMs - lightNoticeMs) : null;
    }
}

/// <summary>The kettle value and Eureka experience at one 1 s sample. Absent parts are null.</summary>
internal readonly record struct GainSample(
    long AtMs, int? Yield, int? Bar, uint? Experience, uint? ExperienceNeeded, uint? ElementalLevel, uint? EffectiveLevel, bool? Synced);

/// <summary>The last few samples. A light gain's "before" is the newest sample taken at or before the kill (else the notice).</summary>
internal sealed class GainSamples
{
    private const int Keep = 12;
    private readonly List<GainSample> samples = [];

    public void Add(GainSample sample)
    {
        samples.Add(sample);
        if (samples.Count > Keep)
            samples.RemoveAt(0);
    }

    public void Clear() => samples.Clear();

    public GainSample? Before(long cutoffMs)
    {
        for (var i = samples.Count - 1; i >= 0; i--)
            if (samples[i].AtMs <= cutoffMs)
                return samples[i];
        return null;
    }
}

/// <summary>
/// Kind 23: low-rate net of every other LogMessage row id with its integer parameters, so a wrong light row can be found
/// afterwards. A repeat of the same row and parameters inside <see cref="DedupeMs"/> is dropped, and at most
/// <see cref="MaxPerWindow"/> records leave per <see cref="WindowMs"/>. The light rows themselves are kind 22, not kind 23.
/// </summary>
internal sealed class NoticeNet
{
    public const long DedupeMs = 60_000;
    public const long WindowMs = 60_000;
    public const int MaxPerWindow = 12;
    public const int MaxParams = 6;

    private readonly Dictionary<string, long> seen = [];
    private long windowStart;
    private int inWindow;

    public void Clear()
    {
        seen.Clear();
        inWindow = 0;
    }

    public bool Accept(uint row, ReadOnlySpan<int> parameters, long nowMs)
    {
        if (row == 0 || LightGainRows.Type(row) != null)
            return false;
        var key = row + ":" + string.Join(",", parameters.ToArray());
        if (seen.TryGetValue(key, out var at) && nowMs - at < DedupeMs)
            return false;
        if (nowMs - windowStart >= WindowMs)
        {
            windowStart = nowMs;
            inWindow = 0;
        }

        if (inWindow >= MaxPerWindow)
            return false;
        inWindow++;
        seen[key] = nowMs;
        if (seen.Count > 256)
            foreach (var old in seen.Where(p => nowMs - p.Value >= DedupeMs).Select(p => p.Key).ToList())
                seen.Remove(old);
        return true;
    }
}

/// <summary>
/// Last light gains as name-free one-liners for the Debug tab, plus a local text file (numbers only, one line per gain)
/// in the plugin's own config folder so a session can be read afterwards without any server.
/// </summary>
internal sealed class LightGainLog(string? filePath = null)
{
    private readonly object gate = new();
    private readonly List<string> lines = [];
    public const int Keep = 5;
    private readonly List<LightRow> rows = [];
    private readonly string? csvPath = filePath == null ? null : Path.ChangeExtension(filePath, ".csv");

    public static readonly string[] TypeNames =
        ["?", "feeble", "gentle", "bright", "brilliant", "blinding", "newborn star", "exploding star", "kettle full", "forge converted"];

    /// <summary>The table rows for the Debug tab, newest first.</summary>
    public LightRow[] Table
    {
        get
        {
            lock (gate)
                return rows.ToArray();
        }
    }

    /// <summary>Adds a finished gain: the text line, the table row and one CSV line (enemy names only, never player names).</summary>
    public void Add(string line, LightRow row)
    {
        lock (gate)
        {
            rows.Insert(0, row);
            if (rows.Count > 8)
                rows.RemoveAt(rows.Count - 1);
            if (csvPath != null)
                try
                {
                    var header = !File.Exists(csvPath);
                    File.AppendAllText(csvPath, (header ? LightRow.CsvHeader + Environment.NewLine : "") + row.Csv() + Environment.NewLine);
                }
                catch (Exception)
                {
                    // The local file is a convenience; never let it break recording.
                }
        }

        Add(line);
    }

    public string Source { get; set; } = "value source: not started";

    public void Add(string line)
    {
        lock (gate)
        {
            lines.Insert(0, line);
            if (lines.Count > Keep)
                lines.RemoveAt(lines.Count - 1);
            if (filePath == null)
                return;
            try
            {
                File.AppendAllText(filePath, DateTimeOffset.UtcNow.ToString("o") + " " + line + Environment.NewLine);
            }
            catch (Exception)
            {
                // The local file is a convenience; never let it break recording.
            }
        }
    }

    public string[] Recent
    {
        get
        {
            lock (gate)
                return lines.ToArray();
        }
    }

    public static string Line(Observation o)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        static string Num<T>(T? v) where T : struct => v == null ? "-" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "-";
        var delta = KettleLightChain.Delta(
            o.GainBarBefore is { } bb ? new KettleReading(o.GainYieldBefore ?? 0, bb) : null,
            o.GainBarAfter is { } ba ? new KettleReading(o.GainYieldAfter ?? 0, ba) : null, KettleLightChain.BarCap);
        var buffs = o.Situation is { } s ? string.Join(",", s.Statuses.Select(x => x[0])) : "-";
        long? exp = o.GainExperienceBefore is { } e0 && o.GainExperienceAfter is { } e1 ? (long)e1 - e0 : null;
        return string.Format(inv,
            "light row {0} type {1} amount {2} (bar {3}>{4} yield {5}>{6}) kill {7} gap {8} ms mut {9} adp {10} chain {11} buffs {12} exp {13} group {14}",
            o.NoticeRow, Num(o.GainType), Num(delta), Num(o.GainBarBefore), Num(o.GainBarAfter), Num(o.GainYieldBefore),
            Num(o.GainYieldAfter), o.GameId == null ? "none" : o.GameId.ToString(), Num(o.GainKillGapMs),
            o.IsMutated == true ? 1 : 0, o.IsAdapted == true ? 1 : 0, Num(o.ChainNumber), buffs, Num(exp),
            o.Situation?.GroupSize ?? 0);
    }
}

/// <summary>One light gain as the Debug table and light-gains.csv show it. Enemy name only; no player names.</summary>
internal sealed record LightRow(
    DateTimeOffset At, uint? Chain, long? ChainGapMs, byte Type, int? Amount, bool Wrapped, string Enemy, bool Mutated, bool Adapted,
    uint? BnpcId, byte? EnemyLevel, int Party, string Buffs, string StatusIds, int? BarBefore, int? BarAfter, int? YieldBefore,
    int? YieldAfter, uint? ExpBefore, uint? ExpAfter, uint? ExpNeeded, uint? ElementalLevel, long? KillGapMs, uint Row)
{
    public const string CsvHeader =
        "time_utc,chain,chain_gap_ms,light,amount,wrapped,enemy,mutated,adapted,bnpc_id,enemy_level,party_size,exp_buffs,bar_before,bar_after,yield_before,yield_after,status_ids,exp_before,exp_after,exp_needed,elemental_level,kill_gap_ms,notice_row";

    public string LightName => Type < LightGainLog.TypeNames.Length ? LightGainLog.TypeNames[Type] : "?";
    public string AmountText => Amount is { } a ? a.ToString(System.Globalization.CultureInfo.InvariantCulture) : Wrapped ? "wrap?" : "-";
    public string EnemyText => Enemy == "" ? "none" : Enemy + (Mutated ? " (M)" : "") + (Adapted ? " (A)" : "");

    private static string N<T>(T? v) where T : struct => v == null ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static string Q(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public string Csv() => string.Join(",", At.ToString("o"), N(Chain), N(ChainGapMs), LightName, N(Amount), Wrapped ? 1 : 0, Q(Enemy),
        Mutated ? 1 : 0, Adapted ? 1 : 0, N(BnpcId), N(EnemyLevel), Party, Q(Buffs), N(BarBefore), N(BarAfter), N(YieldBefore),
        N(YieldAfter), Q(StatusIds), N(ExpBefore), N(ExpAfter), N(ExpNeeded), N(ElementalLevel), N(KillGapMs), Row);
}
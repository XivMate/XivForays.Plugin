using System;
using System.Collections.Generic;
using System.Linq;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>One item row of the FATE reward window: game item id, amount and the window's own flag byte.</summary>
internal readonly record struct RewardItem(uint ItemId, uint Amount, byte Flags)
{
    /// <summary>
    /// The window's left-hand currency entries (tomestones and the like) carry flag bit 64 in the item rows; both live
    /// windows seen so far had item 28 (Allagan tomestone of poetics) with flags 64. The game pays them straight into
    /// the currency with no "You obtain" line.
    /// </summary>
    public bool IsCurrency => (Flags & 64) != 0;
}

/// <summary>Why a listed reward was not received (kind 24 notReceived, third number).</summary>
internal static class NotReceivedReason
{
    /// <summary>"You cannot carry any more [item]." for that item.</summary>
    public const int Capped = 1;

    /// <summary>An inventory-full notice came in the reward period.</summary>
    public const int InventoryFull = 2;

    /// <summary>An "already possess one of that unique item" notice came in the reward period.</summary>
    public const int Unique = 3;

    /// <summary>Nothing in the notices explains it (a missed notice, or an unknown cause).</summary>
    public const int Unknown = 4;
}

/// <summary>
/// The FATE reward window as the game holds it (the agent's reward entry), copied out as plain numbers. Never holds
/// the FATE name or any player data. Built by <c>Services.FateRewardReader</c>; pure so it can be tested.
/// </summary>
internal sealed record RewardWindowRead(
    uint FateId,
    bool Success,
    bool EurekaFate,
    uint MedalRaw,
    uint Experience,
    byte ExperienceFlags,
    uint Currency,
    RewardItem[] Items,
    RewardItem[] ExtraItems)
{
    /// <summary>
    /// Participation rank from the medal value: 1 gold, 2 silver, 3 bronze, 4 critical engagement (the game's
    /// FateRewardMedal order 0 to 3, plus one). Null for any other value (it may then be an icon id; the raw value is
    /// still recorded).
    /// </summary>
    public byte? Rank => MedalRaw <= 3 ? (byte)(MedalRaw + 1) : null;

    /// <summary>Equal for two reads of the same window, so a repeated read is not a second reward.</summary>
    public string Signature =>
        $"{FateId}/{MedalRaw}/{Experience}/{Currency}/{Success}/" +
        string.Join(",", Items.Select(i => $"{i.ItemId}x{i.Amount}")) + "/" +
        string.Join(",", ExtraItems.Select(i => $"{i.ItemId}x{i.Amount}"));

    /// <summary>
    /// Builds the read from the entry's raw fields. Empty item slots (item id 0) are dropped. The game keeps five item
    /// slots and three additional slots; the fields are copied as they are.
    /// </summary>
    public static RewardWindowRead FromFields(uint fateId, bool success, byte eurekaFate, uint medal, uint experience,
        byte experienceFlags, uint currency, IEnumerable<RewardItem> items, IEnumerable<RewardItem> additional) =>
        new(fateId, success, eurekaFate != 0, medal, experience, experienceFlags, currency,
            items.Where(i => i.ItemId != 0).ToArray(), additional.Where(i => i.ItemId != 0).ToArray());

    /// <summary>Plausibility gate for a read of game memory: a real FATE entry has a FATE id and a short item list.</summary>
    public bool IsPlausible => FateId != 0 && Items.Length <= 5 && ExtraItems.Length <= 3;
}

/// <summary>
/// Builds one FATE reward record (kind 24) per completion. Sources, in order of strength:
/// the reward window (FATE id, rank, listed items), the FATE table (FATE level, end time, outcome) and the loot
/// notices (every item actually received). The notices carry no FATE id, so they are matched by time (3 s before to
/// 10 s after the FATE's first sign) and, when several FATEs end together, by the window's item list.
/// Pure (no Dalamud types).
/// </summary>
internal sealed class FateRewardTracker
{
    /// <summary>A loot notice this long before the first sign of the FATE still belongs to it.</summary>
    public const long PreMs = 3000;

    /// <summary>A loot notice this long after the first sign of the FATE still belongs to it.</summary>
    public const long PostMs = 10000;

    /// <summary>The record is sent this long after the reward period ends.</summary>
    public const long GraceMs = 500;

    /// <summary>Unclaimed loot notices are forgotten after this.</summary>
    public const long KeepLootMs = 40000;

    private const int MaxPending = 8;

    private sealed class Pending(uint fateId, Observation context, long anchorMs)
    {
        public uint FateId { get; } = fateId;
        public Observation Context { get; set; } = context;
        public long AnchorMs { get; set; } = anchorMs;
        public long? EndMs { get; set; }
        public long? WindowMs { get; set; }
        public Observation? End { get; set; }
        public bool? InsideRadius { get; set; }
        public byte? FateLevel { get; set; }
        public RewardWindowRead? Window { get; set; }
        public byte? Job { get; set; }
        public byte? JobLevel { get; set; }
        public byte Group { get; set; }
        public List<(uint ItemId, uint Quantity)> Received { get; } = new();
        public int InventoryFull { get; set; }
    }

    private sealed class LootNotice(Guid visit, uint itemId, uint quantity, long atMs, uint? fateId)
    {
        public Guid Visit { get; } = visit;
        public uint ItemId { get; } = itemId;
        public uint Quantity { get; } = quantity;
        public long AtMs { get; } = atMs;
        public uint? FateId { get; } = fateId;
        public bool Claimed { get; set; }
    }

    private readonly Dictionary<(Guid Visit, uint FateId), Pending> pending = new();
    private readonly List<LootNotice> loot = new();
    private readonly List<(Guid Visit, long AtMs)> fullNotices = new();
    private readonly List<(Guid Visit, uint ItemId, long AtMs)> cappedNotices = new();
    private readonly List<(Guid Visit, long AtMs)> uniqueNotices = new();

    public int PendingCount => pending.Count;

    /// <summary>
    /// True only while a FATE the player stood inside has just ended and its window has not been read yet (inside the
    /// reward period). The reward window is read by polling only then; otherwise only the game's own addon events read it.
    /// </summary>
    public bool WantsWindowRead(long nowMs) =>
        pending.Values.Any(p => p.End != null && p.InsideRadius == true && p.Window == null && nowMs < Due(p));

    /// <summary>A loot record that can be a FATE reward item: it follows no kill. (A drop linked to a kill is a monster drop.)</summary>
    public static bool IsRewardLoot(Observation record) =>
        record.Kind == ObservationKind.Loot && record.DefeatTrackId == null;

    /// <summary>
    /// A FATE reached an end state (kind 7 observation). <paramref name="insideRadius"/>: the player stood inside the
    /// FATE radius. Player fields are the player's own job, level and party size at this moment.
    /// </summary>
    public void NoteEnd(Observation end, byte? fateLevel, bool insideRadius, byte? job, byte? jobLevel, byte group)
    {
        if (end.GameId is not { } id)
            return;
        var p = Get(end.VisitId, id, end, end.ObservedAtMs);
        p.End = end;
        p.EndMs = end.ObservedAtMs;
        p.InsideRadius = insideRadius;
        p.FateLevel ??= fateLevel;
        Player(p, job, jobLevel, group);
        // The end is the context of record (levels at the end); the window context may have been taken a moment later.
        p.Context = end;
        Reanchor(p);
    }

    /// <summary>The reward window was read. <paramref name="context"/> is the shared context at the read.</summary>
    public void NoteWindow(Observation context, RewardWindowRead window, byte? fateLevel, byte? job, byte? jobLevel,
        byte group)
    {
        if (!window.IsPlausible)
            return;
        var p = Get(context.VisitId, window.FateId, context, context.ObservedAtMs);
        if (p.Window != null && p.Window.Signature == window.Signature)
            return;
        p.Window = window;
        p.WindowMs = context.ObservedAtMs;
        p.FateLevel ??= fateLevel;
        Player(p, job, jobLevel, group);
        Reanchor(p);
    }

    /// <summary>
    /// A loot notice (kind 9 observation). Loot linked to a kill is not a FATE reward and must not be passed.
    /// <see cref="Observation.FateRewardId"/> (a FATE in its 5 s window) is used as a strong hint.
    /// </summary>
    public void NoteLoot(Observation lootNotice)
    {
        if (lootNotice.GameId is not { } item || item == 0)
            return;
        loot.Add(new LootNotice(lootNotice.VisitId, item, Math.Max(1u, lootNotice.Quantity ?? 1u),
            lootNotice.ObservedAtMs, lootNotice.FateRewardId));
    }

    /// <summary>An "inventory full" notice.</summary>
    public void NoteInventoryFull(Guid visit, long atMs) => fullNotices.Add((visit, atMs));

    /// <summary>"You cannot carry any more [item]." (the item is at its cap).</summary>
    public void NoteCapped(Guid visit, uint itemId, long atMs) => cappedNotices.Add((visit, itemId, atMs));

    /// <summary>"Unable to obtain ... You already possess one of that unique item."</summary>
    public void NoteUnique(Guid visit, long atMs) => uniqueNotices.Add((visit, atMs));

    /// <summary>Finishes every record whose reward period is over.</summary>
    public void Tick(long nowMs, Action<Observation> emit)
    {
        Assign();
        var overlaps = Overlaps();
        foreach (var key in pending.Where(kv => nowMs >= Due(kv.Value)).OrderBy(kv => kv.Value.AnchorMs)
                     .Select(kv => kv.Key).ToList())
            Finish(key, overlaps[key], emit);
        loot.RemoveAll(l => nowMs - l.AtMs > KeepLootMs);
        fullNotices.RemoveAll(f => nowMs - f.AtMs > KeepLootMs);
        cappedNotices.RemoveAll(f => nowMs - f.AtMs > KeepLootMs);
        uniqueNotices.RemoveAll(f => nowMs - f.AtMs > KeepLootMs);
    }

    /// <summary>Left the zone or instance: every record of another visit is finished now, with what it has.</summary>
    public void FlushOtherVisits(Guid currentVisit, Action<Observation> emit)
    {
        Assign();
        var overlaps = Overlaps();
        foreach (var key in pending.Keys.Where(k => k.Visit != currentVisit).ToList())
            Finish(key, overlaps[key], emit);
        loot.RemoveAll(l => l.Visit != currentVisit);
        fullNotices.RemoveAll(f => f.Visit != currentVisit);
        cappedNotices.RemoveAll(f => f.Visit != currentVisit);
        uniqueNotices.RemoveAll(f => f.Visit != currentVisit);
    }

    public void Clear()
    {
        pending.Clear();
        loot.Clear();
        fullNotices.Clear();
        cappedNotices.Clear();
        uniqueNotices.Clear();
    }

    private Pending Get(Guid visit, uint fateId, Observation context, long anchorMs)
    {
        if (pending.TryGetValue((visit, fateId), out var existing))
            return existing;
        if (pending.Count >= MaxPending)
        {
            var oldest = pending.OrderBy(kv => kv.Value.AnchorMs).First().Key;
            pending.Remove(oldest);
        }

        var created = new Pending(fateId, context, anchorMs);
        pending[(visit, fateId)] = created;
        return created;
    }

    private static void Player(Pending p, byte? job, byte? jobLevel, byte group)
    {
        p.Job ??= job;
        p.JobLevel ??= jobLevel;
        if (group > p.Group)
            p.Group = group;
    }

    // The anchor is the first sign of the FATE finishing (the end or the window, whichever came first).
    private static void Reanchor(Pending p) =>
        p.AnchorMs = Math.Min(p.EndMs ?? long.MaxValue, p.WindowMs ?? long.MaxValue);

    private static long Due(Pending p) => p.AnchorMs + PostMs + GraceMs;

    /// <summary>
    /// Gives each unclaimed loot notice to one pending completion. Alone in its period: that one. A FATE id hint wins.
    /// Several together: the one whose window lists the item, then the nearest first sign.
    /// </summary>
    private void Assign()
    {
        foreach (var notice in loot.Where(l => !l.Claimed).OrderBy(l => l.AtMs))
        {
            var candidates = pending.Values.Where(p => p.Context.VisitId == notice.Visit &&
                                                       notice.AtMs >= p.AnchorMs - PreMs &&
                                                       notice.AtMs <= p.AnchorMs + PostMs).ToList();
            if (candidates.Count == 0)
                continue;
            Pending? owner = null;
            if (notice.FateId is { } hinted)
                owner = candidates.FirstOrDefault(p => p.FateId == hinted);
            if (owner == null)
            {
                var byWindow = candidates.Where(p => p.Window != null &&
                                                     (p.Window.Items.Any(i => i.ItemId == notice.ItemId) ||
                                                      p.Window.ExtraItems.Any(i => i.ItemId == notice.ItemId))).ToList();
                var pool = byWindow.Count > 0 ? byWindow : candidates;
                owner = pool.OrderBy(p => Math.Abs(notice.AtMs - p.AnchorMs)).First();
            }

            owner.Received.Add((notice.ItemId, notice.Quantity));
            notice.Claimed = true;
        }
    }

    /// <summary>For each pending completion, how many others of its visit have a reward period overlapping its own.</summary>
    private Dictionary<(Guid Visit, uint FateId), int> Overlaps() =>
        pending.ToDictionary(kv => kv.Key, kv => pending.Values.Count(o => o != kv.Value &&
                                                                        o.Context.VisitId == kv.Key.Visit &&
                                                                        Math.Abs(o.AnchorMs - kv.Value.AnchorMs) <= PostMs + PreMs));

    private void Finish((Guid Visit, uint FateId) key, int overlap, Action<Observation> emit)
    {
        if (!pending.TryGetValue(key, out var p))
            return;
        var fullCount = fullNotices.Count(f => f.Visit == key.Visit && f.AtMs >= p.AnchorMs - PreMs &&
                                              f.AtMs <= p.AnchorMs + PostMs);
        var capped = cappedNotices.Where(c => c.Visit == key.Visit && c.AtMs >= p.AnchorMs - PreMs &&
                                              c.AtMs <= p.AnchorMs + PostMs).Select(c => c.ItemId).ToHashSet();
        var uniqueCount = uniqueNotices.Count(u => u.Visit == key.Visit && u.AtMs >= p.AnchorMs - PreMs &&
                                                   u.AtMs <= p.AnchorMs + PostMs);
        pending.Remove(key);
        if (Build(p, fullCount, overlap, capped, uniqueCount) is { } record)
            emit(record);
    }

    /// <summary>Which kind of zone the FATE was in: 1 Eureka, 2 Bozja or Zadnor, 3 Occult Crescent, 0 anything else.</summary>
    public static byte ZoneFlag(uint territory) =>
        ForayZones.IsEureka(territory) ? (byte)1 : ForayZones.IsBozja(territory) ? (byte)2
        : ForayZones.IsOccultCrescent(territory) ? (byte)3 : (byte)0;

    /// <summary>
    /// The record, or null when this FATE gave the player nothing to report (not their FATE, or a failed one): needs
    /// the window with a success or a listed item, or a received item, or a successful end with the player inside the
    /// radius.
    /// </summary>
    private static Observation? Build(Pending p, int fullCount, int overlap, IReadOnlySet<uint> capped, int uniqueCount)
    {
        var window = p.Window;
        var success = window?.Success ?? p.End?.Outcome == FateOutcome.Success;
        var received = p.Received.GroupBy(r => r.ItemId)
            .Select(g => new[] { (int)g.Key, (int)Math.Min(int.MaxValue, g.Sum(r => (long)r.Quantity)), g.Count() })
            .ToArray();
        var listed = window == null ? 0 : window.Items.Length + window.ExtraItems.Length;
        var credited = window != null && (window.Success || listed > 0) || received.Length > 0 ||
                       p.End?.Outcome == FateOutcome.Success && p.InsideRadius == true;
        if (!credited)
            return null;

        int[][]? notReceived = null;
        int[][]? currencyPaid = null;
        int[][]? notInWindow = null;
        if (window != null)
        {
            // Items the notices gave that the window does not list at all (a longer or different window than the rows read).
            var listedIds = window.Items.Select(i => i.ItemId).Concat(window.ExtraItems.Select(i => i.ItemId)).ToHashSet();
            notInWindow = p.Received.Where(r => !listedIds.Contains(r.ItemId)).GroupBy(r => r.ItemId)
                .Select(g => new[] { (int)g.Key, (int)Math.Min(int.MaxValue, g.Sum(r => (long)r.Quantity)) }).ToArray();
            // Currency rows (the window's left side, flag 64) are paid straight into the currency: received unless
            // the "cannot carry any more" notice names that currency. Item rows are received only by a notice.
            var currency = window.Items.Where(i => i.IsCurrency).GroupBy(i => i.ItemId)
                .ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Amount));
            var wanted = window.Items.Where(i => !i.IsCurrency)
                .Concat(window.ExtraItems.Select(e => new RewardItem(e.ItemId, e.Amount, 0)))
                .GroupBy(i => i.ItemId).ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Amount));
            var got = p.Received.GroupBy(r => r.ItemId).ToDictionary(g => g.Key, g => g.Sum(r => (long)r.Quantity));
            int Reason(uint item) => capped.Contains(item) ? NotReceivedReason.Capped
                : fullCount > 0 ? NotReceivedReason.InventoryFull
                : uniqueCount > 0 ? NotReceivedReason.Unique
                : NotReceivedReason.Unknown;
            var missing = wanted.Where(w => w.Value > got.GetValueOrDefault(w.Key))
                .Select(w => new[]
                {
                    (int)w.Key, (int)Math.Min(int.MaxValue, w.Value - got.GetValueOrDefault(w.Key)), Reason(w.Key)
                }).ToList();
            missing.AddRange(currency.Where(c => capped.Contains(c.Key))
                .Select(c => new[] { (int)c.Key, (int)Math.Min(int.MaxValue, c.Value), NotReceivedReason.Capped }));
            notReceived = missing.ToArray();
            currencyPaid = currency.Where(c => !capped.Contains(c.Key))
                .Select(c => new[] { (int)c.Key, (int)Math.Min(int.MaxValue, c.Value) }).ToArray();
        }

        var end = p.End;
        var firstMs = Math.Min(p.EndMs ?? long.MaxValue, p.WindowMs ?? long.MaxValue);
        return p.Context with
        {
            Kind = ObservationKind.FateReward,
            ObservedAtMs = firstMs == long.MaxValue ? p.Context.ObservedAtMs : firstMs,
            GameId = p.FateId,
            FateLevel = p.FateLevel,
            PlayerClassJobId = p.Job,
            PlayerJobLevel = p.JobLevel,
            GroupSize = p.Group == 0 ? null : p.Group,
            Outcome = window != null ? window.Success ? FateOutcome.Success : FateOutcome.Failed
                : end?.Outcome,
            OutcomeInferred = window != null ? false : end?.OutcomeInferred,
            StartedAt = end?.StartedAt,
            X = end?.X,
            Y = end?.Y,
            Z = end?.Z,
            Radius = end?.Radius,
            RewardEndSeen = end != null,
            InsideFateRadius = p.InsideRadius,
            RewardWindowSeen = window != null,
            WindowAfterEndMs = p.WindowMs is { } w && p.EndMs is { } e ? (int)Math.Clamp(w - e, int.MinValue, int.MaxValue) : null,
            RewardRank = window?.Rank,
            RewardMedalRaw = window?.MedalRaw,
            RewardExperience = window?.Experience,
            RewardExperienceFlags = window?.ExperienceFlags,
            RewardCurrency = window?.Currency,
            RewardItems = window?.Items.Select(i => new[] { (int)i.ItemId, (int)i.Amount, (int)i.Flags }).ToArray(),
            RewardExtraItems = window?.ExtraItems.Select(i => new[] { (int)i.ItemId, (int)i.Amount }).ToArray(),
            ReceivedItems = received,
            NotReceived = notReceived,
            CurrencyPaid = currencyPaid,
            ReceivedNotInWindow = notInWindow,
            RewardZone = ZoneFlag(p.Context.TerritoryId),
            InventoryFullNotices = (byte)Math.Min(fullCount, byte.MaxValue),
            OverlapCount = (byte)Math.Min(overlap, byte.MaxValue)
        };
    }
}

/// <summary>
/// "Inventory full" LogMessage rows (English text checked in the game sheets: 460, 1288, 1289, 4406 and 4658 "Unable
/// to obtain ... Your inventory is full", 1872 and 10823 "Your inventory is full", 7970 "Unable to accept reward.
/// Inventory full", 11596 "... due to a full inventory"). Row ids only: the text is never read.
/// </summary>
internal static class InventoryFullRows
{
    public static readonly IReadOnlySet<uint> Rows = new HashSet<uint> { 460, 1288, 1289, 1872, 4406, 4658, 7970, 10823, 11596 };

    public static bool IsFull(uint row) => Rows.Contains(row);
}

/// <summary>Local, numbers-only text file (plugin config folder) with one line per FATE reward read and per record.</summary>
internal sealed class FateRewardLog(string? filePath)
{
    private readonly object gate = new();

    public void Add(string line)
    {
        if (filePath == null)
            return;
        lock (gate)
        {
            try
            {
                System.IO.File.AppendAllText(filePath, DateTimeOffset.UtcNow.ToString("o") + " " + line + Environment.NewLine);
            }
            catch (Exception)
            {
                // The local file is a convenience; never let it break recording.
            }
        }
    }

    public static string Line(RewardWindowRead w) =>
        $"window fate={w.FateId} success={w.Success} eureka={w.EurekaFate} medalRaw={w.MedalRaw} rank={w.Rank?.ToString() ?? "?"} exp={w.Experience}/{w.ExperienceFlags} " +
        $"currency={w.Currency} items=[{string.Join(" ", w.Items.Select(i => $"{i.ItemId}x{i.Amount}f{i.Flags}"))}] " +
        $"extra=[{string.Join(" ", w.ExtraItems.Select(i => $"{i.ItemId}x{i.Amount}"))}]";

    public static string Line(Observation r) =>
        $"record fate={r.GameId} level={r.FateLevel?.ToString() ?? "?"} elemental={r.PlayerElementalLevel?.ToString() ?? "?"}/{r.PlayerEffectiveElementalLevel?.ToString() ?? "?"} " +
        $"job={r.PlayerClassJobId?.ToString() ?? "?"} group={r.GroupSize?.ToString() ?? "?"} outcome={r.Outcome} rank={r.RewardRank?.ToString() ?? "?"} window={r.RewardWindowSeen} end={r.RewardEndSeen} " +
        $"inside={r.InsideFateRadius} received=[{string.Join(" ", (r.ReceivedItems ?? []).Select(i => $"{i[0]}x{i[1]}"))}] " +
        $"currencyPaid=[{string.Join(" ", (r.CurrencyPaid ?? []).Select(i => $"{i[0]}x{i[1]}"))}] " +
        $"notReceived=[{string.Join(" ", (r.NotReceived ?? []).Select(i => $"{i[0]}x{i[1]}r{(i.Length > 2 ? i[2] : 0)}"))}] full={r.InventoryFullNotices} overlap={r.OverlapCount}";
}

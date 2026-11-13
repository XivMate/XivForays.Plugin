using System;
using System.Collections.Generic;
using System.Numerics;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>A live enemy as the overlay knows it (values only, never a game object).</summary>
internal sealed class OverlayEnemy
{
    public Guid TrackId { get; init; }
    public uint BnpcId { get; init; }
    public uint LayoutId { get; set; }
    public ushort FateId { get; set; }

    /// <summary>The spawn anchor used for matching: home position, else where it was first seen.</summary>
    public Vector3 Anchor { get; set; }

    public bool AnchorIsHome { get; set; }
    public Vector3 Position { get; set; }
    public bool InCombat { get; set; }
    public PointMatch Match { get; set; }
    public int MatchedVersion { get; set; } = -1;
    public long SeenScan { get; set; }
}

/// <summary>A death the overlay counts down for (feature 2).</summary>
internal sealed class OverlayDeath
{
    public Guid TrackId { get; init; }
    public uint BnpcId { get; init; }
    public uint LayoutId { get; init; }
    public Vector3 Anchor { get; init; }

    /// <summary>The anchor came from the live position only (no home): the timer location is approximate.</summary>
    public bool AnchorApproximate { get; init; }

    public string? PointKey { get; init; }
    public long DiedAtMs { get; init; }
    public bool FateBound { get; init; }

    /// <summary>0 = observed death (killer unknown), 1 = your kill, 2 = a party member's kill (kind 13 defeat links).</summary>
    public byte DefeatBy { get; set; }

    public float MaxPlayerDistance { get; set; }
}

/// <summary>A local, server-equivalent confirmation (feature 4), kept until the coverage row says Confirmed.</summary>
internal sealed class OverlayConfirmation
{
    public required string Key { get; init; }
    public Vector3 Anchor { get; init; }
    public uint BnpcId { get; init; }
    public uint LayoutId { get; init; }
    public long AtMs { get; init; }

    /// <summary>The appearance had no layout id: "Spawn watched locally; server match uncertain".</summary>
    public bool NoLayout { get; init; }

    /// <summary>No coverage row matched: a new local point at the clean anchor.</summary>
    public bool Provisional { get; init; }
}

/// <summary>
/// The overlay's in-memory state of one visit: live enemies bound to coverage rows, deaths with their countdowns and
/// local confirmations. Fed from the existing 500 ms enemy scan (framework thread); reads only. Cleared on visit
/// change, zone change, recording stop and when the overlay is turned off. Never restored from an earlier instance.
/// </summary>
internal sealed class CollectorSession
{
    public const int MaxDeaths = 64;
    public const int MaxConfirmations = 256;

    private readonly Dictionary<Guid, OverlayEnemy> live = new();
    private readonly List<Guid> gone = new();
    private readonly HashSet<string> claimed = new(StringComparer.Ordinal);
    private readonly List<OverlayDeath> deaths = new();
    private readonly Dictionary<string, OverlayConfirmation> confirmations = new(StringComparer.Ordinal);
    private readonly List<string> confirmedDrop = new();
    // A defeat notice can name a track before the scan saw it at HP 0.
    private readonly Dictionary<Guid, byte> pendingDefeats = new();
    private CoverageSnapshot? snapshot;
    private int snapshotVersion;
    private long scan;

    public WatchMeter Meter { get; } = new();

    /// <summary>Pack watch: own and party kills of matched spawn points waiting for their respawn.</summary>
    public PackWatch Pack { get; } = new();

    /// <summary>Pack watch on: kills are added to <see cref="Pack"/>.</summary>
    public bool PackEnabled { get; set; }

    /// <summary>The keep-within radius of the pack watch (yalms).</summary>
    public float PackRadius { get; set; } = PackWatch.DefaultRadius;

    /// <summary>Feature 4 on: local confirmations override the downloaded status. Off: the public status only.</summary>
    public bool LocalOverrides { get; set; } = true;
    public Vector3 Player { get; private set; }
    public bool HasPlayer { get; private set; }
    public long NowMs { get; private set; }

    /// <summary>The scan feeding this session ran during the last 2 s (recording is active).</summary>
    public bool ScanActive(long nowMs) => scan > 0 && nowMs - NowMs < 2000;

    public IReadOnlyDictionary<Guid, OverlayEnemy> Live => live;

    /// <summary>The live enemies for drawing (a struct enumerator: no allocation per frame).</summary>
    public Dictionary<Guid, OverlayEnemy>.ValueCollection LiveValues => live.Values;
    public IReadOnlyList<OverlayDeath> Deaths => deaths;
    public IReadOnlyDictionary<string, OverlayConfirmation> Confirmations => confirmations;
    public CoverageSnapshot? Snapshot => snapshot;

    /// <summary>The newest local confirmation, for the 3 s "Confirmed locally" flash.</summary>
    public OverlayConfirmation? LastConfirmation { get; private set; }

    public void Clear()
    {
        live.Clear();
        claimed.Clear();
        deaths.Clear();
        confirmations.Clear();
        pendingDefeats.Clear();
        Pack.Clear();
        LastConfirmation = null;
        Meter.Clear();
        HasPlayer = false;
        scan = 0;
    }

    /// <summary>A new or refreshed coverage file: rebind the live enemies, drop local overrides the server now agrees with.</summary>
    public void SetSnapshot(CoverageSnapshot? next)
    {
        if (ReferenceEquals(next, snapshot))
            return;
        snapshot = next;
        snapshotVersion++;
        claimed.Clear();
        foreach (var enemy in live.Values)
            enemy.MatchedVersion = -1;
        if (next == null)
            return;
        confirmedDrop.Clear();
        foreach (var (key, confirmation) in confirmations)
            if (ServerConfirms(next, confirmation))
                confirmedDrop.Add(key);
        foreach (var key in confirmedDrop)
            confirmations.Remove(key);
    }

    public void BeginScan(long nowMs, Vector3? player)
    {
        NowMs = nowMs;
        scan++;
        if (player is not { } position)
            return;
        Player = position;
        HasPlayer = true;
        Meter.Add(nowMs, position);
        foreach (var death in deaths)
            death.MaxPlayerDistance = MathF.Max(death.MaxPlayerDistance, Vector3.Distance(death.Anchor, position));
        deaths.RemoveAll(d => nowMs - d.DiedAtMs > OverlayRates.DeathKeepMs);
    }

    /// <summary>A live enemy this scan. The match is made once per track (and again only when the file changes).</summary>
    public void Seen(Guid trackId, uint bnpcId, uint layoutId, Vector3? home, Vector3 firstPosition, Vector3 position,
        bool inCombat, ushort fateId)
    {
        if (!live.TryGetValue(trackId, out var enemy))
        {
            enemy = new OverlayEnemy { TrackId = trackId, BnpcId = bnpcId };
            live[trackId] = enemy;
        }

        enemy.LayoutId = layoutId;
        enemy.FateId = fateId;
        if (enemy.MatchedVersion < 0 || !enemy.AnchorIsHome && home != null)
        {
            enemy.Anchor = home ?? firstPosition;
            enemy.AnchorIsHome = home != null;
        }

        enemy.Position = position;
        enemy.InCombat = inCombat;
        enemy.SeenScan = scan;
        if (enemy.MatchedVersion != snapshotVersion)
            Bind(enemy);
        // It is up again: any countdown of the same point ends.
        if (enemy.Match.Point is { } point)
        {
            if (deaths.Count > 0)
                deaths.RemoveAll(d => d.PointKey == point.Key && d.TrackId != trackId);
            if (Pack.Points.Count > 0)
                Pack.Respawned(point.Key, NowMs, HasPlayer ? Vector3.Distance(Player, enemy.Anchor) : float.MaxValue, PackRadius);
        }
    }

    /// <summary>An observed live to HP 0 transition. The timer anchors at the spawn anchor, not the corpse.</summary>
    public void Died(Guid trackId, uint bnpcId, uint layoutId, Vector3? home, Vector3 firstPosition, ushort fateId)
    {
        live.TryGetValue(trackId, out var enemy);
        var anchor = enemy?.Anchor ?? home ?? firstPosition;
        var approximate = enemy is { AnchorIsHome: false } || enemy == null && home == null;
        var key = enemy?.Match.Point?.Key;
        if (enemy != null && enemy.Match.Point != null)
            claimed.Remove(enemy.Match.Point.Key);
        live.Remove(trackId);
        deaths.RemoveAll(d => d.TrackId == trackId);
        if (deaths.Count >= MaxDeaths)
            deaths.RemoveAt(0);
        var death = new OverlayDeath
        {
            TrackId = trackId,
            BnpcId = bnpcId,
            LayoutId = layoutId,
            Anchor = anchor,
            AnchorApproximate = approximate,
            PointKey = key,
            DiedAtMs = NowMs,
            FateBound = fateId != 0,
            MaxPlayerDistance = HasPlayer ? Vector3.Distance(anchor, Player) : 0,
            DefeatBy = pendingDefeats.Remove(trackId, out var by) ? by : (byte)0
        };
        deaths.Add(death);
        AddToPack(death);
    }

    /// <summary>An own or party kill of a matched, non-FATE spawn point joins the pack watch.</summary>
    private void AddToPack(OverlayDeath death)
    {
        if (!PackEnabled || death.DefeatBy == 0 || death.FateBound || death.PointKey == null || snapshot == null ||
            !snapshot.ByKey.TryGetValue(death.PointKey, out var point))
            return;
        Pack.Killed(new PackPoint
        {
            Key = point.Key,
            Name = point.Name,
            BnpcId = point.BnpcId,
            Anchor = point.HeightKnown ? new Vector3(point.X, point.Y, point.Z) : death.Anchor with { X = point.X, Z = point.Z },
            HeightKnown = point.HeightKnown,
            Condition = point.Condition,
            AlreadyDone = StatusOf(point, out _) == CoverageStatus.Confirmed,
            DiedAtMs = death.DiedAtMs
        });
    }

    /// <summary>A kind 13 defeat notice linked to this track: 1 = your kill, 2 = a party member's.</summary>
    public void DefeatLinked(Guid trackId, byte defeatBy)
    {
        foreach (var death in deaths)
            if (death.TrackId == trackId)
            {
                var first = death.DefeatBy == 0;
                death.DefeatBy = defeatBy;
                if (first)
                    AddToPack(death);
                return;
            }

        if (pendingDefeats.Count >= MaxDeaths)
            pendingDefeats.Clear();
        pendingDefeats[trackId] = defeatBy;
    }

    /// <summary>Deaths for the list: your kills first, then party kills, then other observed deaths; newest first.</summary>
    public int SortedDeaths(OverlayDeath[] buffer)
    {
        var n = Math.Min(buffer.Length, deaths.Count);
        var count = 0;
        foreach (var rank in DefeatOrder)
            for (var i = deaths.Count - 1; i >= 0 && count < n; i--)
                if (deaths[i].DefeatBy == rank)
                    buffer[count++] = deaths[i];
        return count;
    }

    private static readonly byte[] DefeatOrder = [1, 2, 0];

    /// <summary>
    /// An emitted kind 1 / 15 / 16 record. Only server-equivalent evidence (<see cref="LocalConfirmation"/>) confirms;
    /// a respawn watched from beyond 100 y never does.
    /// </summary>
    public OverlayConfirmation? Evidence(Observation record)
    {
        if (record.Kind == ObservationKind.EnemyAppeared && record.Respawn is { } respawn)
        {
            foreach (var d in deaths)
                if (d.TrackId == respawn.PreviousTrackId && d.PointKey != null && Pack.Points.Count > 0)
                    Pack.Respawned(d.PointKey, NowMs, HasPlayer ? Vector3.Distance(Player, d.Anchor) : float.MaxValue, PackRadius);
            deaths.RemoveAll(d => d.TrackId == respawn.PreviousTrackId);
        }
        var evidence = LocalConfirmation.Evaluate(record);
        if (evidence == LocalEvidence.None || record.EnemyTrackId is not { } trackId)
            return null;

        var home = record.Kind switch
        {
            ObservationKind.EnemySpawnPose => record.Pose!.HomePosition!,
            ObservationKind.EnemyFormChange => record.FormChange!.After.HomePosition!,
            _ => record.HomePosition!
        };
        var anchor = new Vector3(home[0], home[1], home[2]);
        live.TryGetValue(trackId, out var enemy);
        var bnpc = enemy?.BnpcId ?? record.GameId ?? 0;
        var layout = enemy?.LayoutId ?? record.LayoutId ?? 0;
        var point = enemy?.Match.Point;
        if (point == null && snapshot != null)
            point = CoverageMatcher.Match(snapshot, bnpc, layout, anchor.X, anchor.Z).Point;
        var key = point?.Key ?? "local:" + trackId.ToString("N");
        if (confirmations.TryGetValue(key, out var existing))
            return existing;
        if (point is { Status: CoverageStatus.Confirmed })
            return null; // already public: nothing new to show
        if (confirmations.Count >= MaxConfirmations)
            return null;
        var confirmation = new OverlayConfirmation
        {
            Key = key,
            Anchor = anchor,
            BnpcId = bnpc,
            LayoutId = layout,
            AtMs = NowMs,
            NoLayout = evidence == LocalEvidence.NoLayout,
            Provisional = point == null
        };
        confirmations[key] = confirmation;
        LastConfirmation = confirmation;
        return confirmation;
    }

    /// <summary>Removes live enemies the scan did not see (out of range or gone; never treated as a death).</summary>
    public void EndScan()
    {
        gone.Clear();
        foreach (var (id, enemy) in live)
            if (enemy.SeenScan != scan)
                gone.Add(id);
        foreach (var id in gone)
        {
            if (live[id].Match.Point is { } point)
                claimed.Remove(point.Key);
            live.Remove(id);
        }
    }

    /// <summary>The status the overlay shows for a row, with the local override; <paramref name="local"/> = pending upload.</summary>
    public CoverageStatus StatusOf(CoveragePoint point, out OverlayConfirmation? local)
    {
        if (LocalOverrides && point.Status != CoverageStatus.Confirmed && confirmations.TryGetValue(point.Key, out local))
            return CoverageStatus.Confirmed;
        local = null;
        return point.Status;
    }

    public bool IsOccupied(CoveragePoint point) => claimed.Contains(point.Key);

    private void Bind(OverlayEnemy enemy)
    {
        if (enemy.Match.Point is { } old)
            claimed.Remove(old.Key);
        enemy.MatchedVersion = snapshotVersion;
        if (snapshot == null)
        {
            enemy.Match = PointMatch.NoMatch();
            return;
        }

        var match = CoverageMatcher.Match(snapshot, enemy.BnpcId, enemy.LayoutId, enemy.Anchor.X, enemy.Anchor.Z, claimed);
        enemy.Match = match;
        if (match.Point != null)
            claimed.Add(match.Point.Key);
    }

    private static bool ServerConfirms(CoverageSnapshot next, OverlayConfirmation confirmation)
    {
        if (next.ByKey.TryGetValue(confirmation.Key, out var row) && row.Status == CoverageStatus.Confirmed)
            return true;
        // The row key can change when aggregation replaces an approximate row: reconcile by layout and position too.
        foreach (var candidate in next.WithBnpc(confirmation.BnpcId))
            if (candidate.Status == CoverageStatus.Confirmed &&
                (confirmation.LayoutId == 0 || candidate.LayoutId == 0 || candidate.LayoutId == confirmation.LayoutId) &&
                CoverageMatcher.DistanceXz(candidate.X, candidate.Z, confirmation.Anchor.X, confirmation.Anchor.Z) <=
                OverlayRates.MatchYalms)
                return true;
        return false;
    }
}

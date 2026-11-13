using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMate.DataGathering.Forays.Dalamud.Configuration;
using XivMate.DataGathering.Forays.Dalamud.Core;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

/// <summary>
/// The optional collector overlay (General, "Collector overlay"; everything off by default). Downloads the public
/// coverage file of the current Eureka field zone (anonymous, see <see cref="HttpCoverageFetcher"/>), follows the
/// existing 500 ms enemy scan through <see cref="CollectorSession"/>, and draws a small window plus projected labels
/// on the ImGui background draw list. Read-only and passive: no targeting, movement, input, packets, camera, VFX or
/// waymarks. Nothing is sent. With the master switch off it holds no timer, no scan hook and no download.
/// </summary>
internal sealed class CollectorOverlayService : IDisposable
{
    private const int TickMs = 500;
    private const int MaxEmpty = 32;
    private const int MaxDeathRows = 5;
    private const int RingSegments = 48;
    private const int AreaSegments = 64;
    private const int GuideSteps = 24;
    private const int SmallRingSegments = 16;
    private static readonly Vector4 Amber = new(1f, 0.82f, 0.4f, 1f);
    private static readonly Vector4 Good = new(0.45f, 0.9f, 0.6f, 1f);

    private readonly Plugin plugin;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IFateTable fateTable;
    private readonly ForayService forayService;
    private readonly EnemyTrackingService enemies;
    private readonly SchedulerService scheduler;
    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly CollectorSession session = new();
    private readonly HashSet<uint> activeFates = new();
    private readonly Dictionary<byte, string> weatherNames = new();
    private readonly Dictionary<string, ConditionState> conditionStates = new(StringComparer.Ordinal);
    private bool night;
    private readonly ListRow[] rows = new ListRow[OverlayRates.MaxListRows];
    private readonly CoveragePoint?[] nearest = new CoveragePoint?[OverlayRates.MaxListRows];
    private readonly float[] nearestDistance = new float[OverlayRates.MaxListRows];
    private readonly EmptyMarker[] empty = new EmptyMarker[MaxEmpty];
    private readonly OverlayDeath[] deathBuffer = new OverlayDeath[MaxDeathRows];
    private readonly DeathRow[] deathRows = new DeathRow[MaxDeathRows];
    private readonly Vector2[] ring = new Vector2[RingSegments + 1];
    private readonly bool[] ringOk = new bool[RingSegments + 1];
    private readonly Vector2[] area = new Vector2[AreaSegments];
    private readonly Vector2[] areaScreen = new Vector2[AreaSegments];
    private readonly bool[] areaOk = new bool[AreaSegments];
    private readonly Vector3[] packPoints = new Vector3[PackWatch.MaxPoints];
    private readonly SpawnLabels labels = new();
    private readonly MapPoint[] mapPoints = new MapPoint[MapRates.MaxMaxMarkers];
    private readonly MapPointByDistance mapOrder = new();
    private readonly Dictionary<string, string> pendingTimers = new(StringComparer.Ordinal);
    private int mapPointCount;
    private int mapPicked = -1;
    private string mapLine = "";
    private string mapHoverKey = "";
    private string mapHoverText = "";
    private long mapHoverTick;
    private bool mapWasOpen;
    private int packCount;
    private PackStatus pack;
    private float packY;
    private long unixNow;
    private long tickNow;
    private string? flashKey;
    private string pointerText = "";
    private string pointerEdgeText = "";

    private HttpCoverageFetcher? fetcher;
    private CoverageFeed? feed;
    private OverlayFeatures features;
    private bool ticking;
    private Guid visitId;
    private uint territory;
    private ZoneSupport support;
    private Vector3 player;
    private bool hasPlayer;
    private string? pinnedKey;
    private int rowCount;
    private int emptyCount;
    private int deathRowCount;
    private Target selected;
    private string statusLine = "";
    private string feedLine = "";
    private string confirmLine = "";
    private string flashLabel = "";
    private Vector3 flashAnchor;
    private long flashUntilMs;
    private byte weatherId;

    public CollectorOverlayService(Plugin plugin, IClientState clientState, IObjectTable objectTable, IGameGui gameGui,
        ICondition condition, IDataManager dataManager, IFateTable fateTable, ForayService forayService,
        EnemyTrackingService enemies, SchedulerService scheduler, IPluginLog log, IDalamudPluginInterface pluginInterface)
    {
        this.plugin = plugin;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.condition = condition;
        this.dataManager = dataManager;
        this.fateTable = fateTable;
        this.forayService = forayService;
        this.enemies = enemies;
        this.scheduler = scheduler;
        this.log = log;
        this.pluginInterface = pluginInterface;
        for (var i = 0; i < rows.Length; i++)
            rows[i] = new ListRow();
        for (var i = 0; i < deathRows.Length; i++)
            deathRows[i] = new DeathRow();
    }

    private enum ZoneSupport : byte
    {
        None,
        Supported,
        NotAvailable,
        BaldesionArsenal
    }

    /// <summary>Applies the settings: the master switch starts or stops all overlay work.</summary>
    public void ApplyConfig(Configuration.Configuration configuration)
    {
        features = configuration.OverlayFeatures;
        if (features.Enabled && !ticking)
        {
            ticking = true;
            enemies.OverlayFaulted = false;
            fetcher ??= new HttpCoverageFetcher(pluginInterface.Manifest.AssemblyVersion.ToString());
            var cache = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "coverage-cache");
            feed = new CoverageFeed(fetcher, new FileCoverageStore(cache));
            scheduler.ScheduleOnFrameworkThread(Tick, TickMs);
        }
        else if (!features.Enabled && ticking)
        {
            Stop();
        }

        session.LocalOverrides = features.LocalConfirm;
        session.PackEnabled = features.PackWatch;
        session.PackRadius = features.PackRadius;
        if (!features.PackWatch)
            session.Pack.Clear();
        enemies.Overlay = features.NeedsScan ? session : null;
    }

    private void Stop()
    {
        ticking = false;
        scheduler.CancelScheduledTask(Tick);
        enemies.Overlay = null;
        feed?.Stop();
        feed = null;
        session.Clear();
        session.SetSnapshot(null);
        pinnedKey = null;
        rowCount = emptyCount = deathRowCount = packCount = mapPointCount = 0;
        pack = default;
        selected = default;
        pendingTimers.Clear();
    }

    // ---- 500 ms tick (framework thread): download state, nearest points, prepared strings ----

    private void Tick()
    {
        try
        {
            TickCore(Environment.TickCount64);
        }
        catch (Exception ex)
        {
            log.Warning($"Collector overlay update failed ({ex.GetType().Name}).");
        }
    }

    private void TickCore(long now)
    {
        if (feed == null)
            return;
        tickNow = now;
        unixNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        territory = clientState.IsLoggedIn ? clientState.TerritoryType : 0u;
        support = Support(territory, clientState.MapId);
        // A new visit (zone change, new instance) or an unsupported map: no state is carried over.
        if (visitId != forayService.VisitId || support != ZoneSupport.Supported)
        {
            if (visitId != forayService.VisitId)
                pinnedKey = null;
            visitId = forayService.VisitId;
            session.Clear();
        }

        // The scan feeds the session only on a supported field map (no work in the Arsenal, Bozja or the Crescent).
        enemies.Overlay = features.NeedsScan && support == ZoneSupport.Supported && !enemies.OverlayFaulted ? session : null;
        feed.Tick(now, support == ZoneSupport.Supported ? territory : 0u);
        session.SetSnapshot(feed.Snapshot);
        var local = objectTable.LocalPlayer;
        hasPlayer = local != null;
        if (local != null)
            player = local.Position;

        statusLine = support switch
        {
            ZoneSupport.NotAvailable => "Coverage not available for this zone.",
            ZoneSupport.BaldesionArsenal => "Coverage not available here (Baldesion Arsenal).",
            ZoneSupport.Supported when enemies.OverlayFaulted =>
                "Live feedback stopped after an error. Turn the overlay off and on again.",
            ZoneSupport.Supported when features.NeedsScan && !session.ScanActive(now) =>
                "Recording paused: live feedback needs Crowdsource data and Record in Eureka.",
            _ => ""
        };
        feedLine = support != ZoneSupport.Supported ? "" : FeedText(feed, now);
        if (support != ZoneSupport.Supported || !hasPlayer)
        {
            rowCount = emptyCount = deathRowCount = packCount = mapPointCount = 0;
            pack = default;
            selected = default;
            return;
        }

        if (features.Conditions || features.Map)
            ReadConditions();
        BuildDeaths(now);
        BuildNearest();
        BuildSelected(now);
        BuildPointerText();
        BuildConfirmations(now);
        BuildPack(now);
        BuildMap();
    }

    private void BuildPointerText()
    {
        if (!selected.Valid)
        {
            pointerText = pointerEdgeText = "";
            return;
        }

        var dx = selected.Anchor.X - player.X;
        var dz = selected.Anchor.Z - player.Z;
        var distance = MathF.Sqrt(dx * dx + dz * dz).ToString("0", CultureInfo.InvariantCulture);
        pointerText = selected.Name + "  " + distance + " y";
        pointerEdgeText = pointerText + " " + Compass.Direction(dx, dz);
    }

    /// <summary>A scan time (Environment.TickCount64) as wall clock ms, for the weather forecast.</summary>
    private long Unix(long tickMs) => unixNow - (tickNow - tickMs);

    private void BuildPack(long now)
    {
        packCount = 0;
        pack = default;
        if (!features.PackWatch)
            return;
        var watch = session.Pack;
        watch.Expire(now, p => RespawnTimer.HoldsNow(territory, p.Condition, unixNow));
        var next = -1;
        foreach (var p in watch.Points)
        {
            if (p.Outcome != PackOutcome.Pending)
                continue;
            var estimate = RespawnTable.For(p.BnpcId, p.Condition, territory);
            if (!estimate.HasTimer)
                continue;
            var left = RespawnText.Remaining(p.DiedAtMs, now, estimate.Seconds);
            next = next < 0 ? left : Math.Min(next, left);
        }

        pack = watch.Status(player, features.PackRadius, now, IsConfirmedNow, next);
        var sumY = 0f;
        foreach (var p in watch.Points)
            if (p.Outcome == PackOutcome.Pending && packCount < packPoints.Length)
            {
                packPoints[packCount++] = p.Anchor;
                sumY += p.Anchor.Y;
            }

        packY = packCount > 0 ? sumY / packCount : player.Y;
        if (pack.HasSpot)
            PackGeometry.Outline(watch.PendingXz, pack.Spot, features.PackRadius, area);
    }

    private bool IsConfirmedNow(PackPoint p) =>
        session.Confirmations.ContainsKey(p.Key) ||
        session.Snapshot?.ByKey.TryGetValue(p.Key, out var row) == true && session.StatusOf(row, out _) == CoverageStatus.Confirmed;

    private ZoneSupport Support(uint zone, uint mapId)
    {
        if (zone == 0 || !ForayZones.IsForayTerritory(zone))
            return ZoneSupport.None;
        if (CoverageFeed.ZoneName(zone) == null)
            return ZoneSupport.NotAvailable;
        if (mapId == 0)
            return ZoneSupport.None;
        // The Baldesion Arsenal shares territory 827 (map layers 520-527): never show Hydatos field points there.
        return OverlayZones.IsSupportedFieldMap(zone, mapId) ? ZoneSupport.Supported :
            zone == ForayZones.EurekaHydatos ? ZoneSupport.BaldesionArsenal : ZoneSupport.NotAvailable;
    }

    private static string FeedText(CoverageFeed feed, long now)
    {
        var data = feed.Snapshot is { DataThrough.Length: > 0 } s ? ", data through " + s.DataThrough : "";
        var age = feed.LastCheckedMs is { } at ? AgeText(now - at) : "";
        return feed.State switch
        {
            CoverageFeedState.Loading => "Coverage: downloading...",
            CoverageFeedState.Fresh => "Coverage checked " + age + data,
            CoverageFeedState.OfflineCached => "Coverage: offline / cached" + (age.Length > 0 ? " (checked " + age + ")" : "") + data,
            CoverageFeedState.Unavailable => "Global coverage unavailable (" + feed.LastError + "): local feedback only.",
            _ => ""
        };
    }

    private static string AgeText(long ms) =>
        ms < 60_000 ? "just now" : (ms / 60_000).ToString(CultureInfo.InvariantCulture) + " min ago";

    private unsafe void ReadConditions()
    {
        activeFates.Clear();
        foreach (var fate in fateTable)
            activeFates.Add(fate.FateId);
        var weather = WeatherManager.Instance();
        weatherId = weather == null ? (byte)0 : weather->GetCurrentWeather();
        night = ConditionCheck.IsNight(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        conditionStates.Clear();
    }

    private string? WeatherName()
    {
        if (weatherId == 0)
            return null;
        if (!weatherNames.TryGetValue(weatherId, out var name))
        {
            name = dataManager.GetExcelSheet<Weather>(ClientLanguage.English).TryGetRow(weatherId, out var row)
                ? row.Name.ExtractText() : "";
            weatherNames[weatherId] = name;
        }

        return name.Length == 0 ? null : name;
    }

    private ConditionState ConditionOf(CoveragePoint point) =>
        features.Conditions ? EvaluateCondition(point) : ConditionState.Unknown;

    /// <summary>The condition now (night, weather, FATE), whether or not box 8 is ticked (the map filter uses it).</summary>
    private ConditionState EvaluateCondition(CoveragePoint point)
    {
        if (!conditionStates.TryGetValue(point.Condition, out var state))
        {
            state = ConditionCheck.Evaluate(point.Condition, WeatherName(), night, activeFates);
            if (conditionStates.Count < 256)
                conditionStates[point.Condition] = state;
        }

        return state;
    }

    private void BuildNearest()
    {
        rowCount = 0;
        emptyCount = 0;
        var snapshot = session.Snapshot;
        if (snapshot == null)
            return;
        // The nearest needed point is also the fallback watch point of feature 3 when the list is off.
        var want = features.NearbyList ? features.ListRows : 1;
        for (var i = 0; i < nearest.Length; i++)
        {
            nearest[i] = null;
            nearestDistance[i] = float.MaxValue;
        }

        var display2 = features.DisplayYalms * features.DisplayYalms;
        foreach (var point in snapshot.Points)
        {
            var status = session.StatusOf(point, out _);
            if (status == CoverageStatus.Confirmed)
                continue;
            var dx = point.X - player.X;
            var dz = point.Z - player.Z;
            var d2 = dx * dx + dz * dz;
            var picked = point.Key == pinnedKey;
            if (want > 0 && SpawnsNow.Shows(features.ListSpawnsNow, EvaluateCondition(point), picked))
                Insert(point, MathF.Sqrt(d2), want);
            if (features.EmptyPoints && emptyCount < MaxEmpty && d2 <= display2 && point.HeightKnown && !session.IsOccupied(point) &&
                SpawnsNow.Shows(features.WorldSpawnsNow, EvaluateCondition(point), picked))
            {
                var greyed = ConditionOf(point) == ConditionState.Unavailable;
                empty[emptyCount++] = new EmptyMarker(point.Key, new Vector3(point.X, point.Y, point.Z), status,
                    point.Status == CoverageStatus.WalkPast, greyed, point.Key == pinnedKey);
            }
        }

        for (var i = 0; i < want && features.NearbyList; i++)
        {
            if (nearest[i] is not { } point)
                break;
            var row = rows[rowCount++];
            Fill(row, point, nearestDistance[i]);
        }
    }

    private void Insert(CoveragePoint point, float distance, int want)
    {
        if (distance >= nearestDistance[want - 1] &&
            !(distance == nearestDistance[want - 1] && string.CompareOrdinal(point.Key, nearest[want - 1]?.Key) < 0))
            return;
        var at = want - 1;
        while (at > 0 && (nearestDistance[at - 1] > distance ||
                          nearestDistance[at - 1] == distance && string.CompareOrdinal(point.Key, nearest[at - 1]?.Key) < 0))
        {
            nearest[at] = nearest[at - 1];
            nearestDistance[at] = nearestDistance[at - 1];
            at--;
        }

        nearest[at] = point;
        nearestDistance[at] = distance;
    }

    private void Fill(ListRow row, CoveragePoint point, float distance)
    {
        var status = session.StatusOf(point, out var local);
        var condition = ConditionOf(point);
        row.Key = point.Key;
        row.Name = NameOf(point.Name, point.BnpcId);
        row.PinnedName = "> " + row.Name;
        row.Status = (local != null ? "Confirmed locally" : CoverageStatusMap.Label(status)) +
                     (point.Qualifier != CoverageQualifier.None ? " (" + CoverageStatusMap.QualifierText(point.Qualifier) + ")" : "") +
                     (point.HeightKnown ? "" : ", height uncertain");
        row.Colour = CoverageStatusMap.Colour(status);
        row.Distance = distance.ToString("0", CultureInfo.InvariantCulture) + " y " + Compass.Direction(point.X - player.X, point.Z - player.Z);
        row.Condition = features.Conditions ? point.Condition + (condition == ConditionState.Unavailable ? " (" + ConditionCheck.Reason(point.Condition) + ")" : "") : "";
        row.Greyed = condition == ConditionState.Unavailable;
    }

    private static string NameOf(string name, uint bnpcId) =>
        name.Length > 0 ? name : "Enemy " + bnpcId.ToString(CultureInfo.InvariantCulture);

    private void BuildDeaths(long now)
    {
        deathRowCount = 0;
        if (!features.Respawn)
            return;
        var n = session.SortedDeaths(deathBuffer);
        for (var i = 0; i < n; i++)
        {
            var death = deathBuffer[i];
            var row = deathRows[deathRowCount++];
            var point = death.PointKey != null && session.Snapshot?.ByKey.TryGetValue(death.PointKey, out var p) == true ? p : null;
            row.Anchor = death.Anchor;
            row.Name = NameOf(point?.Name ?? "", death.BnpcId);
            row.Who = death.DefeatBy switch { 1 => "Your kill", 2 => "Party kill", _ => "Observed death" };
            if (death.FateBound)
            {
                row.Timer = "FATE enemy: no timer";
                row.Basis = "";
                row.BasisText = "";
            }
            else
            {
                var estimate = RespawnTable.For(point?.BnpcId ?? death.BnpcId, point?.Condition ?? "", territory);
                row.Timer = RespawnTimer.Text(territory, point?.Condition ?? "", estimate, Unix(death.DiedAtMs), unixNow, out var remaining);
                row.Basis = RespawnTimer.Basis(estimate);
                row.BasisText = row.Basis.Length == 0 || remaining < 0 ? "" : "(" + row.Basis + ")";
            }

            row.Lost = WatchRules.ContinuityLost(death.MaxPlayerDistance);
            row.Approximate = death.AnchorApproximate;
            row.FateBound = death.FateBound;
            row.Key = death.PointKey;
            row.TrackId = death.TrackId;
            var dx = death.Anchor.X - player.X;
            var dz = death.Anchor.Z - player.Z;
            row.Distance = Vector3.Distance(death.Anchor, player).ToString("0", CultureInfo.InvariantCulture) + " y " + Compass.Direction(dx, dz);
        }
    }

    private void BuildSelected(long now)
    {
        selected = default;
        CoveragePoint? point = null;
        if (pinnedKey != null && session.Snapshot?.ByKey.TryGetValue(pinnedKey, out var pinned) == true)
            point = pinned;
        DeathRow? death = null;
        for (var i = 0; i < deathRowCount; i++)
        {
            if (point != null ? deathRows[i].Key == point.Key : deathRows[i].Who == "Your kill" || pinnedKey == null && i == 0)
            {
                death = deathRows[i];
                break;
            }
        }

        if (point == null && death?.Key != null && session.Snapshot?.ByKey.TryGetValue(death.Key, out var byDeath) == true)
            point = byDeath;
        if (point == null && death == null && nearest[0] != null && session.Snapshot?.ByKey.TryGetValue(nearest[0]!.Key, out var first) == true)
            point = first;
        if (point == null && death == null)
            return;

        var anchor = point != null ? new Vector3(point.X, point.Y, point.Z) : death!.Anchor;
        var heightKnown = point?.HeightKnown ?? !death!.Approximate;
        var distance = heightKnown ? Vector3.Distance(anchor, player)
            : CoverageMatcher.DistanceXz(anchor.X, anchor.Z, player.X, player.Z);
        var stood = heightKnown && session.Meter.StoodWithin(anchor, OverlayRates.WatchYalms, now);
        var advice = WatchRules.Advice(distance, stood);
        var status = point != null ? session.StatusOf(point, out _) : CoverageStatus.Unknown;
        selected = new Target(true, point?.Key, anchor, heightKnown,
            NameOf(point?.Name ?? death?.Name ?? "", point?.BnpcId ?? 0) + (pinnedKey != null && point?.Key == pinnedKey ? " (pinned)" : ""),
            point != null ? CoverageStatusMap.Label(status) : "Unmatched point",
            CoverageStatusMap.Colour(status),
            distance.ToString("0", CultureInfo.InvariantCulture) + " / 80 y" + (heightKnown ? "" : " (horizontal, height uncertain)"),
            heightKnown ? session.Meter.WithinMs(anchor, OverlayRates.WatchYalms, now) / 5000f : 0f,
            WatchRules.Text(advice) + (death != null && death.Lost ? " | Respawn-watch continuity lost" : ""),
            death != null ? "Respawn " + death.Timer + (death.Approximate ? " (location approximate)" : "") : "",
            point is { PhotoNeeded: true } ? (distance <= OverlayRates.PhotoYalms ? "Photo still needed: within 70 y" : "Photo still needed: get within 70 y") : "",
            advice == WatchAdvice.MoveCloser);
    }

    private void BuildConfirmations(long now)
    {
        var count = session.Confirmations.Count;
        confirmLine = count == 0 ? "" : count.ToString(CultureInfo.InvariantCulture) + " confirmed locally, upload pending";
        if (session.LastConfirmation is { } last && now - last.AtMs <= OverlayRates.ConfirmFlashMs)
        {
            flashAnchor = last.Anchor;
            flashKey = last.Key;
            flashUntilMs = last.AtMs + OverlayRates.ConfirmFlashMs;
            flashLabel = last.NoLayout ? "Spawn watched locally; server match uncertain" :
                last.Provisional ? "Confirmed locally (new point)" : "Confirmed locally";
        }
    }

    /// <summary>
    /// Feature 9: the points the map shows, filtered by the status boxes and the two "just spawning" filters, nearest
    /// to the player first (the per-view cap keeps the nearest). Pending respawn timers come from the same estimates
    /// as feature 2.
    /// </summary>
    private void BuildMap()
    {
        mapPointCount = 0;
        pendingTimers.Clear();
        if (!features.Map || session.Snapshot is not { } snapshot)
            return;
        foreach (var death in session.Deaths)
        {
            if (death.PointKey == null || death.FateBound || pendingTimers.Count >= CollectorSession.MaxDeaths)
                continue;
            var point = snapshot.ByKey.TryGetValue(death.PointKey, out var p) ? p : null;
            var condition = point?.Condition ?? "";
            var estimate = RespawnTable.For(point?.BnpcId ?? death.BnpcId, condition, territory);
            pendingTimers[death.PointKey] = RespawnTimer.Text(territory, condition, estimate, Unix(death.DiedAtMs), unixNow, out _);
        }

        var filter = features.MapFilter;
        foreach (var point in snapshot.Points)
        {
            if (mapPointCount >= mapPoints.Length)
                break;
            var status = session.StatusOf(point, out _);
            var pending = pendingTimers.ContainsKey(point.Key);
            var picked = point.Key == pinnedKey;
            var condition = EvaluateCondition(point);
            if (!filter.Shows(status, condition, pending, picked))
                continue;
            var dx = point.X - player.X;
            var dz = point.Z - player.Z;
            mapPoints[mapPointCount++] = new MapPoint(point, status, condition == ConditionState.Unavailable, pending, picked, dx * dx + dz * dz);
        }

        Array.Sort(mapPoints, 0, mapPointCount, mapOrder);
        mapPicked = -1;
        for (var i = 0; i < mapPointCount && pinnedKey != null; i++)
            if (mapPoints[i].Picked)
            {
                mapPicked = i;
                break;
            }
    }

    // ---- Drawing (every frame): no allocation, only the prepared values ----

    public void Draw()
    {
        if (!features.Enabled || !ticking || features.Hidden || support == ZoneSupport.None)
            return;
        if (features.HideInCombat && condition[ConditionFlag.InCombat])
            return;
        try
        {
            DrawWindow();
            if (support == ZoneSupport.Supported && hasPlayer && features.DrawsWorld)
                DrawWorld(Environment.TickCount64);
            if (support == ZoneSupport.Supported && hasPlayer && features.Map)
                DrawMap();
        }
        catch (Exception ex)
        {
            log.Warning($"Collector overlay draw failed ({ex.GetType().Name}).");
        }
    }

    private void DrawWindow()
    {
        var listOrRange = features.NearbyList || features.Range || features.Respawn || features.LocalConfirm || features.PackWatch || features.Map;
        if (!listOrRange && statusLine.Length == 0)
            return;
        // A sensible first position and size; afterwards ImGui keeps where the window was put.
        ImGui.SetNextWindowPos(new Vector2(40, 260), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(480, 340), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 120), new Vector2(1000, 1000));
        if (!ImGui.Begin("Collector overlay##XivForaysCollector", ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.End();
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 2));
        if (statusLine.Length > 0)
            ImGui.TextWrapped(statusLine);
        if (ImGui.SmallButton("Hide overlay"))
        {
            plugin.Configuration.OverlayHidden = true;
            plugin.Configuration.Save();
            ApplyConfig(plugin.Configuration);
        }

        if (feedLine.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(feedLine);
        }

        if (support == ZoneSupport.Supported)
        {
            if (features.Map)
                ImGui.TextDisabled(mapLine.Length > 0 ? mapLine : "Map: open the area map to see the spawn points.");
            if (features.PackWatch)
                DrawPackLines();
            if (features.LocalConfirm)
            {
                // Always two lines, so nothing below moves when a confirmation comes and goes.
                if (Environment.TickCount64 < flashUntilMs)
                    ImGui.TextColored(Rgba(CoverageStatusMap.Colour(CoverageStatus.Confirmed)), flashLabel);
                else
                    ImGui.TextDisabled(" ");
                ImGui.TextDisabled(confirmLine.Length > 0 ? confirmLine : "No local confirmation pending.");
            }

            if (features.Range)
                DrawSelected();
            if (features.NearbyList)
                DrawList();
            if (features.Respawn)
                DrawDeaths();
        }

        ImGui.PopStyleVar();
        ImGui.End();
    }

    private void DrawPackLines()
    {
        ImGui.Separator();
        if (pack.Line.Length == 0)
        {
            ImGui.TextDisabled("Pack watch: kill a pack, then wait where all its spawn points are in range.");
            ImGui.TextDisabled(" ");
            return;
        }

        ImGui.TextColored(pack.Done ? Good : pack.AllInRange ? new Vector4(1f, 1f, 1f, 1f) : Amber, pack.Line);
        if (pack.Detail.Length > 0)
            ImGui.TextColored(Amber, pack.Detail);
        else
            ImGui.TextDisabled(" ");
    }

    private void DrawSelected()
    {
        // A fixed block of four lines: the list below never jumps when the selection changes.
        ImGui.Separator();
        if (!selected.Valid)
        {
            ImGui.TextDisabled("No point selected.");
            ImGui.TextDisabled(" ");
            ImGui.TextDisabled(" ");
            ImGui.TextDisabled(" ");
            return;
        }

        ImGui.TextColored(Rgba(selected.Colour), selected.Status);
        ImGui.SameLine();
        ImGui.TextUnformatted(selected.Name);
        if (pinnedKey != null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Unpin##XivForaysUnpin"))
                pinnedKey = null;
        }

        ImGui.TextUnformatted(selected.RangeText);
        ImGui.SameLine(220);
        ImGui.ProgressBar(selected.Meter, new Vector2(-1, ImGui.GetTextLineHeight()), "5 s history");
        if (selected.MoveCloser)
            ImGui.TextColored(Amber, selected.Advice);
        else
            ImGui.TextUnformatted(selected.Advice);
        var extra = selected.Respawn.Length > 0 && selected.Photo.Length > 0 ? selected.Respawn + " | " + selected.Photo :
            selected.Respawn.Length > 0 ? selected.Respawn : selected.Photo;
        ImGui.TextDisabled(extra.Length > 0 ? extra : " ");
    }

    private void DrawDeaths()
    {
        ImGui.Separator();
        if (deathRowCount == 0)
        {
            ImGui.TextDisabled("No respawn pending.");
            return;
        }

        if (!ImGui.BeginTable("##XivForaysDeaths", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoHostExtendX))
            return;
        ImGui.TableSetupColumn("who", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn("timer", ImGuiTableColumnFlags.WidthFixed, 250);
        ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, 70);
        for (var i = 0; i < deathRowCount; i++)
        {
            var row = deathRows[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Who);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Timer);
            if (row.BasisText.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(row.BasisText);
            }

            if (row.Lost)
            {
                ImGui.SameLine();
                ImGui.TextColored(Amber, "lost");
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled(row.Distance);
        }

        ImGui.EndTable();
    }

    private void DrawList()
    {
        ImGui.Separator();
        if (session.Snapshot == null)
        {
            ImGui.TextDisabled("No coverage yet.");
            return;
        }

        var columns = features.Conditions ? 4 : 3;
        if (!ImGui.BeginTable("##XivForaysNearby", columns, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoHostExtendX))
            return;
        ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("status", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, 70);
        if (features.Conditions)
            ImGui.TableSetupColumn("condition", ImGuiTableColumnFlags.WidthFixed, 200);
        // Always the configured number of rows (blank when fewer points): no layout jump as points come and go.
        for (var i = 0; i < features.ListRows; i++)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (i >= rowCount)
            {
                ImGui.TextDisabled(i == 0 ? "Nothing needed nearby." : " ");
                continue;
            }

            var row = rows[i];
            var pinned = row.Key == pinnedKey;
            ImGui.PushID(i);
            if (pinned)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, 0x90A0602Au);
            if (ImGui.Selectable(pinned ? row.PinnedName : row.Name, pinned, ImGuiSelectableFlags.SpanAllColumns))
                pinnedKey = pinned ? null : row.Key;
            ImGui.PopID();
            ImGui.TableNextColumn();
            var colour = Rgba(row.Colour);
            if (row.Greyed)
                colour.W = 0.45f;
            ImGui.TextColored(colour, row.Status);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Distance);
            if (features.Conditions)
            {
                ImGui.TableNextColumn();
                if (row.Greyed)
                    ImGui.TextDisabled(row.Condition);
                else
                    ImGui.TextUnformatted(row.Condition);
            }
        }

        ImGui.EndTable();
        ImGui.TextDisabled(pinnedKey == null ? "Click a row to pick it: the game points to it." : "Picked: the game points to it. Click it again to let go.");
    }

    private void DrawWorld(long now)
    {
        var list = ImGui.GetBackgroundDrawList();
        var scale = features.MarkerScale;
        var alpha = features.Opacity;
        var display2 = features.DisplayYalms * features.DisplayYalms;
        var budget = features.MaxMarkers;
        var liveBudget = budget / 2;
        labels.Clear();

        if (features.PackWatch)
            DrawPack(list, alpha);

        if (features.Rings && selected.Valid && selected.HeightKnown)
        {
            DrawRing(list, selected.Anchor, OverlayRates.WatchYalms, Colour(0xFFFFB768u, alpha * 0.8f), "80 y watch");
            DrawRing(list, selected.Anchor, OverlayRates.PhotoYalms, Colour(0xFF66D1FFu, alpha * 0.8f), "70 y photo");
        }

        if (features.Pointer && pinnedKey != null && selected.Valid && selected.Key == pinnedKey)
            DrawPointer(list, alpha, scale);

        // One label per spawn: the enemy marker first, then the confirmation flash, timers and empty markers only
        // where no label is drawn yet (the flash used to be drawn on top of the respawned enemy's own marker).
        if (features.EnemyMarkers && session.Snapshot != null)
        {
            foreach (var enemy in session.LiveValues)
            {
                if (budget <= 0 || liveBudget <= 0)
                    break;
                if (Vector3.DistanceSquared(enemy.Position, player) > display2)
                    continue;
                CoverageStatus status;
                OverlayConfirmation? local = null;
                if (enemy.Match.Point is { } point)
                    status = session.StatusOf(point, out local);
                else
                    status = CoverageStatus.Unknown;
                var flash = local != null && now - local.AtMs <= OverlayRates.ConfirmFlashMs;
                if (status == CoverageStatus.Confirmed && !features.ShowConfirmed && !flash)
                    continue;
                if (enemy.Match.Point is { } matched && !flash &&
                    !SpawnsNow.Shows(features.WorldSpawnsNow, EvaluateCondition(matched), matched.Key == pinnedKey))
                    continue;
                if (!labels.TryClaim(enemy.Match.Point?.Key, enemy.Anchor))
                    continue;
                var text = enemy.Match.Point == null
                    ? enemy.Match.Kind == MatchKind.Ambiguous ? "Ambiguous" : "Unmatched"
                    : local != null ? "Confirmed locally" : CoverageStatusMap.Label(status);
                if (Label(list, enemy.Position + new Vector3(0, 2.5f, 0), text, Colour(CoverageStatusMap.Colour(status), alpha), scale,
                        ShapeOf(status), false))
                {
                    budget--;
                    liveBudget--;
                }
            }
        }

        if (features.LocalConfirm && now < flashUntilMs && budget > 0 && labels.TryClaim(flashKey, flashAnchor))
        {
            if (Label(list, flashAnchor + new Vector3(0, 3f, 0), flashLabel, Colour(CoverageStatusMap.Colour(CoverageStatus.Confirmed), 1f), scale * 1.2f, Shape.Check, false))
                budget--;
        }

        if (features.Respawn)
            for (var i = 0; i < deathRowCount && budget > 0; i++)
            {
                var row = deathRows[i];
                if (Vector3.DistanceSquared(row.Anchor, player) > display2 || !labels.TryClaim(row.Key, row.Anchor))
                    continue;
                if (Label(list, row.Anchor + new Vector3(0, 2f, 0), row.Timer, Colour(row.DefeatColour, alpha), scale, Shape.Clock, false))
                    budget--;
            }

        if (features.EmptyPoints)
            for (var i = 0; i < emptyCount && budget > 0; i++)
            {
                var marker = empty[i];
                if (!labels.TryClaim(marker.Key, marker.Position))
                    continue;
                var colour = Colour(CoverageStatusMap.Colour(marker.Status), marker.Greyed ? alpha * 0.4f : alpha);
                if (Label(list, marker.Position + new Vector3(0, 0.5f, 0), CoverageStatusMap.Label(marker.Status), colour,
                        marker.Pinned ? scale * 1.2f : scale, ShapeOf(marker.Status), marker.Hollow))
                    budget--;
            }
    }

    /// <summary>
    /// The picked point: a beacon over the spot with its name and distance, a dashed guide on the ground from the
    /// player to it, and when the spot is off screen an arrow at the screen edge with the distance.
    /// </summary>
    private void DrawPointer(ImDrawListPtr list, float alpha, float scale)
    {
        var colour = Colour(0xFF40E0FFu, MathF.Max(alpha, 0.8f));
        var anchor = selected.Anchor;
        var spot = selected.HeightKnown ? anchor : anchor with { Y = player.Y };
        var distance = CoverageMatcher.DistanceXz(spot.X, spot.Z, player.X, player.Z);

        // Guide: dashes from the player's feet to the spot, height interpolated, failed projections skipped.
        var steps = Math.Clamp((int)(distance / 4f), 2, GuideSteps);
        var havePrevious = gameGui.WorldToScreen(player, out var previous);
        for (var i = 1; i <= steps; i++)
        {
            var world = Vector3.Lerp(player, spot, i / (float)steps);
            var ok = gameGui.WorldToScreen(world, out var screen);
            if (ok && havePrevious && i % 2 == 1)
                list.AddLine(previous, screen, colour, 3f);
            havePrevious = ok;
            previous = screen;
        }

        // Beacon: a vertical line up from the spot, a ring at the foot and the label at the top.
        var front = gameGui.WorldToScreen(spot, out var foot, out var inView);
        if (front && inView)
        {
            var labelBase = foot;
            if (gameGui.WorldToScreen(spot + new Vector3(0, 8f, 0), out var top))
            {
                list.AddLine(foot, top, Colour(0xFF000000u, alpha * 0.6f), 6f);
                list.AddLine(foot, top, colour, 3f);
                labelBase = top;
            }

            DrawWorldCircle(list, spot, 1.5f, colour, 2.5f);
            var size = ImGui.CalcTextSize(pointerText);
            var at = new Vector2(labelBase.X - size.X / 2, labelBase.Y - size.Y - 6 * scale);
            list.AddRectFilled(at - new Vector2(4, 2), at + size + new Vector2(4, 2), Colour(0xFF000000u, 0.65f), 3f);
            list.AddText(at, colour, pointerText);
            return;
        }

        // Off screen: which way to turn. In front of the camera the projection itself gives the way; behind it, a
        // short step from the player towards the spot does.
        var viewport = ImGui.GetMainViewport();
        var centre = viewport.Pos + viewport.Size / 2f;
        Vector2 direction;
        if (front)
            direction = foot - centre;
        else
        {
            var toward = new Vector3(spot.X - player.X, 0, spot.Z - player.Z);
            toward = toward.LengthSquared() > 1e-4f ? Vector3.Normalize(toward) * 2f : new Vector3(0, 0, -2f);
            direction = gameGui.WorldToScreen(player, out var a) && gameGui.WorldToScreen(player + toward, out var b) ? b - a : centre - foot;
        }

        var edge = viewport.Pos + ScreenEdge.Point(viewport.Size, direction, 70f);
        var d = direction.LengthSquared() > 1e-6f ? Vector2.Normalize(direction) : new Vector2(0, 1);
        var n = new Vector2(-d.Y, d.X);
        var tip = edge + d * 22f;
        list.AddTriangleFilled(tip + d * 2f, edge - n * 13f, edge + n * 13f, Colour(0xFF000000u, 0.6f));
        list.AddTriangleFilled(tip, edge - n * 10f, edge + n * 10f, colour);
        var labelSize = ImGui.CalcTextSize(pointerEdgeText);
        var labelAt = edge - d * 34f - labelSize / 2f;
        list.AddRectFilled(labelAt - new Vector2(4, 2), labelAt + labelSize + new Vector2(4, 2), Colour(0xFF000000u, 0.65f), 3f);
        list.AddText(labelAt, colour, pointerEdgeText);
    }

    /// <summary>Pack watch drawing: a small ring at each waiting spawn point, the safe area outline, the best spot and the status text.</summary>
    private void DrawPack(ImDrawListPtr list, float alpha)
    {
        if (pack.Line.Length == 0)
            return;
        var inside = pack.AllInRange;
        var ringColour = Colour(0xFF66D1FFu, alpha);
        for (var i = 0; i < packCount; i++)
            DrawWorldCircle(list, packPoints[i], 2.5f, ringColour, 2.5f);

        if (pack.HasSpot)
        {
            var areaColour = Colour(inside ? 0xFF7FE07Fu : 0xFF40C0FFu, alpha * 0.9f);
            for (var i = 0; i < AreaSegments; i++)
                areaOk[i] = gameGui.WorldToScreen(new Vector3(area[i].X, packY, area[i].Y), out areaScreen[i]);
            for (var i = 0; i < AreaSegments; i++)
            {
                var j = (i + 1) % AreaSegments;
                if (areaOk[i] && areaOk[j])
                    list.AddLine(areaScreen[i], areaScreen[j], areaColour, 2.5f);
            }

            if (gameGui.WorldToScreen(new Vector3(pack.Spot.X, packY, pack.Spot.Y), out var at))
            {
                list.AddCircleFilled(at, 7f, Colour(0xFF000000u, 0.6f));
                list.AddLine(at - new Vector2(7, 7), at + new Vector2(7, 7), areaColour, 3f);
                list.AddLine(at - new Vector2(7, -7), at + new Vector2(7, -7), areaColour, 3f);
                const string tag = "Best spot";
                var tagSize = ImGui.CalcTextSize(tag);
                var tagAt = new Vector2(at.X - tagSize.X / 2, at.Y + 10);
                list.AddRectFilled(tagAt - new Vector2(3, 1), tagAt + tagSize + new Vector2(3, 1), Colour(0xFF000000u, 0.6f), 3f);
                list.AddText(tagAt, areaColour, tag);
            }
        }

        // The status line on screen, top centre.
        var viewport = ImGui.GetMainViewport();
        var colour = pack.Done ? 0xFF99E673u : inside ? 0xFFFFFFFFu : 0xFF66D1FFu;
        var lineSize = ImGui.CalcTextSize(pack.Line);
        var detailSize = pack.Detail.Length > 0 ? ImGui.CalcTextSize(pack.Detail) : Vector2.Zero;
        var top = viewport.Pos.Y + viewport.Size.Y * 0.12f;
        var width = MathF.Max(lineSize.X, detailSize.X);
        var height = lineSize.Y + (detailSize.Y > 0 ? detailSize.Y + 2 : 0);
        var boxAt = new Vector2(viewport.Pos.X + (viewport.Size.X - width) / 2, top);
        list.AddRectFilled(boxAt - new Vector2(8, 4), boxAt + new Vector2(width, height) + new Vector2(8, 4), Colour(0xFF000000u, 0.65f), 4f);
        list.AddText(new Vector2(viewport.Pos.X + (viewport.Size.X - lineSize.X) / 2, top), Colour(colour, 1f), pack.Line);
        if (pack.Detail.Length > 0)
            list.AddText(new Vector2(viewport.Pos.X + (viewport.Size.X - detailSize.X) / 2, top + lineSize.Y + 2), Colour(0xFF66D1FFu, 1f), pack.Detail);
    }

    /// <summary>
    /// Feature 9: the spawn points drawn over the game's area map window (ImGui, read-only). The map image node of the
    /// open map gives where the map texture is on screen (it moves and scales with the game's zoom and pan), and the
    /// map agent gives the open map's id, size factor and offsets. Drawn only while the map shows the player's own map,
    /// clipped to the map view, at most the configured number of markers (nearest first). No allocation per frame
    /// except the tooltip text, rebuilt only when the hovered point or the 500 ms data changes.
    /// </summary>
    private unsafe void DrawMap()
    {
        var addonPtr = gameGui.GetAddonByName("AreaMap");
        if (addonPtr.IsNull || !addonPtr.IsVisible)
        {
            mapWasOpen = false;
            SetMapLine(-1, 0, 0);
            mapHoverKey = "";
            return;
        }

        var addon = (AddonAreaMap*)addonPtr.Address;
        var agent = AgentMap.Instance();
        var component = addon->ComponentMap;
        if (agent == null || component == null || component->BaseMapImage == null)
        {
            SetMapLine(-2, 0, 0);
            return;
        }

        if (agent->SelectedMapId != clientState.MapId || agent->SelectedMapSizeFactor <= 0)
        {
            SetMapLine(-3, 0, 0);
            return;
        }

        var origin = ImGui.GetMainViewport().Pos;
        var image = &component->BaseMapImage->AtkResNode;
        var frame = component->OwnerNode != null ? &component->OwnerNode->AtkResNode : addon->RootNode;
        if (frame == null)
            return;
        var clipMin = origin + new Vector2(frame->ScreenX, frame->ScreenY);
        var clipMax = clipMin + new Vector2(frame->Width * frame->Transform.M11, frame->Height * frame->Transform.M22);
        var view = MapView.FromNode(origin.X + image->ScreenX, origin.Y + image->ScreenY, image->Transform.M11, image->Transform.M22,
            image->Width, image->Height, agent->SelectedOffsetX, agent->SelectedOffsetY, agent->SelectedMapSizeFactor, clipMin, clipMax);
        if (!view.Valid)
        {
            SetMapLine(-2, 0, 0);
            return;
        }

        // A check for the test: how far the game's own player arrow is from where this maths puts the player.
        var gap = -1;
        var cone = addon->AreaMap.PlayerCone;
        if (cone != null && cone->AtkResNode.IsVisible())
        {
            var node = &cone->AtkResNode;
            var pivot = origin + new Vector2(node->ScreenX + node->Transform.M11 * node->OriginX + node->Transform.M21 * node->OriginY,
                node->ScreenY + node->Transform.M12 * node->OriginX + node->Transform.M22 * node->OriginY);
            var distance = Vector2.Distance(pivot, view.ToScreen(player.X, player.Z));
            gap = float.IsFinite(distance) ? (int)MathF.Min(distance, 9999f) : -1;
        }

        if (!mapWasOpen)
        {
            mapWasOpen = true;
            log.Information(string.Create(CultureInfo.InvariantCulture,
                $"Collector overlay map view: size factor {agent->SelectedMapSizeFactor}, offset {agent->SelectedOffsetX}/{agent->SelectedOffsetY}, image {image->Width}x{image->Height} scale {image->Transform.M11:0.###}/{image->Transform.M22:0.###}, map scale {addon->AreaMap.MapScale:0.###}, frame {frame->Width}x{frame->Height}, arrow gap {gap} px"));
        }

        var list = ImGui.GetBackgroundDrawList();
        list.PushClipRect(clipMin, clipMax, false);
        var alpha = features.Opacity;
        var size = 4.5f * features.MarkerScale;
        var mouse = ImGui.GetMousePos();
        var hoverLimit = MathF.Max(MapRates.HoverPixels, size + 2f);
        var hovered = -1;
        var hoveredDistance = hoverLimit * hoverLimit;
        var budget = features.MapMaxMarkers;
        var drawn = 0;
        var pickedAt = -1;
        for (var i = 0; i < mapPointCount && drawn < budget; i++)
        {
            var marker = mapPoints[i];
            var screen = view.ToScreen(marker.Point.X, marker.Point.Z);
            if (!view.Visible(screen, size))
                continue;
            drawn++;
            if (marker.Picked)
                pickedAt = i;
            var colour = Colour(CoverageStatusMap.Colour(marker.Status), marker.Greyed ? alpha * 0.4f : alpha);
            MapShape(list, screen, size, colour, ShapeOf(marker.Status), marker.Status == CoverageStatus.WalkPast);
            if (marker.Pending)
                list.AddCircle(screen, size + 3f, Colour(0xFFB0D64Du, alpha), 16, 2f);
            var d2 = Vector2.DistanceSquared(mouse, screen);
            if (d2 < hoveredDistance)
            {
                hoveredDistance = d2;
                hovered = i;
            }
        }

        // The picked point (always, even beyond the marker cap): a large ring and a cross, on top.
        if (mapPicked >= 0 && mapPicked < mapPointCount &&
            view.ToScreen(mapPoints[mapPicked].Point.X, mapPoints[mapPicked].Point.Z) is var at && view.Visible(at, size))
        {
            var picked = mapPoints[mapPicked];
            if (pickedAt < 0)
                MapShape(list, at, size, Colour(CoverageStatusMap.Colour(picked.Status), alpha), ShapeOf(picked.Status),
                    picked.Status == CoverageStatus.WalkPast);
            var pointer = Colour(0xFF40E0FFu, 1f);
            list.AddCircle(at, size + 8f, Colour(0xFF000000u, 0.6f), 24, 4f);
            list.AddCircle(at, size + 8f, pointer, 24, 2.5f);
            list.AddLine(at - new Vector2(size + 14f, 0), at - new Vector2(size + 5f, 0), pointer, 2f);
            list.AddLine(at + new Vector2(size + 5f, 0), at + new Vector2(size + 14f, 0), pointer, 2f);
        }

        if (features.PackWatch && pack.Line.Length > 0)
            DrawPackOnMap(list, view, alpha);
        list.PopClipRect();
        SetMapLine(drawn, mapPointCount, gap);

        if (hovered >= 0 && !ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow))
        {
            var marker = mapPoints[hovered];
            if (marker.Point.Key != mapHoverKey || mapHoverTick != tickNow)
            {
                mapHoverKey = marker.Point.Key;
                mapHoverTick = tickNow;
                mapHoverText = MapTooltip(marker);
            }

            ImGui.BeginTooltip();
            ImGui.TextUnformatted(mapHoverText);
            ImGui.EndTooltip();
        }
    }

    private int mapLineDrawn = int.MinValue;
    private int mapLineTotal = int.MinValue;
    private int mapLineGap = int.MinValue;

    /// <summary>The map line of the window, rebuilt only when a number changes (drawn below 0: a state code).</summary>
    private void SetMapLine(int drawn, int total, int gap)
    {
        if (drawn == mapLineDrawn && total == mapLineTotal && gap == mapLineGap)
            return;
        mapLineDrawn = drawn;
        mapLineTotal = total;
        mapLineGap = gap;
        mapLine = drawn switch
        {
            -1 => "",
            -2 => "Map: the map view could not be read.",
            -3 => "Map: showing another map; spawn points are drawn on your current zone's map only.",
            _ => "Map: " + drawn.ToString(CultureInfo.InvariantCulture) + " markers in view (" + total.ToString(CultureInfo.InvariantCulture) +
                 " match the filters)" + (gap >= 0 ? ", arrow check " + gap.ToString(CultureInfo.InvariantCulture) + " px" : "")
        };
    }

    private string MapTooltip(MapPoint marker)
    {
        var point = marker.Point;
        var level = session.LevelOf(point.BnpcId);
        var text = NameOf(point.Name, point.BnpcId) + (level > 0 ? "  Lv " + level.ToString(CultureInfo.InvariantCulture) : "") + "\n" +
                   (session.Confirmations.ContainsKey(point.Key) && marker.Status == CoverageStatus.Confirmed ? "Confirmed locally" : CoverageStatusMap.Label(marker.Status)) +
                   (point.Qualifier != CoverageQualifier.None ? " (" + CoverageStatusMap.QualifierText(point.Qualifier) + ")" : "") +
                   (point.HeightKnown ? "" : ", height uncertain") + "\n" +
                   "When: " + (point.Condition.Length > 0 ? point.Condition : "unknown") +
                   (marker.Greyed ? " (" + ConditionCheck.Reason(point.Condition) + ")" : "");
        if (marker.Pending && pendingTimers.TryGetValue(point.Key, out var timer))
            text += "\nRespawn " + timer;
        if (marker.Picked)
            text += "\nPicked in the list";
        return text;
    }

    /// <summary>Pack watch on the map: the waiting points, the safe area outline and the best spot.</summary>
    private void DrawPackOnMap(ImDrawListPtr list, MapView view, float alpha)
    {
        var ringColour = Colour(0xFF66D1FFu, alpha);
        for (var i = 0; i < packCount; i++)
            list.AddCircle(view.ToScreen(packPoints[i].X, packPoints[i].Z), 7f, ringColour, 16, 2f);
        if (!pack.HasSpot)
            return;
        var areaColour = Colour(pack.AllInRange ? 0xFF7FE07Fu : 0xFF40C0FFu, alpha);
        for (var i = 0; i < AreaSegments; i++)
        {
            var j = (i + 1) % AreaSegments;
            list.AddLine(view.ToScreen(area[i].X, area[i].Y), view.ToScreen(area[j].X, area[j].Y), areaColour, 2f);
        }

        var at = view.ToScreen(pack.Spot.X, pack.Spot.Y);
        list.AddLine(at - new Vector2(5, 5), at + new Vector2(5, 5), areaColour, 2.5f);
        list.AddLine(at - new Vector2(5, -5), at + new Vector2(5, -5), areaColour, 2.5f);
    }

    /// <summary>A small map marker in the in-world shapes: check, diamond, circle (hollow for walk-past), ?, square.</summary>
    private static void MapShape(ImDrawListPtr list, Vector2 at, float size, uint colour, Shape shape, bool hollow)
    {
        var backing = Colour(0xFF000000u, ((colour >> 24) & 0xFF) / 255f * 0.7f);
        switch (shape)
        {
            case Shape.Diamond:
                list.AddQuadFilled(at with { Y = at.Y - size - 1 }, at with { X = at.X + size + 1 }, at with { Y = at.Y + size + 1 },
                    at with { X = at.X - size - 1 }, backing);
                list.AddQuad(at with { Y = at.Y - size }, at with { X = at.X + size }, at with { Y = at.Y + size }, at with { X = at.X - size },
                    colour, 2f);
                break;
            case Shape.Circle:
                list.AddCircleFilled(at, size + 1, backing);
                if (!hollow)
                    list.AddCircleFilled(at, size - 1, colour);
                list.AddCircle(at, size - 0.5f, colour, 12, 1.8f);
                break;
            case Shape.Check:
                list.AddCircleFilled(at, size + 1, backing);
                list.AddLine(at + new Vector2(-size * 0.6f, 0), at + new Vector2(-size * 0.15f, size * 0.5f), colour, 2f);
                list.AddLine(at + new Vector2(-size * 0.15f, size * 0.5f), at + new Vector2(size * 0.7f, -size * 0.6f), colour, 2f);
                break;
            case Shape.Question:
                list.AddCircleFilled(at, size + 1, backing);
                list.AddCircle(at, size, colour, 12, 1.5f);
                var fontSize = MathF.Max(8f, size * 2.2f);
                list.AddText(ImGui.GetFont(), fontSize, at - new Vector2(fontSize * 0.27f, fontSize * 0.5f), colour, "?", 0f);
                break;
            default:
                list.AddRectFilled(at - new Vector2(size, size), at + new Vector2(size, size), backing);
                list.AddRect(at - new Vector2(size, size), at + new Vector2(size, size), colour, 0f, ImDrawFlags.None, 1.5f);
                break;
        }
    }

    /// <summary>A flat circle at the point's own height (16 segments, failed projections skipped).</summary>
    private void DrawWorldCircle(ImDrawListPtr list, Vector3 centre, float radius, uint colour, float thickness)
    {
        var havePrevious = false;
        Vector2 previous = default;
        for (var i = 0; i <= SmallRingSegments; i++)
        {
            var angle = i * MathF.Tau / SmallRingSegments;
            var ok = gameGui.WorldToScreen(new Vector3(centre.X + radius * MathF.Cos(angle), centre.Y, centre.Z + radius * MathF.Sin(angle)), out var screen);
            if (ok && havePrevious)
                list.AddLine(previous, screen, colour, thickness);
            havePrevious = ok;
            previous = screen;
        }
    }

    private enum Shape : byte
    {
        Check,
        Diamond,
        Circle,
        Question,
        Square,
        Clock
    }

    private static Shape ShapeOf(CoverageStatus status) => status switch
    {
        CoverageStatus.Confirmed => Shape.Check,
        CoverageStatus.ToConfirm => Shape.Diamond,
        CoverageStatus.WalkPast => Shape.Circle,
        CoverageStatus.NeverSeen => Shape.Question,
        _ => Shape.Square
    };

    /// <summary>One projected marker: shape, then the short word on a dark backing. False when off screen.</summary>
    private bool Label(ImDrawListPtr list, Vector3 world, string text, uint colour, float scale, Shape shape, bool hollow)
    {
        if (!gameGui.WorldToScreen(world, out var screen))
            return false;
        var size = 7f * scale;
        var backing = Colour(0xFF000000u, ((colour >> 24) & 0xFF) / 255f * 0.6f);
        var outline = Colour(0xFFFFFFFFu, ((colour >> 24) & 0xFF) / 255f);
        switch (shape)
        {
            case Shape.Diamond:
                var top = screen with { Y = screen.Y - size };
                var right = screen with { X = screen.X + size };
                var bottom = screen with { Y = screen.Y + size };
                var left = screen with { X = screen.X - size };
                list.AddQuad(top, right, bottom, left, outline, 3f);
                list.AddQuad(top, right, bottom, left, colour, 2f);
                break;
            case Shape.Circle:
                if (!hollow)
                    list.AddCircleFilled(screen, size, colour);
                list.AddCircle(screen, size, outline, 16, 1.5f);
                list.AddCircle(screen, size - 1.5f, colour, 16, 2f);
                break;
            case Shape.Check:
                list.AddCircleFilled(screen, size + 1, backing);
                list.AddLine(screen + new Vector2(-size * 0.6f, 0), screen + new Vector2(-size * 0.15f, size * 0.5f), colour, 2.5f);
                list.AddLine(screen + new Vector2(-size * 0.15f, size * 0.5f), screen + new Vector2(size * 0.7f, -size * 0.6f), colour, 2.5f);
                break;
            case Shape.Question:
                list.AddCircle(screen, size, outline, 16, 1.5f);
                list.AddText(screen - new Vector2(3.5f, 7f), colour, "?");
                break;
            case Shape.Clock:
                list.AddCircle(screen, size, colour, 16, 2f);
                list.AddLine(screen, screen + new Vector2(0, -size * 0.7f), colour, 2f);
                list.AddLine(screen, screen + new Vector2(size * 0.5f, 0), colour, 2f);
                break;
            default:
                list.AddRect(screen - new Vector2(size, size), screen + new Vector2(size, size), colour, 0f, ImDrawFlags.None, 2f);
                break;
        }

        var textSize = ImGui.CalcTextSize(text);
        var at = new Vector2(screen.X - textSize.X / 2, screen.Y + size + 3);
        list.AddRectFilled(at - new Vector2(3, 1), at + textSize + new Vector2(3, 1), backing, 3f);
        list.AddText(at, colour, text);
        return true;
    }

    /// <summary>
    /// A world-space ring: 48 points on the horizontal cross-section of the 3D range sphere at the player's height
    /// (radius sqrt(R*R - dy*dy)), projected one by one; a segment with a failed projection is skipped, never bridged.
    /// </summary>
    private void DrawRing(ImDrawListPtr list, Vector3 centre, float radius, uint colour, string text)
    {
        var dy = player.Y - centre.Y;
        if (MathF.Abs(dy) > radius)
            return;
        var r = MathF.Sqrt(radius * radius - dy * dy);
        for (var i = 0; i <= RingSegments; i++)
        {
            var angle = i * MathF.Tau / RingSegments;
            var world = new Vector3(centre.X + r * MathF.Cos(angle), player.Y, centre.Z + r * MathF.Sin(angle));
            ringOk[i] = gameGui.WorldToScreen(world, out ring[i]);
        }

        for (var i = 0; i < RingSegments; i++)
            if (ringOk[i] && ringOk[i + 1])
                list.AddLine(ring[i], ring[i + 1], colour, 2f);
        if (ringOk[0])
            list.AddText(ring[0] + new Vector2(4, -16), colour, text);
    }

    private static uint Colour(uint abgr, float alpha) =>
        (abgr & 0x00FFFFFFu) | ((uint)(Math.Clamp(alpha, 0f, 1f) * ((abgr >> 24) & 0xFF)) << 24);

    private static Vector4 Rgba(uint abgr) =>
        new((abgr & 0xFF) / 255f, ((abgr >> 8) & 0xFF) / 255f, ((abgr >> 16) & 0xFF) / 255f, ((abgr >> 24) & 0xFF) / 255f);

    public void Dispose()
    {
        Stop();
        fetcher?.Dispose();
        fetcher = null;
    }

    private sealed class ListRow
    {
        public string Key = "";
        public string Name = "";
        public string PinnedName = "";
        public string Status = "";
        public string Distance = "";
        public string Condition = "";
        public uint Colour;
        public bool Greyed;
    }

    private sealed class DeathRow
    {
        public string? Key;
        public Guid TrackId;
        public Vector3 Anchor;
        public string Name = "";
        public string Who = "";
        public string Timer = "";
        public string Basis = "";
        public string BasisText = "";
        public string Distance = "";
        public bool Lost;
        public bool Approximate;
        public bool FateBound;
        public uint DefeatColour => Who == "Your kill" ? 0xFFB0D64Du : 0xFFD0D0D0u;
    }

    /// <summary>One spawn point the map may draw (feature 9), with what the filters decided at the last tick.</summary>
    private readonly record struct MapPoint(CoveragePoint Point, CoverageStatus Status, bool Greyed, bool Pending, bool Picked, float Distance2);

    /// <summary>Nearest first; ties by key so the order is stable.</summary>
    private sealed class MapPointByDistance : IComparer<MapPoint>
    {
        public int Compare(MapPoint a, MapPoint b)
        {
            var c = a.Distance2.CompareTo(b.Distance2);
            return c != 0 ? c : string.CompareOrdinal(a.Point.Key, b.Point.Key);
        }
    }

    private readonly record struct EmptyMarker(string Key, Vector3 Position, CoverageStatus Status, bool Hollow, bool Greyed, bool Pinned);

    private readonly record struct Target(bool Valid, string? Key, Vector3 Anchor, bool HeightKnown, string Name, string Status,
        uint Colour, string RangeText, float Meter, string Advice, string Respawn, string Photo, bool MoveCloser);
}

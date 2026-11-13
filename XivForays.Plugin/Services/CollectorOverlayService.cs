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
        rowCount = emptyCount = deathRowCount = 0;
        selected = default;
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
            rowCount = emptyCount = deathRowCount = 0;
            selected = default;
            return;
        }

        if (features.Conditions)
            ReadConditions();
        BuildDeaths(now);
        BuildNearest();
        BuildSelected(now);
        BuildConfirmations(now);
    }

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

    private ConditionState ConditionOf(CoveragePoint point)
    {
        if (!features.Conditions)
            return ConditionState.Unknown;
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
            if (want > 0)
                Insert(point, MathF.Sqrt(d2), want);
            if (features.EmptyPoints && emptyCount < MaxEmpty && d2 <= display2 && point.HeightKnown && !session.IsOccupied(point))
            {
                var greyed = ConditionOf(point) == ConditionState.Unavailable;
                empty[emptyCount++] = new EmptyMarker(new Vector3(point.X, point.Y, point.Z), status,
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
            row.Timer = death.FateBound ? "FATE enemy: no timer" : RespawnText.Text(RespawnText.Remaining(death.DiedAtMs, now));
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
            flashUntilMs = last.AtMs + OverlayRates.ConfirmFlashMs;
            flashLabel = last.NoLayout ? "Spawn watched locally; server match uncertain" :
                last.Provisional ? "Confirmed locally (new point)" : "Confirmed locally";
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
        }
        catch (Exception ex)
        {
            log.Warning($"Collector overlay draw failed ({ex.GetType().Name}).");
        }
    }

    private void DrawWindow()
    {
        var listOrRange = features.NearbyList || features.Range || features.Respawn || features.LocalConfirm;
        if (!listOrRange && statusLine.Length == 0)
            return;
        ImGui.SetNextWindowSize(new Vector2(380, 300), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Collector overlay##XivForaysCollector", ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.End();
            return;
        }

        if (statusLine.Length > 0)
            ImGui.TextWrapped(statusLine);
        if (feedLine.Length > 0)
            ImGui.TextDisabled(feedLine);
        if (ImGui.SmallButton("Hide overlay"))
        {
            plugin.Configuration.OverlayHidden = true;
            plugin.Configuration.Save();
            ApplyConfig(plugin.Configuration);
        }

        if (support == ZoneSupport.Supported)
        {
            if (features.LocalConfirm)
            {
                if (Environment.TickCount64 < flashUntilMs)
                    ImGui.TextColored(Rgba(CoverageStatusMap.Colour(CoverageStatus.Confirmed)), flashLabel);
                if (confirmLine.Length > 0)
                    ImGui.TextDisabled(confirmLine);
            }

            if (features.Range && selected.Valid)
                DrawSelected();
            if (features.Respawn && deathRowCount > 0)
                DrawDeaths();
            if (features.NearbyList)
                DrawList();
        }

        ImGui.End();
    }

    private void DrawSelected()
    {
        ImGui.Separator();
        ImGui.TextColored(Rgba(selected.Colour), selected.Status);
        ImGui.SameLine();
        ImGui.TextUnformatted(selected.Name);
        ImGui.TextUnformatted(selected.RangeText);
        ImGui.ProgressBar(selected.Meter, new Vector2(-1, 0), "5 s nearby history");
        if (selected.MoveCloser)
            ImGui.TextColored(new Vector4(1f, 0.82f, 0.4f, 1f), selected.Advice);
        else
            ImGui.TextUnformatted(selected.Advice);
        if (selected.Respawn.Length > 0)
            ImGui.TextUnformatted(selected.Respawn);
        if (selected.Photo.Length > 0)
            ImGui.TextDisabled(selected.Photo);
        if (pinnedKey != null && ImGui.SmallButton("Unpin##XivForaysUnpin"))
            pinnedKey = null;
    }

    private void DrawDeaths()
    {
        ImGui.Separator();
        for (var i = 0; i < deathRowCount; i++)
        {
            var row = deathRows[i];
            ImGui.TextUnformatted(row.Who);
            ImGui.SameLine();
            ImGui.TextUnformatted(row.Name);
            ImGui.SameLine();
            ImGui.TextUnformatted(row.Timer);
            ImGui.SameLine();
            ImGui.TextDisabled(row.Distance);
            if (row.Lost)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.82f, 0.4f, 1f), "continuity lost");
            }
        }
    }

    private void DrawList()
    {
        ImGui.Separator();
        if (session.Snapshot == null)
        {
            ImGui.TextDisabled("No coverage yet.");
            return;
        }

        if (rowCount == 0)
        {
            ImGui.TextDisabled("Nothing needed nearby.");
            return;
        }

        if (!ImGui.BeginTable("##XivForaysNearby", features.Conditions ? 4 : 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        for (var i = 0; i < rowCount; i++)
        {
            var row = rows[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var pinned = row.Key == pinnedKey;
            ImGui.PushID(i);
            if (ImGui.Selectable(row.Name, pinned, ImGuiSelectableFlags.SpanAllColumns))
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
        ImGui.TextDisabled("Click a row to pin it as the watch point.");
    }

    private void DrawWorld(long now)
    {
        var list = ImGui.GetBackgroundDrawList();
        var scale = features.MarkerScale;
        var alpha = features.Opacity;
        var display2 = features.DisplayYalms * features.DisplayYalms;
        var budget = features.MaxMarkers;
        var liveBudget = budget / 2;

        if (features.Rings && selected.Valid && selected.HeightKnown)
        {
            DrawRing(list, selected.Anchor, OverlayRates.WatchYalms, Colour(0xFFFFB768u, alpha * 0.8f), "80 y watch");
            DrawRing(list, selected.Anchor, OverlayRates.PhotoYalms, Colour(0xFF66D1FFu, alpha * 0.8f), "70 y photo");
        }

        if (features.LocalConfirm && now < flashUntilMs && budget > 0)
        {
            if (Label(list, flashAnchor + new Vector3(0, 3f, 0), flashLabel, Colour(CoverageStatusMap.Colour(CoverageStatus.Confirmed), 1f), scale * 1.2f, Shape.Check, false))
                budget--;
        }

        if (features.Respawn)
            for (var i = 0; i < deathRowCount && budget > 0; i++)
            {
                var row = deathRows[i];
                if (Vector3.DistanceSquared(row.Anchor, player) > display2)
                    continue;
                if (Label(list, row.Anchor + new Vector3(0, 2f, 0), row.Timer, Colour(row.DefeatColour, alpha), scale, Shape.Clock, false))
                    budget--;
            }

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

        if (features.EmptyPoints)
            for (var i = 0; i < emptyCount && budget > 0; i++)
            {
                var marker = empty[i];
                var colour = Colour(CoverageStatusMap.Colour(marker.Status), marker.Greyed ? alpha * 0.4f : alpha);
                if (Label(list, marker.Position + new Vector3(0, 0.5f, 0), CoverageStatusMap.Label(marker.Status), colour,
                        marker.Pinned ? scale * 1.2f : scale, ShapeOf(marker.Status), marker.Hollow))
                    budget--;
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
        public string Distance = "";
        public bool Lost;
        public bool Approximate;
        public bool FateBound;
        public uint DefeatColour => Who == "Your kill" ? 0xFFB0D64Du : 0xFFD0D0D0u;
    }

    private readonly record struct EmptyMarker(Vector3 Position, CoverageStatus Status, bool Hollow, bool Greyed, bool Pinned);

    private readonly record struct Target(bool Valid, string? Key, Vector3 Anchor, bool HeightKnown, string Name, string Status,
        uint Colour, string RangeText, float Meter, string Advice, string Respawn, string Photo, bool MoveCloser);
}

using System.Linq;
using Dalamud.Bindings.ImGui;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Gathering.Fate;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Windows.Tabs;

/// <summary>
/// Tab for FATE tracking configuration and status display
/// </summary>
internal class GeneralTab(
    Plugin plugin,
    FateModule fateModule,
    CollectorOverlayService collectorOverlay) : ITab
{
    /// <inheritdoc />
    public int Index => 1;

    /// <inheritdoc />
    public string TabTitle => "General";

    /// <inheritdoc />
    public void Draw(Configuration.Configuration configuration)
    {
        var canCrowdsourceData = configuration.CanCrowdsourceData;
        if (ImGui.Checkbox("Crowdsource data##CrowdsourceData", ref canCrowdsourceData))
        {
            configuration.CanCrowdsourceData = canCrowdsourceData;
            configuration.Save();
            plugin.ReloadModules();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Record in");
        var changed = false;
        var recordEureka = configuration.RecordEureka;
        if (ImGui.Checkbox("Eureka##RecordEureka", ref recordEureka))
        {
            configuration.RecordEureka = recordEureka;
            changed = true;
        }

        var recordBozja = configuration.RecordBozja;
        if (ImGui.Checkbox("Bozja (incl. Zadnor, Delubrum)##RecordBozja", ref recordBozja))
        {
            configuration.RecordBozja = recordBozja;
            changed = true;
        }

        var recordOccultCrescent = configuration.RecordOccultCrescent;
        if (ImGui.Checkbox("Occult Crescent##RecordOccultCrescent", ref recordOccultCrescent))
        {
            configuration.RecordOccultCrescent = recordOccultCrescent;
            changed = true;
        }

        // The zone gate reads these live, so no module reload is needed.
        if (changed)
            configuration.Save();

        if (configuration.RecordsNothing)
            ImGui.TextUnformatted("All areas are unticked: nothing will be recorded.");

        ImGui.Spacing();
        DrawCollectorOverlay(configuration);

        ImGui.Spacing();
        if (!configuration.CanCrowdsourceData)
        {
            return;
        }

        var fateCount = fateModule.ActiveFates.Count();
        ImGui.TextUnformatted($"Fates on map: {fateCount}");
    }

    /// <summary>
    /// The optional collector overlay. Everything is off by default so each part can be turned on and tested on its
    /// own. Off = no downloads, no window, no drawing. The overlay only reads; it never changes what is recorded.
    /// </summary>
    private void DrawCollectorOverlay(Configuration.Configuration configuration)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Collector overlay");
        var changed = Box("Enable collector overlay##OverlayEnabled", configuration.OverlayEnabled, v => configuration.OverlayEnabled = v,
            "Downloads the public coverage of your current Eureka zone (nothing is sent) and shows the parts ticked below. Off: no downloads, no extra work.");
        if (!configuration.OverlayEnabled)
        {
            if (changed)
                Apply(configuration);
            return;
        }

        ImGui.Indent();
        changed |= Box("1. Nearby needed list##OverlayNearbyList", configuration.OverlayNearbyList, v => configuration.OverlayNearbyList = v,
            "A small window with the nearest spawn points that still need a confirmation: distance, compass direction; click a row to pin it as the watch point.");
        changed |= Box("2. Respawn countdown##OverlayRespawn", configuration.OverlayRespawn, v => configuration.OverlayRespawn = v,
            "Approximate countdown (~50 s) on the spots of enemies that just died, your own kills first. At zero: Awaiting spawn.");
        changed |= Box("3. Range and readiness##OverlayRange", configuration.OverlayRange, v => configuration.OverlayRange = v,
            "For the selected or pinned point: distance against the 80 y watch range, the 5 s history meter, Move closer, and continuity lost beyond 100 y.");
        changed |= Box("4. Confirmed locally feedback##OverlayLocalConfirm", configuration.OverlayLocalConfirm, v => configuration.OverlayLocalConfirm = v,
            "Shows Confirmed locally the moment a clean spawn is seen (the same rule the server uses), then upload pending until the public coverage agrees.");
        changed |= Box("5. Status markers over visible enemies##OverlayEnemyMarkers", configuration.OverlayEnemyMarkers, v => configuration.OverlayEnemyMarkers = v,
            "Colour, word and shape over each loaded enemy: To confirm, Walk-past, Never seen (and Confirmed if ticked below).");
        if (configuration.OverlayEnemyMarkers)
        {
            ImGui.Indent();
            changed |= Box("Show confirmed##OverlayShowConfirmed", configuration.OverlayShowConfirmed, v => configuration.OverlayShowConfirmed = v,
                "Also mark enemies whose spawn point is already Confirmed.");
            ImGui.Unindent();
        }

        changed |= Box("6. Markers on empty needed points##OverlayEmptyPoints", configuration.OverlayEmptyPoints, v => configuration.OverlayEmptyPoints = v,
            "Markers where a needed spawn point is not currently occupied. Approximate (walk-past) points are hollow; points with an uncertain height stay in the list only.");
        changed |= Box("7. Ground circles##OverlayRings", configuration.OverlayRings, v => configuration.OverlayRings = v,
            "80 y watch and 70 y photo circles around the selected point only (approximate guide; the numbers in the window decide).");
        changed |= Box("8. Spawn conditions##OverlayConditions", configuration.OverlayConditions, v => configuration.OverlayConditions = v,
            "Shows the condition text (weather, night, FATE) and greys a point when the condition is known to be unmet right now.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Options");
        ImGui.SetNextItemWidth(200);
        var distance = configuration.OverlayDisplayYalms;
        if (ImGui.SliderFloat("Display distance (y)##OverlayDisplay", ref distance, 20f, OverlayRates.MaxDisplayYalms, "%.0f"))
        {
            configuration.OverlayDisplayYalms = distance;
            changed = true;
        }

        ImGui.SetNextItemWidth(200);
        var scale = configuration.OverlayMarkerScale;
        if (ImGui.SliderFloat("Marker scale##OverlayScale", ref scale, 0.5f, 2f, "%.1f"))
        {
            configuration.OverlayMarkerScale = scale;
            changed = true;
        }

        ImGui.SetNextItemWidth(200);
        var opacity = configuration.OverlayOpacity;
        if (ImGui.SliderFloat("Marker opacity##OverlayOpacity", ref opacity, 0.2f, 1f, "%.2f"))
        {
            configuration.OverlayOpacity = opacity;
            changed = true;
        }

        ImGui.SetNextItemWidth(200);
        var maxMarkers = configuration.OverlayMaxMarkers;
        if (ImGui.SliderInt("Max markers##OverlayMaxMarkers", ref maxMarkers, 8, OverlayRates.DefaultMaxMarkers))
        {
            configuration.OverlayMaxMarkers = maxMarkers;
            changed = true;
        }

        ImGui.SetNextItemWidth(200);
        var listRows = configuration.OverlayListRows;
        if (ImGui.SliderInt("List rows##OverlayListRows", ref listRows, OverlayRates.MinListRows, OverlayRates.MaxListRows))
        {
            configuration.OverlayListRows = listRows;
            changed = true;
        }

        changed |= Box("Hide while in combat##OverlayHideInCombat", configuration.OverlayHideInCombat, v => configuration.OverlayHideInCombat = v,
            "Hides the window and the markers while you are in combat.");
        changed |= Box("Hide overlay##OverlayHidden", configuration.OverlayHidden, v => configuration.OverlayHidden = v,
            "Quickly hides the window and all markers without changing the other settings (recording is not affected).");
        ImGui.Unindent();

        if (changed)
            Apply(configuration);
    }

    private void Apply(Configuration.Configuration configuration)
    {
        configuration.Save();
        collectorOverlay.ApplyConfig(configuration);
    }

    private static bool Box(string label, bool value, System.Action<bool> set, string tooltip)
    {
        var current = value;
        var changed = ImGui.Checkbox(label, ref current);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
            ImGui.TextUnformatted(tooltip);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        if (changed)
            set(current);
        return changed;
    }
}

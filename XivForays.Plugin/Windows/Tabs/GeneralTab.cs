using System.Linq;
using Dalamud.Bindings.ImGui;
using XivMate.DataGathering.Forays.Dalamud.Gathering.Fate;

namespace XivMate.DataGathering.Forays.Dalamud.Windows.Tabs;

/// <summary>
/// Tab for FATE tracking configuration and status display
/// </summary>
internal class GeneralTab(
    Plugin plugin,
    FateModule fateModule) : ITab
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
        if (!configuration.CanCrowdsourceData)
        {
            return;
        }

        var fateCount = fateModule.ActiveFates.Count();
        ImGui.TextUnformatted($"Fates on map: {fateCount}");
    }
}

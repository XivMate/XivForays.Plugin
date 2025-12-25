using System.Linq;
using Dalamud.Bindings.ImGui;
using XivMate.DataGathering.Forays.Dalamud.Gathering.Enemy;
using XivMate.DataGathering.Forays.Dalamud.Gathering.Fate;

namespace XivMate.DataGathering.Forays.Dalamud.Windows.Tabs;

/// <summary>
/// Tab for FATE tracking configuration and status display
/// </summary>
public class GeneralTab(
    Plugin plugin,
    FateModule fateModule,
    EnemyLocationGatherer enemyLocationGatherer) : ITab
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

        if (!configuration.CanCrowdsourceData)
        {
            return;
        }

        var fateCount = fateModule.ActiveFates.Count();
        ImGui.TextUnformatted($"Fates on map: {fateCount}##fatecount");
    }
}

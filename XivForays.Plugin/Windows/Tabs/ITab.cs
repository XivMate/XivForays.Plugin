using Dalamud.Bindings.ImGui;

namespace XivMate.DataGathering.Forays.Dalamud.Windows.Tabs;

/// <summary>
/// Interface for UI tab components
/// </summary>
public interface ITab
{
    /// <summary>
    /// The index determining tab order
    /// </summary>
    int Index { get; }

    /// <summary>
    /// The title shown in the tab
    /// </summary>
    string TabTitle { get; }

    /// <summary>
    /// Draws the tab and handles ImGui tab wrapping
    /// </summary>
    /// <param name="configuration">Plugin configuration</param>
    public void DrawTab(Configuration.Configuration configuration)
    {
        if (ImGui.BeginTabItem($"{TabTitle}###{Index}"))
        {
            Draw(configuration);
            ImGui.EndTabItem();
        }
    }

    /// <summary>
    /// Internal implementation for drawing tab content
    /// </summary>
    /// <param name="configuration">Plugin configuration</param>
    void Draw(Configuration.Configuration configuration);
}

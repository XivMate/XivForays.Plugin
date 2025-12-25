using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using XivMate.DataGathering.Forays.Dalamud.Windows.Tabs;

namespace XivMate.DataGathering.Forays.Dalamud.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly IPluginLog log;
    private readonly IEnumerable<ITab> tabs;
    private Configuration.Configuration Configuration;

    public ConfigWindow(Plugin plugin, IEnumerable<ITab> tabs, IPluginLog log) :
        base("XivForays Settings")
    {
        this.log = log;
        this.tabs = tabs.OrderBy(t => t.Index).ToList();

        SizeCondition = ImGuiCond.Always;
        Configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.BeginTabBar("#tabs");
        foreach (var tab in tabs)
        {
            tab.DrawTab(Configuration);
        }

        ImGui.EndTabBar();
    }
}

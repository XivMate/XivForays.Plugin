using System;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Windows;

public class MainWindow : Window, IDisposable
{
    private Plugin Plugin;
    private readonly TerritoryService territoryService;
    private readonly ForayService forayService;

    public MainWindow(Plugin plugin, TerritoryService territoryService, ForayService forayService)
        : base("XivForays")
    {
        Plugin = plugin;
        Size = new Vector2(600, 250);
        this.territoryService = territoryService;
        this.forayService = forayService;
    }


    public void Dispose() { }

    public override void Draw()
    {
        // Do not use .Text() or any other formatted function like TextWrapped(), or SetTooltip().
        // These expect formatting parameter if any part of the text contains a "%", which we can't
        // provide through our bindings, leading to a Crash to Desktop.
        // Replacements can be found in the ImGuiHelpers Class

        if (ImGui.Button("Show Settings"))
        {
            Plugin.ToggleConfigUI();
        }

        ImGui.Spacing();

        // Normally a BeginChild() would have to be followed by an unconditional EndChild(),
        // ImRaii takes care of this after the scope ends.
        // This works for all ImGui functions that require specific handling, examples are BeginTable() or Indent().
        using (var child = ImRaii.Child("SomeChildWithAScrollbar", Vector2.Zero, true))
        {
            if (child.Success)
            {

                var localPlayer = Plugin.ObjectTable.LocalPlayer;
                if (localPlayer == null)
                {
                    ImGui.TextUnformatted("Our local player is currently not loaded.");
                    return;
                }

                if (!localPlayer.ClassJob.IsValid)
                {
                    ImGui.TextUnformatted("Our current job is currently not valid.");
                    return;
                }

                // ExtractText() should be the preferred method to read Lumina SeStrings,
                // as ToString does not provide the actual text values, instead gives an encoded macro string.
                ImGui.TextUnformatted(
                    $"Our current job is ({localPlayer.ClassJob.RowId}) \"{localPlayer.ClassJob.Value.Abbreviation.ExtractText()}\"");

                // Example for quarrying Lumina directly, getting the name of our current area.
                var territoryId = Plugin.ClientState.TerritoryType;
                if (Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territoryRow))
                {
                    ImGui.TextUnformatted(
                        $"We are currently in ({territoryId}) \"{territoryRow.PlaceName.Value.Name.ExtractText()}\" - default territory map: {territoryRow.Map.RowId}");
                    
                }
                else
                {
                    ImGui.TextUnformatted("Invalid territory.");
                }
                if(Plugin.DataManager.GetExcelSheet<MapType>().TryGetRow(Plugin.ClientState.MapId, out var mapRow))
                {
                    ImGui.TextUnformatted(
                        $"(Map {mapRow.RowId})");
                }
                else
                {
                    ImGui.TextUnformatted($"Invalid map. ({Plugin.ClientState.MapId})");
                }
                var isInForay = forayService.IsInRecordableTerritory();
                ImGui.TextUnformatted($"Is in recordable territory: {isInForay}");
            }
        }
    }
}

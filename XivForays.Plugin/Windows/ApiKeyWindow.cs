using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XivMate.DataGathering.Forays.Dalamud.Windows;

public sealed class ApiKeyWindow : Window
{
    private readonly Plugin plugin;
    private string key;
    private bool allowCrowdsourcing;
    private string error = "";

    public ApiKeyWindow(Plugin plugin) : base("XivForays API Key")
    {
        this.plugin = plugin;
        Size = new Vector2(600, 250);
        key = plugin.Configuration.SystemConfiguration.ApiKey;
        allowCrowdsourcing = plugin.Configuration.CanCrowdsourceData;
    }

    public override void Draw()
    {
        ImGui.Checkbox("Enable crowdsourcing", ref allowCrowdsourcing);
        ImGui.InputText("API key", ref key, 64, ImGuiInputTextFlags.Password);
        if (ImGui.Button("Save"))
        {
            var trimmedKey = key.Trim();
            if ((allowCrowdsourcing || trimmedKey.Length > 0) && !Guid.TryParse(trimmedKey, out _))
            {
                error = "Enter the API key you received from the Forays website.";
                return;
            }

            var configuration = plugin.Configuration;
            configuration.SystemConfiguration.ApiKey = trimmedKey;
            configuration.CanCrowdsourceData = allowCrowdsourcing;
            configuration.Save();
            plugin.ReloadModules();
            Toggle();
        }

        if (error.Length > 0)
            ImGui.TextUnformatted(error);
    }
}

using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace XivMate.DataGathering.Forays.Dalamud.Windows;

public class ApiKeyWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly IPluginLog log;
    private readonly Configuration.Configuration configuration;

    public ApiKeyWindow(Plugin plugin, IPluginLog log) :
        base("XivForays API Key")
    {
        this.plugin = plugin;
        this.log = log;
        configuration = plugin.Configuration;
        Size = new Vector2(600, 250);
        key = configuration.SystemConfiguration.ApiKey;
        canCrowdSourceData = configuration.CanCrowdsourceData;
    }

    public void Dispose() { }
    private string key;
    private bool canCrowdSourceData;

    public override void Draw()
    {
        if (ImGui.Checkbox("Enable Crowdsourcing data", ref canCrowdSourceData))
        {
            log.Debug($"CanCrowdsourceData updated to {canCrowdSourceData}");
        }

        if (ImGui.InputText("Api Key##ApiKey", ref key))
        {
            key = key.Trim();
            log.Debug($"ApiKey updated to {key}");
        }

        if (ImGui.Button("Save"))
        {
            configuration.SystemConfiguration.ApiKey = key;
            log.Debug($"Saving API Key: '{configuration.SystemConfiguration.ApiKey}'");
            configuration.CanCrowdsourceData = canCrowdSourceData;
            log.Debug($"Saving CanCrowdsourceData: '{configuration.CanCrowdsourceData}'");
            configuration.Save();
            Toggle();
            plugin.ReloadModules();
            log.Info("Saved configuration");
        }
    }
}

using System;
using Dalamud.Bindings.ImGui;

namespace XivMate.DataGathering.Forays.Dalamud.Windows.Tabs;

public sealed class SystemTab(Plugin plugin) : ITab
{
    private string apiUrl = plugin.Configuration.SystemConfiguration.ApiUrl;
    private string apiKey = plugin.Configuration.SystemConfiguration.ApiKey;
    private string message = "";

    public int Index => 999;
    public string TabTitle => "System";

    public void Draw(Configuration.Configuration configuration)
    {
        ImGui.InputText("API URL", ref apiUrl, 512);
        ImGui.InputText("API key", ref apiKey, 64, ImGuiInputTextFlags.Password);

        if (ImGui.Button("Save"))
        {
            var system = configuration.SystemConfiguration;
            var url = apiUrl.Trim();
            var key = apiKey.Trim();
            if (key.Length != 0 && !Guid.TryParse(key, out _))
            {
                message = "Enter a valid Forays API key.";
                return;
            }

            if (url != system.ApiUrl || key != system.ApiKey)
            {
                configuration.CanCrowdsourceData = false;
                plugin.ReloadModules();
                system.ApiUrl = url;
                system.ApiKey = key;
                configuration.Save();
                message = "Saved. Enable crowdsourcing again in General to use these settings.";
            }
        }

        if (message.Length > 0)
            ImGui.TextUnformatted(message);
    }
}

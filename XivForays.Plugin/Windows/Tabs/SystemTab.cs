using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using XivMate.DataGathering.Forays.Dalamud.Extensions;

namespace XivMate.DataGathering.Forays.Dalamud.Windows.Tabs;

/// <summary>
/// Tab for system configuration settings
/// </summary>
public class SystemTab : ITab
{
    // Global fields to store input values
    private string _apiUrl = "";
    private string _apiKey = "";

    // Characters to trim from input fields
    private readonly char[] _charsToTrim = new char[]
    {
        '\0', // Null character
        ' ',  // Space
        '\t', // Tab
        '\n', // Newline
        '\r', // Carriage return
        '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007',
        '\u0008', '\u000b', '\u000c', '\u000e', '\u000f', '\u0010', '\u0011',
        '\u0012', '\u0013', '\u0014', '\u0015', '\u0016', '\u0017', '\u0018',
        '\u0019', '\u001a', '\u001b', '\u001c', '\u001d', '\u001e', '\u001f',
        '\u007f' // DEL character
    };

    private readonly IPluginLog logger;

    /// <summary>
    /// Tab for system configuration settings
    /// </summary>
    public SystemTab(IPluginLog logger, Plugin plugin)
    {
        var config = plugin.Configuration;
        this.logger = logger;
        // Initialize input fields from configuration if not done yet
        _apiUrl = config.SystemConfiguration.ApiUrl ?? "";
        _apiKey = config.SystemConfiguration.ApiKey ?? "";
    }

    /// <inheritdoc />
    public int Index => 999;

    /// <inheritdoc />
    public string TabTitle => "System";

    /// <inheritdoc />
    public void Draw(Configuration.Configuration configuration)
    {
        var sysConfig = configuration.SystemConfiguration;

        // Display input fields
        ImGuiHelper.InputText("Api URL", ref _apiUrl, 512U);
        ImGuiHelper.InputText("Api Key", ref _apiKey, 512U);

        // Add save button
        if (ImGui.Button("Save"))
        {
            // Trim the defined set of potentially problematic trailing characters
            var cleanedApiUrl = _apiUrl.TrimEnd(_charsToTrim);
            var cleanedApiKey = _apiKey.TrimEnd(_charsToTrim);

            // Update configuration
            sysConfig.ApiUrl = cleanedApiUrl;
            sysConfig.ApiKey = cleanedApiKey;

            // Log changes
            logger.Debug($"Setting API URL to {cleanedApiUrl}");
            logger.Debug($"Setting API Key to {cleanedApiKey}");
            logger.Info("Saved settings");

            // Save configuration
            configuration.Save();
        }
    }
}

using System;
using Dalamud.Configuration;

namespace XivMate.DataGathering.Forays.Dalamud.Configuration;

/// <summary>
/// Main plugin configuration
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>
    /// Configuration version
    /// </summary>
    public int Version { get; set; } = 0;

    /// <summary>
    /// System-related configuration
    /// </summary>
    public SystemConfiguration SystemConfiguration { get; set; } = new();

    public bool CanCrowdsourceData { get; set; } = false;

    /// <summary>Pose capture pass: also record a 60 s idle track (kind 17) after each spawn pose. Off by default.</summary>
    public bool CapturePass { get; set; } = false;
    
    /// <summary>
    /// Saves the configuration
    /// </summary>
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

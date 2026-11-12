using System;
using Dalamud.Configuration;

namespace XivMate.DataGathering.Forays.Dalamud.Configuration;

/// <summary>
/// Main plugin configuration
/// </summary>
[Serializable]
public partial class Configuration : IPluginConfiguration
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
    /// The legacy "enemyposition" upload (a 15 s snapshot of visible enemies WITH monster names, a separate route).
    /// Off by default since 0.0.0.13: the observations stream carries the same data without strings.
    /// </summary>
    public bool LegacyEnemySnapshots { get; set; } = false;
    
    /// <summary>
    /// Saves the configuration
    /// </summary>
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

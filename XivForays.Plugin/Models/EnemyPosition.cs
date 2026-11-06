using System;

namespace XivMate.DataGathering.Forays.Dalamud.Models;

/// <summary>
/// Represents position and state data for an enemy in Foray content
/// </summary>
public class EnemyPosition
{
    /// <summary>
    /// The in-game ID of the mob
    /// </summary>
    public uint MobIngameId { get; set; }

    /// <summary>
    /// The name of the mob
    /// </summary>
    public string MobName { get; set; } = string.Empty;

    /// <summary>
    /// The level of the mob
    /// </summary>
    public byte Level { get; set; }

    /// <summary>
    /// X position coordinate
    /// </summary>
    public float X { get; set; }

    /// <summary>
    /// Y position coordinate
    /// </summary>
    public float Y { get; set; }

    /// <summary>
    /// Z position coordinate
    /// </summary>
    public float Z { get; set; }

    /// <summary>
    /// Whether the mob has the Adaptation status
    /// </summary>
    public bool IsAdapted { get; set; }

    /// <summary>
    /// Whether the mob has the Mutation status
    /// </summary>
    public bool IsMutated { get; set; }

    /// <summary>
    /// Unix timestamp when this position was recorded
    /// </summary>
    public long TimeStamp { get; set; }

    /// <summary>
    /// The element of the mob if applicable (e.g., in Eureka content)
    /// </summary>
    public string Element { get; set; } = string.Empty;

    /// <summary>
    /// Whether the mob is currently in combat
    /// </summary>
    public bool IsInCombat { get; set; }

    /// <summary>
    /// Whether the mob has ever been observed in combat
    /// </summary>
    public bool HasBeenInCombat { get; set; }

    /// <summary>
    /// Territory ID where the mob was found
    /// </summary>
    public int TerritoryId { get; set; }

    /// <summary>
    /// Map ID where the mob was found
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Instance ID for tracking purposes
    /// </summary>
    public Guid InstanceId { get; set; }
}

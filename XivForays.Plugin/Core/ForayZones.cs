using System.Collections.Generic;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>
/// The recording zone gate, by TerritoryType row ID so it works in every client language.
/// IDs are checked against the game's TerritoryType and Map sheets by
/// XivForays.Plugin.Tests/GameSheetTerritoryTests.cs (needs a local game install).
/// Dependency-free so it can be unit tested without Dalamud.
/// </summary>
internal static class ForayZones
{
    // Eureka: Anemos, Pagos, Pyros, Hydatos. The Baldesion Arsenal is map layers 520-527 of Hydatos (827).
    // Eureka Orthos (1099-1108, 1124) is a deep dungeon and deliberately not included.
    public const uint EurekaAnemos = 732;
    public const uint EurekaPagos = 763;
    public const uint EurekaPyros = 795;
    public const uint EurekaHydatos = 827;

    // Save the Queen: Bozjan Southern Front (incl. Castrum Lacus Litore map 616), Zadnor
    // (the Dalriada is map layers 673-675 of Zadnor), Delubrum Reginae normal and Savage.
    public const uint BozjanSouthernFront = 920;
    public const uint Zadnor = 975;
    public const uint DelubrumReginae = 936;
    public const uint DelubrumReginaeSavage = 937;

    // Occult Crescent. The Forked Tower is map layers of each Horn (Tower of Blood in 1252, Tower of Magic in 1346).
    public const uint SouthHorn = 1252;
    public const uint NorthHorn = 1346;

    public static IReadOnlySet<uint> All { get; } = new HashSet<uint>
    {
        EurekaAnemos, EurekaPagos, EurekaPyros, EurekaHydatos,
        BozjanSouthernFront, Zadnor, DelubrumReginae, DelubrumReginaeSavage,
        SouthHorn, NorthHorn
    };

    public static bool IsForayTerritory(uint territory) => All.Contains(territory);

    public static bool IsEureka(uint territory) =>
        territory is EurekaAnemos or EurekaPagos or EurekaPyros or EurekaHydatos;

    public static bool IsBozja(uint territory) =>
        territory is BozjanSouthernFront or Zadnor or DelubrumReginae or DelubrumReginaeSavage;

    public static bool IsOccultCrescent(uint territory) => territory is SouthHorn or NorthHorn;

    /// <summary>
    /// The per-content recording toggle for <paramref name="territory"/>: Eureka (the 4 Eureka zones), Bozja (Southern
    /// Front, Zadnor, Delubrum Reginae and Savage) or Occult Crescent (South and North Horn). False outside the gate.
    /// </summary>
    public static bool IsContentEnabled(uint territory, bool eureka, bool bozja, bool occultCrescent) =>
        IsEureka(territory) ? eureka :
        IsBozja(territory) ? bozja :
        IsOccultCrescent(territory) && occultCrescent;

    /// <summary>Zones whose critical engagements come from a DynamicEventContainer (Bozja field zones and the Horns).</summary>
    public static bool HasCriticalEngagements(uint territory) =>
        territory is BozjanSouthernFront or Zadnor || IsOccultCrescent(territory);

    /// <summary>The territory that owns the Map rows used inside <paramref name="territory"/>.</summary>
    /// <remarks>Delubrum Reginae (Savage) reuses the normal mode's map rows, whose TerritoryType is 936.</remarks>
    public static uint MapOwner(uint territory) =>
        territory == DelubrumReginaeSavage ? DelubrumReginae : territory;

    /// <summary>
    /// True when the player is in a gated zone on any map layer that belongs to it (default map,
    /// BA, Dalriada, Castrum Lacus Litore, Delubrum floors, Forked Tower ...). Map ID 0 means the
    /// client has not settled the map yet (loading), which is never recorded.
    /// </summary>
    public static bool IsRecordable(uint territory, uint mapId, uint mapTerritory) =>
        IsForayTerritory(territory) && mapId != 0 && mapTerritory == MapOwner(territory);
}

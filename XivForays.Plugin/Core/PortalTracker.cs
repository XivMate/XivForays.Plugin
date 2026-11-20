using System;
using System.Collections.Generic;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>One Baldesion Arsenal portal object as seen in the object table.</summary>
internal readonly record struct PortalSnapshot(ulong ObjectId, uint BaseId, float X, float Y, float Z);

/// <summary>
/// Kind 21 portal records (Hydatos field map): an "Unstable Aetherial Node" (blue, event object 2009726) or a
/// "Stable Aetherial Node" (red, 2009727) entering the object table. One appearance record per object in a visit, and a
/// "gone" record when it has been out of the table for <see cref="GoneGraceMs"/>. Dependency-free for tests.
/// </summary>
internal sealed class PortalTracker
{
    public const uint BlueBaseId = 2009726;
    public const uint RedBaseId = 2009727;
    public const uint FieldMapId = 515;
    public const long GoneGraceMs = 7_500;

    /// <summary>An object that comes back at the same spot this close (yalms) is the same appearance.</summary>
    private const float SameSpot = 1f;

    private sealed class Entry
    {
        public Guid TrackId;
        public uint BaseId;
        public float X, Y, Z;
        public long FirstSeenMs;
        public long LastSeenMs;
        public bool Active;
    }

    private readonly Dictionary<ulong, Entry> entries = new();
    private readonly HashSet<ulong> seen = new();
    private Guid visit;

    /// <summary>True when nothing is tracked, so a scan outside the field map has nothing to end.</summary>
    public bool IsIdle => entries.Count == 0;

    public static bool IsPortal(uint baseId) => baseId is BlueBaseId or RedBaseId;

    /// <summary>1 = blue (unstable), 2 = red (stable).</summary>
    public static byte Colour(uint baseId) => baseId == BlueBaseId ? (byte)1 : (byte)2;

    public static bool InScope(uint territory, uint map) => territory == ForayZones.EurekaHydatos && map == FieldMapId;

    public void Clear()
    {
        entries.Clear();
        visit = Guid.Empty;
    }

    /// <summary>Compares the current table with the last scan. <paramref name="context"/> carries time, visit, zone and weather.</summary>
    public void Sample(Observation context, IEnumerable<PortalSnapshot> table, Action<Observation> emit)
    {
        if (visit != context.VisitId)
        {
            entries.Clear();
            visit = context.VisitId;
        }

        var now = context.ObservedAtMs;
        var inScope = InScope(context.TerritoryId, context.MapId);
        seen.Clear();
        if (inScope)
        {
            foreach (var snap in table)
            {
                if (!IsPortal(snap.BaseId) || !seen.Add(snap.ObjectId))
                    continue;
                if (entries.TryGetValue(snap.ObjectId, out var entry) && entry.BaseId == snap.BaseId &&
                    Near(entry, snap))
                {
                    entry.LastSeenMs = now;
                    entry.Active = true;
                    continue;
                }

                entry = new Entry
                {
                    TrackId = Guid.NewGuid(), BaseId = snap.BaseId, X = snap.X, Y = snap.Y, Z = snap.Z,
                    FirstSeenMs = now, LastSeenMs = now, Active = true
                };
                entries[snap.ObjectId] = entry;
                emit(Build(context, entry, present: true));
            }
        }

        foreach (var (id, entry) in entries)
            if (!seen.Contains(id))
                Gone(context, entry, emit, force: !inScope);
    }

    private static void Gone(Observation context, Entry entry, Action<Observation> emit, bool force)
    {
        if (!entry.Active)
            return;
        if (!force && context.ObservedAtMs - entry.LastSeenMs < GoneGraceMs)
            return;
        entry.Active = false;
        emit(Build(context with { ObservedAtMs = entry.LastSeenMs }, entry, present: false));
    }

    private static bool Near(Entry entry, PortalSnapshot snap)
    {
        float dx = entry.X - snap.X, dy = entry.Y - snap.Y, dz = entry.Z - snap.Z;
        return dx * dx + dy * dy + dz * dz <= SameSpot * SameSpot;
    }

    private static Observation Build(Observation context, Entry entry, bool present) => context with
    {
        Kind = ObservationKind.Portal,
        GameId = entry.BaseId,
        ObjectTrackId = entry.TrackId,
        PortalColour = Colour(entry.BaseId),
        State = present ? (byte)1 : (byte)0,
        X = entry.X,
        Y = entry.Y,
        Z = entry.Z,
        VisibleMs = present ? null : (uint)Math.Min(uint.MaxValue, Math.Max(0, entry.LastSeenMs - entry.FirstSeenMs))
    };
}

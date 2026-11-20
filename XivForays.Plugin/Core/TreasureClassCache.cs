using System;
using System.Collections.Generic;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>
/// Remembers what each object kind and base id classified as, so the sheet lookup and name text happen once per kind of
/// object instead of once per object per scan. The answer for a base id never changes within a game session.
/// </summary>
internal sealed class TreasureClassCache
{
    private readonly Dictionary<(int Kind, uint Id), (byte Kind, byte? Grade)> known = new();

    public (byte Kind, byte? Grade) Get(int objectKind, uint id, Func<(byte Kind, byte? Grade)> classify)
    {
        if (!known.TryGetValue((objectKind, id), out var result))
            known[(objectKind, id)] = result = classify();
        return result;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

// Collector overlay (0.0.0.16): the public per-zone coverage file (https://beta.xivforays.com/live/coverage-<zone>.json)
// as an immutable snapshot. Dependency-free so the status mapping and validation can be unit tested.

/// <summary>What the overlay says about one spawn point, from the coverage row (spawn, rank, exactBy).</summary>
internal enum CoverageStatus : byte
{
    /// <summary>A combination the overlay does not know. Never guessed into Confirmed.</summary>
    Unknown = 0,

    /// <summary>rank Confirmed (normally exactBy plugin-spawn): a plugin player saw it spawn.</summary>
    Confirmed = 1,

    /// <summary>spawn exact, rank Seen nearby (exactBy act-nearby): exact point, the plugin only saw it nearby.</summary>
    ToConfirm = 2,

    /// <summary>spawn approximate: the plugin saw it only walking past, so the position is approximate.</summary>
    WalkPast = 3,

    /// <summary>spawn unseen: an exact point the plugin never saw.</summary>
    NeverSeen = 4
}

/// <summary>Extra row ranks that qualify a status. They are never a confirmation.</summary>
[Flags]
internal enum CoverageQualifier : byte
{
    None = 0,
    Fate = 1,
    Doubtful = 2,
    NotReal = 4
}

/// <summary>One spawn point (one coverage row) of the current zone.</summary>
internal sealed class CoveragePoint
{
    public required string Key { get; init; }
    public uint BnpcId { get; init; }

    /// <summary>0 when the row has no layout id (common for points the plugin never saw).</summary>
    public uint LayoutId { get; init; }

    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }

    /// <summary>False when the row's height comes from a home position (unreliable): list only, no marker or ring.</summary>
    public bool HeightKnown { get; init; }

    public CoverageStatus Status { get; init; }
    public CoverageQualifier Qualifier { get; init; }

    /// <summary>The spawn point is known but a photo pose is still wanted (status partial / needs-pose).</summary>
    public bool PhotoNeeded { get; init; }

    /// <summary>English display text of when the enemy can be up ("any time", "night", "weather Snow" ...).</summary>
    public string Condition { get; init; } = "";

    /// <summary>The enemy name from the file's enemy table (game data), or "" when absent.</summary>
    public string Name { get; init; } = "";

    /// <summary>Still needs a spawn confirmation: everything but Confirmed (Duplicate rows are not loaded).</summary>
    public bool Needed => Status != CoverageStatus.Confirmed;
}

/// <summary>A validated coverage file of one zone. Never changed after it is built.</summary>
internal sealed class CoverageSnapshot
{
    private static readonly CoveragePoint[] NoPoints = [];

    public CoverageSnapshot(uint territory, string dataThrough, IReadOnlyList<CoveragePoint> points)
    {
        Territory = territory;
        DataThrough = dataThrough;
        Points = points;
        var byLayout = new Dictionary<uint, List<CoveragePoint>>();
        var byBnpc = new Dictionary<uint, List<CoveragePoint>>();
        var byKey = new Dictionary<string, CoveragePoint>(StringComparer.Ordinal);
        foreach (var point in points)
        {
            byKey[point.Key] = point;
            if (point.LayoutId != 0)
                Add(byLayout, point.LayoutId, point);
            Add(byBnpc, point.BnpcId, point);
        }

        ByLayout = Freeze(byLayout);
        ByBnpc = Freeze(byBnpc);
        ByKey = byKey;
    }

    public uint Territory { get; }

    /// <summary>The file's dataThrough (hour-rounded, newest input across zones), shown as is.</summary>
    public string DataThrough { get; }

    public IReadOnlyList<CoveragePoint> Points { get; }
    public IReadOnlyDictionary<uint, CoveragePoint[]> ByLayout { get; }
    public IReadOnlyDictionary<uint, CoveragePoint[]> ByBnpc { get; }
    public IReadOnlyDictionary<string, CoveragePoint> ByKey { get; }

    public CoveragePoint[] WithLayout(uint layoutId) =>
        layoutId != 0 && ByLayout.TryGetValue(layoutId, out var rows) ? rows : NoPoints;

    public CoveragePoint[] WithBnpc(uint bnpcId) => ByBnpc.TryGetValue(bnpcId, out var rows) ? rows : NoPoints;

    private static void Add(Dictionary<uint, List<CoveragePoint>> index, uint id, CoveragePoint point)
    {
        if (!index.TryGetValue(id, out var list))
            index[id] = list = new List<CoveragePoint>(1);
        list.Add(point);
    }

    private static Dictionary<uint, CoveragePoint[]> Freeze(Dictionary<uint, List<CoveragePoint>> index)
    {
        var result = new Dictionary<uint, CoveragePoint[]>(index.Count);
        foreach (var (id, list) in index)
            result[id] = list.ToArray();
        return result;
    }
}

/// <summary>The status mapping of a coverage row (docs/WHAT_WE_COLLECT.md, collector overlay).</summary>
internal static class CoverageStatusMap
{
    /// <summary>
    /// Maps a row's spawn / rank / exactBy. Returns false for Duplicate rows (hidden). FATE, Doubtful and Not real are
    /// qualifiers, not extra confirmations; an unknown combination is <see cref="CoverageStatus.Unknown"/>.
    /// </summary>
    public static bool TryMap(string? spawn, string? rank, string? exactBy, out CoverageStatus status,
        out CoverageQualifier qualifier)
    {
        qualifier = rank switch
        {
            "FATE" => CoverageQualifier.Fate,
            "Doubtful" => CoverageQualifier.Doubtful,
            "Not real" => CoverageQualifier.NotReal,
            _ => CoverageQualifier.None
        };
        status = CoverageStatus.Unknown;
        if (rank == "Duplicate")
            return false;

        if (rank == "Confirmed")
        {
            // A Confirmed row is a spawn sighting by definition; anything but an exact spawn is not trusted.
            status = spawn == "exact" && exactBy is null or "plugin-spawn" ? CoverageStatus.Confirmed : CoverageStatus.Unknown;
            return true;
        }

        status = (spawn, rank) switch
        {
            ("exact", "Seen nearby") => CoverageStatus.ToConfirm,
            ("approximate", _) => CoverageStatus.WalkPast,
            ("unseen", _) => CoverageStatus.NeverSeen,
            _ => CoverageStatus.Unknown
        };
        return true;
    }

    /// <summary>The short word shown with the colour and shape.</summary>
    public static string Label(CoverageStatus status) => status switch
    {
        CoverageStatus.Confirmed => "Confirmed",
        CoverageStatus.ToConfirm => "To confirm",
        CoverageStatus.WalkPast => "Walk-past",
        CoverageStatus.NeverSeen => "Never seen",
        _ => "Unknown"
    };

    /// <summary>Default colours (ABGR for ImGui): teal, amber, sky blue, lavender, grey.</summary>
    public static uint Colour(CoverageStatus status) => status switch
    {
        CoverageStatus.Confirmed => 0xFFB0D64Du,
        CoverageStatus.ToConfirm => 0xFF66D1FFu,
        CoverageStatus.WalkPast => 0xFFFFB768u,
        CoverageStatus.NeverSeen => 0xFFFFA3C5u,
        _ => 0xFFB0B0B0u
    };

    public static string QualifierText(CoverageQualifier qualifier) => qualifier switch
    {
        CoverageQualifier.Fate => "FATE",
        CoverageQualifier.Doubtful => "Doubtful",
        CoverageQualifier.NotReal => "Not real",
        _ => ""
    };
}

/// <summary>Parses and validates a coverage file off the framework thread.</summary>
internal static class CoverageParser
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public const int MaxRows = 20_000;
    public const int SchemaMajor = 1;

    /// <summary>
    /// Builds a snapshot, or returns null with a reason: too large, not JSON, an incompatible schema major, the wrong
    /// territory, a row without a key or with non-finite coordinates, a repeated key, too many rows.
    /// </summary>
    public static CoverageSnapshot? Parse(ReadOnlySpan<byte> json, uint expectedTerritory, out string error)
    {
        error = "";
        if (json.Length == 0 || json.Length > MaxBytes)
        {
            error = json.Length == 0 ? "empty" : "too large";
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(json);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail("not an object", out error);
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.String ||
                !MajorIs(schema.GetString(), SchemaMajor))
                return Fail("schema", out error);
            if (!root.TryGetProperty("territoryId", out var territory) || territory.ValueKind != JsonValueKind.Number ||
                !territory.TryGetUInt32(out var territoryId) || territoryId != expectedTerritory)
                return Fail("wrong zone", out error);
            if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return Fail("no rows", out error);
            if (rows.GetArrayLength() > MaxRows)
                return Fail("too many rows", out error);

            var names = new Dictionary<uint, string>();
            if (root.TryGetProperty("enemies", out var enemies) && enemies.ValueKind == JsonValueKind.Array)
                foreach (var enemy in enemies.EnumerateArray())
                    if (enemy.ValueKind == JsonValueKind.Object && UInt(enemy, "bnpcId") is { } id && Str(enemy, "name") is { } name)
                        names[id] = name.Length > 64 ? name[..64] : name;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var points = new List<CoveragePoint>(rows.GetArrayLength());
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || Str(row, "key") is not { Length: > 0 } key)
                    return Fail("row without key", out error);
                if (!keys.Add(key))
                    return Fail("repeated key", out error);
                if (Num(row, "x") is not { } x || Num(row, "y") is not { } y || Num(row, "z") is not { } z)
                    return Fail("bad coordinates", out error);
                if (!CoverageStatusMap.TryMap(Str(row, "spawn"), Str(row, "rank"), Str(row, "exactBy"), out var status,
                        out var qualifier))
                    continue; // Duplicate
                var bnpc = UInt(row, "bnpcId") ?? 0;
                var rowStatus = Str(row, "status");
                points.Add(new CoveragePoint
                {
                    Key = key,
                    BnpcId = bnpc,
                    LayoutId = UInt(row, "layoutId") ?? 0,
                    X = x,
                    Y = y,
                    Z = z,
                    HeightKnown = HeightKnown(Str(row, "ySource")),
                    Status = status,
                    Qualifier = qualifier,
                    PhotoNeeded = rowStatus is "partial" or "needs-pose",
                    Condition = Str(row, "condition") is { } condition ? (condition.Length > 80 ? condition[..80] : condition) : "",
                    Name = names.TryGetValue(bnpc, out var n) ? n : ""
                });
            }

            return new CoverageSnapshot(territoryId, Str(root, "dataThrough") ?? "", points);
        }
        catch (JsonException)
        {
            return Fail("not JSON", out error);
        }
    }

    /// <summary>Heights from ACT or a seen spawn are trusted; a home position height is not (exporter rule).</summary>
    public static bool HeightKnown(string? ySource) => ySource is "act" or "spawn position";

    private static CoverageSnapshot? Fail(string reason, out string error)
    {
        error = reason;
        return null;
    }

    private static bool MajorIs(string? version, int major)
    {
        if (string.IsNullOrEmpty(version))
            return false;
        var dot = version.IndexOf('.');
        var head = dot < 0 ? version : version[..dot];
        return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value == major;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static uint? UInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetUInt32(out var result) ? result : null;

    private static float? Num(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var number) || !double.IsFinite(number) || Math.Abs(number) > 100_000)
            return null;
        return (float)number;
    }
}

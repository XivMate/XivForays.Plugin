using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>Wire format of the observations stream: camelCase, enums as numbers, null fields left out.</summary>
internal static class ObservationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

using System.Numerics;
using System.Reflection;
using System.Text.Json;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

public class PayloadPrivacyTests
{
    private static readonly string[] PersonalFragments =
        ["name", "sender", "chat", "message", "text", "content", "account", "character", "world", "party", "retainer", "key", "ip"];

    private static Observation Context => new()
    {
        ObservedAtMs = 1793940205000,
        VisitId = Guid.Parse("4463f91b-5bd4-4d59-8830-261375699a11"),
        TerritoryId = 827,
        MapId = 520,
        WeatherId = 3,
        PlayerElementalLevel = 60,
        PlayerEffectiveElementalLevel = 60,
        PlayerElementalSynced = false
    };

    // Game data ids whose names contain a personal fragment but are not personal: a BNpcName row and a nameplate icon.
    private static readonly string[] AllowedNames = ["chainnumber", "nameid", "nameplateiconid"];

    [Theory]
    [InlineData(typeof(Observation))]
    [InlineData(typeof(ObservationBatch))]
    [InlineData(typeof(EnemyPose))]
    [InlineData(typeof(EnemyFormChange))]
    [InlineData(typeof(EnemyIdleTrack))]
    [InlineData(typeof(EnemyAggro))]
    [InlineData(typeof(AggroPlayer))]
    [InlineData(typeof(AggroLink))]
    [InlineData(typeof(EnemyRespawn))]
    [InlineData(typeof(PlayerContext))]
    [InlineData(typeof(EurekaContext))]
    [InlineData(typeof(OccultContext))]
    public void UploadTypesHaveNoTextFields(Type type) => AssertNoTextFields(type, new HashSet<Type>());

    // Walks the upload type and every nested record: only numbers, bools, enums, Guids (random track/visit ids) and
    // arrays of those or of nested upload records. No strings anywhere, no personal field names.
    private static void AssertNoTextFields(Type type, HashSet<Type> seen)
    {
        if (!seen.Add(type))
            return;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var t = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            while (t.IsArray)
                t = t.GetElementType()!;
            Assert.NotEqual(typeof(string), t);
            Assert.NotEqual(typeof(char), t);
            Assert.NotEqual(typeof(object), t);
            var nested = t.Namespace == typeof(Observation).Namespace && t.IsClass;
            Assert.True(t.IsPrimitive || t.IsEnum || t == typeof(Guid) || nested,
                $"{type.Name}.{property.Name} has unexpected type {t.Name}");
            if (nested)
                AssertNoTextFields(t, seen);

            var name = property.Name.ToLowerInvariant();
            if (AllowedNames.Contains(name))
                continue;
            Assert.DoesNotContain(PersonalFragments, fragment => name == fragment || (fragment.Length > 2 && name.Contains(fragment)));
            Assert.False(name.Contains("objectid") || name.Contains("entityid") || name.Contains("contentid") ||
                         name.Contains("taggerid") || name.Contains("ownerid") || name.Contains("sourceid") ||
                         name.Contains("targetid"), $"{type.Name}.{property.Name} looks like a game object id");
        }
    }

    [Fact]
    public void HeartbeatCarriesOnlyOwnPositionAndContext()
    {
        var heartbeat = Heartbeat.Create(Context, new Vector3(1.5f, 2.5f, -3.5f));
        var json = JsonSerializer.Serialize(heartbeat, ObservationJson.Options);
        using var document = JsonDocument.Parse(json);
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();

        string[] allowed =
        [
            "kind", "mapId", "observedAtMs", "playerEffectiveElementalLevel", "playerElementalLevel",
            "playerElementalSynced", "territoryId", "visitId", "weatherId", "x", "y", "z"
        ];
        Assert.Equal(allowed.Order(), keys);
        Assert.Equal(14, document.RootElement.GetProperty("kind").GetInt32());
        Assert.Equal(1.5f, document.RootElement.GetProperty("x").GetSingle());
    }

    [Fact]
    public void HeartbeatDropsEnemyFieldsEvenIfTheContextHadThem()
    {
        var context = Context with { GameId = 123, EnemyTrackId = Guid.NewGuid(), ModelCharaId = 9, Quantity = 4 };
        var heartbeat = Heartbeat.Create(context, Vector3.Zero);
        Assert.Null(heartbeat.GameId);
        Assert.Null(heartbeat.EnemyTrackId);
        Assert.Null(heartbeat.ModelCharaId);
        Assert.Null(heartbeat.Quantity);
    }

    [Fact]
    public void HeartbeatIsDueEveryThirtySeconds()
    {
        Assert.Equal(30_000, Heartbeat.IntervalMs);
        Assert.True(Heartbeat.IsDue(100, 0));
        Assert.False(Heartbeat.IsDue(10_000 + 29_999, 10_000));
        Assert.True(Heartbeat.IsDue(40_000, 10_000));
    }

    [Fact]
    public void StreamRatesAreTheLowLoadOnes()
    {
        Assert.Equal(30_000, StreamRates.EnemyPeriodicSampleMs);
        Assert.Equal(15_000, StreamRates.LegacySnapshotMs);
        Assert.False(StreamRates.MovedEnough(10, 0, 10));
        Assert.True(StreamRates.MovedEnough(15, 0, 0));
    }

    [Fact]
    public void EnemyRecordsSerializeModelFields()
    {
        var enemy = Context with { Kind = ObservationKind.EnemyAppeared, GameId = 7777, ModelCharaId = 4321, ModelScale = 1.25f };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(enemy, ObservationJson.Options));
        Assert.Equal(4321, document.RootElement.GetProperty("modelCharaId").GetInt32());
        Assert.Equal(1.25f, document.RootElement.GetProperty("modelScale").GetSingle());
        Assert.False(document.RootElement.TryGetProperty("x", out _));
    }

    [Fact]
    public void KindNumbersMatchTheIngestContract()
    {
        Assert.Equal(12, (int)ObservationKind.CriticalEncounter);
        Assert.Equal(13, (int)ObservationKind.SelfDefeat);
        Assert.Equal(14, (int)ObservationKind.RecordingActive);
        Assert.Equal(15, (int)ObservationKind.EnemySpawnPose);
        Assert.Equal(16, (int)ObservationKind.EnemyFormChange);
        Assert.Equal(17, (int)ObservationKind.EnemyIdleTrack);
        Assert.Equal(18, (int)ObservationKind.EnemyAggro);
        Assert.Equal(19, (int)ObservationKind.PlayerContext);
        Assert.Equal(Enumerable.Range(1, 19), Enum.GetValues<ObservationKind>().Select(k => (int)k));
    }
}

using System.Numerics;
using System.Text.Json;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>
/// Language-independent notices (task 2026-11-08-1353). A LogMessage row id and its integer parameters are the same
/// in every client language; only the rendered text differs. The samples below are rendered from the sheet's format
/// strings with placeholder item names ("Testitem"), as each client would show them.
/// </summary>
public class LogNoticeTests
{
    // (language, rendered text) for row 549 with lnum1 = 12 (chain), lnum2 = 30 (seconds left).
    private static readonly (string Lang, string Text)[] ChainSamples =
    [
        ("EN", "EXP chain #12! Chain expires in 30 seconds."),
        ("DE", "12er-Erfolgssträhne! 30 Sekunden bis zum Reißen der Strähne."),
        ("FR", "Chaîne : 12 d'affilée ! Vous avez 30 secondes pour continuer."),
        ("JA", "12チェーン成功！ 次のチェーンまで30秒以内。")
    ];

    // Row 657 with lnum1 = 3 (quantity), lnum2 = 21801 (item id), lnum3 = 0 (bonus %).
    private static readonly (string Lang, string Text)[] LootSamples =
    [
        ("EN", "You obtain 3 Testitems."),
        ("DE", "Du hast 3 Testgegenstände erhalten."),
        ("FR", "Vous obtenez 3 objets de test."),
        ("JA", "テストアイテムを3枚手に入れた。")
    ];

    private static Func<int, int?> Params(params int[] values) => i => i < values.Length ? values[i] : null;

    [Fact]
    public void ChainIsReadFromTheRowAndParametersInEveryLanguage()
    {
        foreach (var (lang, text) in ChainSamples)
        {
            // The structured path never looks at the text: same row, same parameters, same result.
            Assert.Equal((12u, 30u), LogNotices.Chain(LogNotices.ChainRow, Params(12, 30)));
            // The English fallback only understands English.
            Assert.Equal(lang == "EN" ? 12u : null, EnglishNotices.Chain(text));
        }

        Assert.Equal((40u, 0u), LogNotices.Chain(LogNotices.ChainRow, Params(40, 0))); // "Maximum chain (40) achieved!"
        Assert.Null(LogNotices.Chain(550, Params(12, 30)));
        Assert.Null(LogNotices.Chain(LogNotices.ChainRow, Params()));
    }

    [Fact]
    public void LootIsReadFromTheRowAndParametersInEveryLanguage()
    {
        foreach (var (lang, text) in LootSamples)
        {
            Assert.Equal((21801u, 3u), LogNotices.Loot(657, Params(3, 21801, 0)));
            Assert.Equal(lang == "EN", EnglishNotices.IsLoot(text));
        }

        Assert.Equal((21801u, 1u), LogNotices.Loot(3538, Params(1, 21801, 0)));
        Assert.Equal((21801u, 2u), LogNotices.Loot(3599, Params(2, 21801, 10)));
        Assert.Null(LogNotices.Loot(1300, Params(1, 5, 0))); // grand company seals: not an item row
        Assert.Null(LogNotices.Loot(657, Params(3))); // no item id
        Assert.Equal(1u, EnglishNotices.LootQuantity("You obtain a Testitem."));
        Assert.Equal(1200u, EnglishNotices.LootQuantity("You obtain 1,200 Testitems."));
    }

    [Fact]
    public void SelfDefeatNeedsTheLocalPlayerAsSourceAndANonPlayerTarget()
    {
        Assert.True(LogNotices.SelfDefeat(LogNotices.DefeatRow, sourceIsLocalPlayer: true, targetIsPlayer: false));
        Assert.False(LogNotices.SelfDefeat(LogNotices.DefeatRow, false, false)); // a party member's kill
        Assert.False(LogNotices.SelfDefeat(LogNotices.DefeatRow, true, true));
        Assert.False(LogNotices.SelfDefeat(558, true, false)); // "is defeated by"
        Assert.True(EnglishNotices.IsSelfDefeat("You defeat the Testmob."));
        Assert.False(EnglishNotices.IsSelfDefeat("Du hast den Testmob besiegt."));
    }

    [Fact]
    public void EachNoticeIsRecordedOnceWhenBothPathsSeeIt()
    {
        var pairing = new NoticePairing();
        const int loot = (int)ObservationKind.Loot;
        // English client: the LogMessage path records, then the chat line arrives and is skipped.
        pairing.Structured(loot, 1000);
        pairing.Structured(loot, 1000); // two drops in one frame
        Assert.False(pairing.FallbackRecords(loot, 1001));
        Assert.False(pairing.FallbackRecords(loot, 1001));
        // A notice only the chat path saw (a row the structured path doesn't know): recorded by the fallback.
        Assert.True(pairing.FallbackRecords(loot, 1500));
        // A stale token (no matching chat line) expires and never blocks a later fallback.
        pairing.Structured(loot, 2000);
        Assert.True(pairing.FallbackRecords(loot, 2000 + NoticePairing.TokenMs + 1));
        // Kinds are independent.
        pairing.Structured((int)ObservationKind.Chain, 9000);
        Assert.True(pairing.FallbackRecords((int)ObservationKind.SelfDefeat, 9001));
    }

    [Theory]
    [InlineData("2025.10.01.0000.0000", 2025100100000000L)]
    [InlineData("2026.02.11.0000.0001", 2026021100000001L)]
    [InlineData("", null)]
    [InlineData("v7.3", null)]
    [InlineData(null, null)]
    public void GameVersionIsOneNumber(string? version, long? expected) =>
        Assert.Equal(expected, LogNotices.GameVersionNumber(version));

    [Fact]
    public void HeartbeatCarriesTheGameVersionAndPosesTheCaptureRevision()
    {
        var context = new Observation { ObservedAtMs = 1, VisitId = Guid.NewGuid(), TerritoryId = 763, MapId = 1 };
        var heartbeat = Heartbeat.Create(context, Vector3.One, 2025100100000000L);
        using var hb = JsonDocument.Parse(JsonSerializer.Serialize(heartbeat, ObservationJson.Options));
        Assert.Equal(2025100100000000L, hb.RootElement.GetProperty("gameVersion").GetInt64());

        using var pose = JsonDocument.Parse(JsonSerializer.Serialize(new EnemyPose(), ObservationJson.Options));
        Assert.Equal(ContextRules.CaptureRevision, pose.RootElement.GetProperty("captureRevision").GetInt32());
        using var aggro = JsonDocument.Parse(JsonSerializer.Serialize(new EnemyAggro(), ObservationJson.Options));
        Assert.Equal(ContextRules.CaptureRevision, aggro.RootElement.GetProperty("captureRevision").GetInt32());
        Assert.Equal(4, ContextRules.CaptureRevision);

        var chain = context with { Kind = ObservationKind.Chain, ChainNumber = 12, ChainSecondsLeft = 30 };
        using var c = JsonDocument.Parse(JsonSerializer.Serialize(chain, ObservationJson.Options));
        Assert.Equal(30u, c.RootElement.GetProperty("chainSecondsLeft").GetUInt32());
    }
}

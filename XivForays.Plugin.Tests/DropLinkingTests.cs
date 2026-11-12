using System.Numerics;
using System.Text.Json;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>
/// Drop linking (capture revision 4): defeat notices name their enemy (own and party kills), loot and chain notices
/// carry the latest defeat's track id, the player's chain and the notice row, and FATE rewards are never linked to a
/// kill.
/// </summary>
public class DropLinkingTests
{
    private static readonly Guid Visit = Guid.Parse("4463f91b-5bd4-4d59-8830-261375699a11");
    private static readonly Guid Track = Guid.Parse("0f8e0000-0000-0000-0000-000000000001");
    private const long T0 = 1793940205000;

    private static Observation Context(long at, Guid? visit = null) => new()
    {
        ObservedAtMs = at, VisitId = visit ?? Visit, TerritoryId = 827, MapId = 520
    };

    private static Observation Defeat(long at, byte by = 1, Guid? visit = null) =>
        Context(at, visit) with { Kind = ObservationKind.SelfDefeat, DefeatBy = by };

    private static Observation Loot(long at, Guid? visit = null) =>
        Context(at, visit) with { Kind = ObservationKind.Loot, GameId = 21801, Quantity = 1, NoticeRow = 657 };

    private static Observation Chain(long at) =>
        Context(at) with { Kind = ObservationKind.Chain, ChainNumber = 30, ChainSecondsLeft = 0 };

    private static Observation Named(Observation defeat) => defeat with
    {
        EnemyTrackId = Track, GameId = 7213, LayoutId = 4211, Form = 1, IsMutated = true, IsAdapted = false,
        EnemyElementalLevel = 52, EnemyCombatLevel = 70, X = 1, Y = 2, Z = 3,
        EnemyMatch = (byte)EnemyMatch.NameUnique, EnemyCandidates = 1
    };

    private static DropLinker.Resolver Finds => (defeat, _, _, _) => Named(defeat);
    private static DropLinker.Resolver NeverFinds =>
        (defeat, _, final, _) => final ? defeat with { EnemyMatch = (byte)EnemyMatch.None } : null;

    // ---- who made the kill ----

    [Theory]
    [InlineData(true, false, false, (byte)1)]
    [InlineData(false, true, false, (byte)2)]
    [InlineData(false, false, false, null)] // another player's kill is not recorded
    [InlineData(true, false, true, null)] // a player target (PvP) is not a kill
    [InlineData(false, true, true, null)]
    public void DefeatByIsSelfOrPartyOnly(bool self, bool party, bool targetIsPlayer, byte? expected) =>
        Assert.Equal(expected, LogNotices.DefeatBy(LogNotices.DefeatRow, self, party, targetIsPlayer));

    [Fact]
    public void DefeatByNeedsTheDefeatRow() => Assert.Null(LogNotices.DefeatBy(558, true, false, false));

    [Theory]
    [InlineData(7213u, 7213u)]
    [InlineData(0u, null)]
    [InlineData(1_000_001u, null)] // event npc range, not a BNpcName row
    public void EnemyNameIdIsTheBNpcNameRow(uint objStrId, uint? expected) =>
        Assert.Equal(expected, LogNotices.EnemyNameId(objStrId));

    // ---- which enemy ----

    [Fact]
    public void ANameMatchWins()
    {
        var candidates = new[]
        {
            new DefeatCandidate(0, false, true, null, 1),
            new DefeatCandidate(1, true, true, null, 20)
        };
        var choice = DefeatMatch.Choose(candidates, hasName: true, finalAttempt: false, T0);
        Assert.Equal((1, EnemyMatch.NameUnique, (byte)1), choice);
    }

    [Fact]
    public void SeveralNameMatchesTakeTheFreshestDeathThenTheNearest()
    {
        var candidates = new[]
        {
            new DefeatCandidate(0, true, false, T0 - 2000, 1), // seen dead two seconds ago
            new DefeatCandidate(1, true, true, null, 30), // died since the last scan, far
            new DefeatCandidate(2, true, true, null, 10) // died since the last scan, near
        };
        Assert.Equal((2, EnemyMatch.NameNearest, (byte)3),
            DefeatMatch.Choose(candidates, true, false, T0));

        var seenDead = new[]
        {
            new DefeatCandidate(0, true, false, T0 - 2000, 1),
            new DefeatCandidate(1, true, false, T0 - 500, 50)
        };
        Assert.Equal(1, DefeatMatch.Choose(seenDead, true, false, T0)!.Value.Index);
    }

    [Fact]
    public void StaleDeathsAreNotCandidates()
    {
        var candidates = new[] { new DefeatCandidate(0, true, false, T0 - DefeatMatch.FreshMs - 1, 1) };
        Assert.Null(DefeatMatch.Choose(candidates, true, true, T0));
    }

    [Fact]
    public void WithoutANameMatchOnlyTheFinalAttemptTakesAnyFreshDeath()
    {
        var candidates = new[] { new DefeatCandidate(0, false, true, null, 5) };
        Assert.Null(DefeatMatch.Choose(candidates, hasName: true, finalAttempt: false, T0));
        Assert.Equal((0, EnemyMatch.FreshUnique, (byte)1), DefeatMatch.Choose(candidates, true, true, T0));

        // The English chat fallback has no name row: any fresh death on any attempt.
        var two = new[] { new DefeatCandidate(0, false, true, null, 9), new DefeatCandidate(1, false, true, null, 3) };
        Assert.Equal((1, EnemyMatch.FreshNearest, (byte)2), DefeatMatch.Choose(two, hasName: false, false, T0));
        Assert.Null(DefeatMatch.Choose([], false, true, T0));
    }

    // ---- linking ----

    [Fact]
    public void LootAfterAFoundDefeatCarriesItsTrackIdAgeAndCategory()
    {
        var linker = new DropLinker();
        var sent = new List<Observation>();
        linker.Defeat(Defeat(T0, by: 2), 7213, Finds, sent.Add);
        linker.Loot(Loot(T0 + 400), null, sent.Add);
        linker.Chain(Chain(T0 + 300), sent.Add);

        Assert.Equal(3, sent.Count);
        var defeat = sent[0];
        Assert.Equal(ObservationKind.SelfDefeat, defeat.Kind);
        Assert.Equal(Track, defeat.EnemyTrackId);
        Assert.Equal((byte)2, defeat.DefeatBy);
        var loot = sent[1];
        Assert.Equal(Track, loot.DefeatTrackId);
        Assert.Equal(400u, loot.DefeatAgeMs);
        Assert.Equal((byte)2, loot.DefeatBy);
        Assert.Null(loot.FateRewardId);
        Assert.Equal(Track, sent[2].DefeatTrackId);
        Assert.Equal(300u, sent[2].DefeatAgeMs);
    }

    [Fact]
    public void LootAfterAnUnresolvedDefeatIsHeldUntilTheEnemyIsFound()
    {
        var linker = new DropLinker();
        var sent = new List<Observation>();
        var found = false;
        DropLinker.Resolver later = (defeat, _, _, _) => found ? Named(defeat) : null;
        linker.Defeat(Defeat(T0), 7213, later, sent.Add);
        linker.Loot(Loot(T0 + 200), null, sent.Add);
        linker.Chain(Chain(T0 + 250), sent.Add);
        Assert.Empty(sent);
        Assert.Equal(1, linker.PendingCount);

        linker.Tick(T0 + 300, later, sent.Add);
        Assert.Empty(sent);
        found = true;
        linker.Tick(T0 + 600, later, sent.Add);

        Assert.Equal(0, linker.PendingCount);
        Assert.Equal([ObservationKind.SelfDefeat, ObservationKind.Loot, ObservationKind.Chain], sent.Select(o => o.Kind));
        Assert.Equal(T0 + 200, sent[1].ObservedAtMs); // original time kept
        Assert.Equal(Track, sent[1].DefeatTrackId);
        Assert.Equal(200u, sent[1].DefeatAgeMs);
        Assert.Equal(Track, sent[2].DefeatTrackId);
    }

    [Fact]
    public void ADefeatWhoseEnemyIsNeverFoundIsSentAfterTheResolveTimeWithoutAnEnemy()
    {
        var linker = new DropLinker();
        var sent = new List<Observation>();
        linker.Defeat(Defeat(T0), 7213, NeverFinds, sent.Add);
        linker.Loot(Loot(T0 + 100), null, sent.Add);
        linker.Tick(T0 + DropLinker.ResolveMs - 1, NeverFinds, sent.Add);
        Assert.Empty(sent);
        linker.Tick(T0 + DropLinker.ResolveMs, NeverFinds, sent.Add);

        Assert.Equal(2, sent.Count);
        Assert.Equal((byte)EnemyMatch.None, sent[0].EnemyMatch);
        Assert.Null(sent[0].EnemyTrackId);
        Assert.Null(sent[1].DefeatTrackId); // a defeat is linked, its enemy is unknown
        Assert.Equal(100u, sent[1].DefeatAgeMs);
        Assert.Equal((byte)1, sent[1].DefeatBy);
    }

    [Fact]
    public void LootLongAfterTheDefeatOrWithoutOneIsNotLinked()
    {
        var linker = new DropLinker();
        var sent = new List<Observation>();
        linker.Loot(Loot(T0), null, sent.Add);
        linker.Defeat(Defeat(T0 + 1000), 7213, Finds, sent.Add);
        linker.Loot(Loot(T0 + 1000 + DropLinker.MaxDefeatAgeMs + 1), null, sent.Add);

        Assert.Null(sent[0].DefeatAgeMs);
        Assert.Null(sent[2].DefeatTrackId);
        Assert.Null(sent[2].DefeatAgeMs);
    }

    [Fact]
    public void FateRewardGuardNeverLinksAFateRewardToAKill()
    {
        var linker = new DropLinker();
        var sent = new List<Observation>();
        linker.Defeat(Defeat(T0), 7213, Finds, sent.Add);
        linker.Loot(Loot(T0 + 300), fateRewardId: 1367, sent.Add);
        Assert.Equal(1367u, sent[1].FateRewardId);
        Assert.Null(sent[1].DefeatTrackId);
        Assert.Null(sent[1].DefeatAgeMs);
        Assert.Null(sent[1].DefeatBy);

        // Also while the defeat is still unresolved: the FATE reward is sent at once, not held.
        linker.Defeat(Defeat(T0 + 1000), 7213, NeverFinds, sent.Add);
        linker.Loot(Loot(T0 + 1100), 1367, sent.Add);
        Assert.Equal(1367u, sent[^1].FateRewardId);
        Assert.Null(sent[^1].DefeatAgeMs);
        Assert.Equal(1, linker.PendingCount);
    }

    [Fact]
    public void LootIsLinkedOnlyWithinItsVisitAndAVisitChangeFlushesHeldRecords()
    {
        var linker = new DropLinker();
        var sent = new List<Observation>();
        linker.Defeat(Defeat(T0), 7213, NeverFinds, sent.Add);
        linker.Loot(Loot(T0 + 100), null, sent.Add);
        linker.Flush(sent.Add);
        Assert.Equal(2, sent.Count);
        Assert.Equal(0, linker.PendingCount);

        var other = Guid.NewGuid();
        linker.Defeat(Defeat(T0 + 200), 7213, Finds, sent.Add);
        linker.Loot(Loot(T0 + 300, other), null, sent.Add);
        Assert.Null(sent[^1].DefeatAgeMs);
    }

    [Fact]
    public void TwoQuickKillsLinkEachLootToTheLatestDefeat()
    {
        var linker = new DropLinker();
        var sent = new List<Observation>();
        var second = Guid.NewGuid();
        linker.Defeat(Defeat(T0), 1, Finds, sent.Add);
        linker.Defeat(Defeat(T0 + 500), 2, (d, _, _, _) => d with { EnemyTrackId = second }, sent.Add);
        linker.Loot(Loot(T0 + 700), null, sent.Add);
        Assert.Equal(second, sent[^1].DefeatTrackId);
        Assert.Equal(200u, sent[^1].DefeatAgeMs);
    }

    // ---- chain ----

    [Fact]
    public void ChainNowIsTheLastNoticeWhileTheTimerRuns()
    {
        Assert.Equal((29u, 1500u), ChainNow.At(29, T0, true, T0 + 1500));
        Assert.Equal((0u, (uint?)null), ChainNow.At(29, T0, false, T0 + 1500)); // the chain ran out
        Assert.Equal(((uint?)null, (uint?)null), ChainNow.At(null, null, true, T0)); // running, no notice seen
    }

    // ---- FATE reward window ----

    [Theory]
    [InlineData(FateLifecycle.Running, (byte)99, false)]
    [InlineData(FateLifecycle.Running, (byte)100, true)]
    [InlineData(FateLifecycle.Ending, (byte)100, true)]
    [InlineData(FateLifecycle.Ended, (byte)0, true)]
    [InlineData(FateLifecycle.Failed, (byte)100, false)]
    public void AFateIsFinishingAtOneHundredPercentOrEnded(byte state, byte progress, bool expected) =>
        Assert.Equal(expected, FateRewardWindow.IsFinishing(state, progress));

    private static FateSnapshot Fate(uint id, byte state, byte progress, float x = 0, float z = 0) =>
        new(id, state, progress, 1, x, 0, z, 20);

    [Fact]
    public void TheRewardWindowIsOpenForFiveSecondsAfterTheLastFinishingSighting()
    {
        var window = new FateRewardWindow();
        window.Observe(Visit, [Fate(1367, FateLifecycle.Running, 80)], T0);
        Assert.Null(window.Open(Visit, T0, 0, 0));

        window.Observe(Visit, [Fate(1367, FateLifecycle.Running, 100)], T0 + 1000);
        Assert.Equal(1367u, window.Open(Visit, T0 + 1000, 0, 0));
        window.Observe(Visit, [Fate(1367, FateLifecycle.Ended, 100)], T0 + 2000);
        Assert.Equal(1367u, window.Open(Visit, T0 + 2000 + FateRewardWindow.HoldMs, 0, 0));
        Assert.Null(window.Open(Visit, T0 + 2001 + FateRewardWindow.HoldMs, 0, 0));
        Assert.Null(window.Open(Guid.NewGuid(), T0 + 2000, 0, 0));
    }

    [Fact]
    public void TheNearestFinishingFateOwnsTheWindowAndFailedFatesNeverOpenIt()
    {
        var window = new FateRewardWindow();
        window.Observe(Visit, [Fate(1, FateLifecycle.Ended, 100, 500, 500), Fate(2, FateLifecycle.Ended, 100, 10, 10),
            Fate(3, FateLifecycle.Failed, 40, 0, 0)], T0);
        Assert.Equal(2u, window.Open(Visit, T0, 0, 0));

        window.Clear();
        Assert.Null(window.Open(Visit, T0, 0, 0));
        window.Observe(Visit, [Fate(3, FateLifecycle.Failed, 100)], T0);
        Assert.Null(window.Open(Visit, T0, 0, 0));
    }

    // ---- wire format ----

    private static JsonElement Json(Observation observation) =>
        JsonDocument.Parse(JsonSerializer.Serialize(observation, ObservationJson.Options)).RootElement;

    [Fact]
    public void DefeatRecordsCarryTheEnemyFields()
    {
        var json = Json(Named(Defeat(T0, by: 2)) with { FateId = 1412 });
        Assert.Equal(13, json.GetProperty("kind").GetInt32());
        Assert.Equal(2, json.GetProperty("defeatBy").GetInt32());
        Assert.Equal(Track, json.GetProperty("enemyTrackId").GetGuid());
        Assert.Equal(7213u, json.GetProperty("gameId").GetUInt32());
        Assert.Equal(4211u, json.GetProperty("layoutId").GetUInt32());
        Assert.Equal(1, json.GetProperty("form").GetInt32());
        Assert.True(json.GetProperty("isMutated").GetBoolean());
        Assert.Equal(52, json.GetProperty("enemyElementalLevel").GetInt32());
        Assert.Equal(70, json.GetProperty("enemyCombatLevel").GetInt32());
        Assert.Equal(3f, json.GetProperty("z").GetSingle());
        Assert.Equal(1412, json.GetProperty("fateId").GetInt32());
        Assert.Equal(1, json.GetProperty("enemyMatch").GetInt32());
        Assert.Equal(1, json.GetProperty("enemyCandidates").GetInt32());
        // No names and no player ids: the property set is numbers, a random track id and categories only.
        foreach (var property in json.EnumerateObject())
            Assert.DoesNotContain("name", property.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LootRecordsCarryTheLinkTheChainTheRowAndTheFate()
    {
        var linked = Json(Loot(T0) with
        {
            DefeatTrackId = Track, DefeatAgeMs = 400, DefeatBy = 1, ChainNumber = 30, ChainAgeMs = 250
        });
        Assert.Equal(9, linked.GetProperty("kind").GetInt32());
        Assert.Equal(657u, linked.GetProperty("noticeRow").GetUInt32());
        Assert.Equal(Track, linked.GetProperty("defeatTrackId").GetGuid());
        Assert.Equal(400u, linked.GetProperty("defeatAgeMs").GetUInt32());
        Assert.Equal(1, linked.GetProperty("defeatBy").GetInt32());
        Assert.Equal(30u, linked.GetProperty("chainNumber").GetUInt32());
        Assert.Equal(250u, linked.GetProperty("chainAgeMs").GetUInt32());
        Assert.False(linked.TryGetProperty("fateRewardId", out _));

        var fate = Json(Loot(T0) with { FateRewardId = 1367 });
        Assert.Equal(1367u, fate.GetProperty("fateRewardId").GetUInt32());
        Assert.False(fate.TryGetProperty("defeatTrackId", out _));
    }

    [Fact]
    public void DeathRecordsCarryTheChainAndTheHeartbeatSaysLootIsListening()
    {
        var died = Json(Context(T0) with
        {
            Kind = ObservationKind.EnemyDied, EnemyTrackId = Track, ChainNumber = 12, ChainAgeMs = 900
        });
        Assert.Equal(12u, died.GetProperty("chainNumber").GetUInt32());
        Assert.Equal(900u, died.GetProperty("chainAgeMs").GetUInt32());

        Assert.True(Json(Heartbeat.Create(Context(T0), Vector3.One, null, true)).GetProperty("lootListening").GetBoolean());
        Assert.False(Json(Heartbeat.Create(Context(T0), Vector3.One, null, false)).GetProperty("lootListening").GetBoolean());
        Assert.False(Json(Heartbeat.Create(Context(T0), Vector3.One)).TryGetProperty("lootListening", out _));
    }

    [Fact]
    public void CaptureRevisionIsFourForDropLinking() => Assert.Equal(4, ContextRules.CaptureRevision);
}

using System.Text.Json;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>
/// FATE reward record (kind 24). Chat samples are real "You obtain" lines from a Louhi on Ice completion in Pagos
/// (a Louhi completion paid 9 lockboxes and 40 crystals, one fixed amount per FATE). Window samples are laid out
/// like the game's reward entry (five item slots, three additional slots, empty slots id 0); the numbers are the
/// measured NM amounts, because no window had been captured yet when these were written. Item ids are illustrative.
/// </summary>
public class FateRewardTests
{
    private static readonly Guid Visit = Guid.Parse("4463f91b-5bd4-4d59-8830-261375699a11");
    private static readonly Guid Other = Guid.Parse("0a0a0a0a-5bd4-4d59-8830-261375699a11");
    private const long T0 = 1793940205000;
    private const uint Louhi = 1203, Willows = 1301;
    private const uint Lockbox = 21801, Crystals = 21802, Card = 30011, Altar = 30012;

    private static Observation Context(long at, Guid? visit = null) => new()
    {
        ObservedAtMs = at, VisitId = visit ?? Visit, TerritoryId = 763, MapId = 1,
        PlayerElementalLevel = 38, PlayerEffectiveElementalLevel = 38, PlayerElementalSynced = false
    };

    private static Observation End(uint fate, long at, FateOutcome outcome = FateOutcome.Success, Guid? visit = null) =>
        Context(at, visit) with
        {
            Kind = ObservationKind.FateEnded, GameId = fate, Outcome = outcome, OutcomeInferred = false,
            StartedAt = (int)(at / 1000 - 300), X = 10, Y = 0, Z = 20, Radius = 40
        };

    private static Observation Loot(uint item, uint qty, long at, uint? fate = null, Guid? defeat = null) =>
        Context(at) with
        {
            Kind = ObservationKind.Loot, GameId = item, Quantity = qty, NoticeRow = 657, FateRewardId = fate,
            DefeatTrackId = defeat
        };

    private static RewardWindowRead Window(uint fate, uint medal, bool success = true, uint crystals = 40,
        params RewardItem[] more)
    {
        var slots = new List<RewardItem> { new(Lockbox, 9, 0), new(Crystals, crystals, 0) };
        slots.AddRange(more);
        while (slots.Count < 5)
            slots.Add(default);
        return RewardWindowRead.FromFields(fate, success, 1, medal, 31000, 0, 0, slots, [default, default, default]);
    }

    private static List<Observation> Run(Action<FateRewardTracker> feed, long tickAt = T0 + 60_000)
    {
        var tracker = new FateRewardTracker();
        feed(tracker);
        var records = new List<Observation>();
        tracker.Tick(tickAt, records.Add);
        return records;
    }

    private static void Pay(FateRewardTracker t, long at, uint crystals = 40)
    {
        t.NoteLoot(Loot(Lockbox, 9, at + 800));
        t.NoteLoot(Loot(Crystals, crystals, at + 900));
    }

    // ---- parsers: real chat lines, window rows ----

    [Theory]
    [InlineData("You obtain 9 Pagos lockboxes.", 9u)]
    [InlineData("You obtain 40 Pagos crystals.", 40u)]
    [InlineData("You obtain a cold-warped lockbox.", 1u)]
    [InlineData("You obtain 10,000 gil.", 10000u)]
    public void ChatSamplesGiveTheQuantity(string line, uint quantity)
    {
        Assert.True(EnglishNotices.IsLoot(line));
        Assert.Equal(quantity, EnglishNotices.LootQuantity(line));
    }

    [Fact]
    public void StructuredLootRowGivesItemAndQuantity() =>
        Assert.Equal((Crystals, 40u), LogNotices.Loot(657, i => i switch { 0 => 40, 1 => (int)Crystals, _ => 0 }));

    [Theory]
    [InlineData(0u, (byte)1)]
    [InlineData(1u, (byte)2)]
    [InlineData(2u, (byte)3)]
    [InlineData(3u, (byte)4)]
    public void MedalValuesZeroToThreeAreTheRank(uint medal, byte rank) => Assert.Equal(rank, Window(1, medal).Rank);

    [Fact]
    public void AnyOtherMedalValueIsNotARankButIsKept()
    {
        var window = Window(1, 60_903);
        Assert.Null(window.Rank);
        Assert.Equal(60_903u, window.MedalRaw);
    }

    [Fact]
    public void EmptyItemSlotsAreDropped()
    {
        var window = Window(Louhi, 0, more: new RewardItem(Card, 1, 16));
        Assert.Equal([new RewardItem(Lockbox, 9, 0), new RewardItem(Crystals, 40, 0), new RewardItem(Card, 1, 16)],
            window.Items);
        Assert.Empty(window.ExtraItems);
        Assert.True(window.EurekaFate);
        Assert.True(window.IsPlausible);
    }

    [Fact]
    public void AReadWithoutAFateIdIsNotPlausible() =>
        Assert.False(RewardWindowRead.FromFields(0, true, 1, 0, 1, 0, 0, [new RewardItem(Lockbox, 1, 0)], []).IsPlausible);

    [Fact]
    public void ARepeatedReadHasTheSameSignature() =>
        Assert.Equal(Window(Louhi, 0).Signature, Window(Louhi, 0).Signature);

    // ---- one completion ----

    [Fact]
    public void FullCompletionJoinsWindowEndAndLootNotices()
    {
        var records = Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 3);
            t.NoteWindow(Context(T0 + 400), Window(Louhi, 0, more: new RewardItem(Card, 1, 0)), 35, 37, 80, 3);
            Pay(t, T0);
            t.NoteLoot(Loot(Card, 1, T0 + 950));
        });

        var r = Assert.Single(records);
        Assert.Equal(ObservationKind.FateReward, r.Kind);
        Assert.Equal(Louhi, r.GameId);
        Assert.Equal((byte)35, r.FateLevel);
        Assert.Equal(38u, r.PlayerElementalLevel);
        Assert.Equal((byte)37, r.PlayerClassJobId);
        Assert.Equal((byte)80, r.PlayerJobLevel);
        Assert.Equal((byte)3, r.GroupSize);
        Assert.Equal((byte)1, r.RewardRank);
        Assert.True(r.RewardWindowSeen);
        Assert.True(r.RewardEndSeen);
        Assert.True(r.InsideFateRadius);
        Assert.Equal(400, r.WindowAfterEndMs);
        Assert.Equal(FateOutcome.Success, r.Outcome);
        Assert.Equal(T0, r.ObservedAtMs);
        Assert.Equal([[(int)Lockbox, 9, 1], [(int)Crystals, 40, 1], [(int)Card, 1, 1]], r.ReceivedItems);
        Assert.Empty(r.NotReceived!);
        Assert.Equal((byte)0, r.InventoryFullNotices);
        Assert.Equal((byte)0, r.OverlapCount);
    }

    [Fact]
    public void NothingIsSentBeforeTheRewardPeriodEnds()
    {
        var tracker = new FateRewardTracker();
        tracker.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
        var records = new List<Observation>();
        tracker.Tick(T0 + FateRewardTracker.PostMs, records.Add);
        Assert.Empty(records);
        tracker.Tick(T0 + FateRewardTracker.PostMs + FateRewardTracker.GraceMs, records.Add);
        Assert.Single(records);
    }

    [Fact]
    public void WindowBeforeTheEndIsTheSameCompletion()
    {
        var records = Run(t =>
        {
            t.NoteWindow(Context(T0), Window(Louhi, 1), null, 37, 80, 1);
            t.NoteEnd(End(Louhi, T0 + 300), 35, true, 37, 80, 1);
            Pay(t, T0);
        });
        var r = Assert.Single(records);
        Assert.Equal(-300, r.WindowAfterEndMs);
        Assert.Equal((byte)2, r.RewardRank);
        Assert.Equal((byte)35, r.FateLevel);
        Assert.Equal(T0, r.ObservedAtMs);
        Assert.Equal(2, r.ReceivedItems!.Length);
    }

    [Fact]
    public void ARepeatedWindowReadIsIgnored()
    {
        var records = Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 400), Window(Louhi, 0), 35, 37, 80, 1);
            t.NoteWindow(Context(T0 + 1400), Window(Louhi, 0), 35, 37, 80, 1);
            Pay(t, T0);
        });
        Assert.Single(records);
    }

    // ---- edge cases ----

    [Fact]
    public void ReducedRankAndReducedCrystalsAreBothRecorded()
    {
        // Silver: the 80% tier seen in the logs (40 crystals become 32).
        var records = Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 400), Window(Louhi, 1, crystals: 32), 35, 37, 80, 1);
            Pay(t, T0, 32);
        });
        var r = Assert.Single(records);
        Assert.Equal((byte)2, r.RewardRank);
        Assert.Equal((int)Crystals, r.RewardItems![1][0]);
        Assert.Equal(32, r.RewardItems[1][1]);
        Assert.Empty(r.NotReceived!);
    }

    [Fact]
    public void InventoryFullLeavesTheCardInNotReceived()
    {
        var records = Run(t =>
        {
            var tracker = t;
            tracker.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            tracker.NoteWindow(Context(T0 + 400), Window(Louhi, 0, more: new RewardItem(Card, 1, 0)), 35, 37, 80, 1);
            Pay(tracker, T0);
            tracker.NoteInventoryFull(Visit, T0 + 1000);
        });
        var r = Assert.Single(records);
        Assert.Equal([[(int)Card, 1, NotReceivedReason.InventoryFull]], r.NotReceived);
        Assert.Equal((byte)1, r.InventoryFullNotices);
    }

    [Fact]
    public void ACrystalCapPickupShowsTheMissingAmount()
    {
        // Only 3 of 40 crystals fit under the cap.
        var records = Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 400), Window(Louhi, 0), 35, 37, 80, 1);
            Pay(t, T0, 3);
        });
        Assert.Equal([[(int)Crystals, 37, NotReceivedReason.Unknown]], Assert.Single(records).NotReceived);
    }

    [Fact]
    public void NoLootNoticesMeansEverythingListedIsNotReceived()
    {
        // Chat filters or notices missed: the window still lists the rewards.
        var records = Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 400), Window(Louhi, 0), 35, 37, 80, 1);
        });
        var r = Assert.Single(records);
        Assert.Empty(r.ReceivedItems!);
        Assert.Equal(2, r.NotReceived!.Length);
    }

    [Fact]
    public void LootNoticesAloneStillMakeARecordWithoutTheWindow()
    {
        var records = Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            Pay(t, T0);
        });
        var r = Assert.Single(records);
        Assert.False(r.RewardWindowSeen);
        Assert.Null(r.RewardRank);
        Assert.Null(r.NotReceived);
        Assert.Equal(2, r.ReceivedItems!.Length);
    }

    [Fact]
    public void TwoFatesEndingTogetherAreSplitByTheWindowItemLists()
    {
        var records = Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteEnd(End(Willows, T0 + 200), 20, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 500), Window(Louhi, 0, more: new RewardItem(Card, 1, 0)), 35, 37, 80, 1);
            t.NoteWindow(Context(T0 + 600), Window(Willows, 1, more: new RewardItem(Altar, 1, 0)), 20, 37, 80, 1);
            t.NoteLoot(Loot(Card, 1, T0 + 900));
            t.NoteLoot(Loot(Altar, 1, T0 + 950));
        });
        Assert.Equal(2, records.Count);
        var louhi = records.Single(r => r.GameId == Louhi);
        var willows = records.Single(r => r.GameId == Willows);
        Assert.Contains(louhi.ReceivedItems!, i => i[0] == (int)Card);
        Assert.DoesNotContain(louhi.ReceivedItems!, i => i[0] == (int)Altar);
        Assert.Contains(willows.ReceivedItems!, i => i[0] == (int)Altar);
        Assert.Equal((byte)1, louhi.OverlapCount);
        Assert.Equal((byte)1, willows.OverlapCount);
        Assert.Equal((byte)1, louhi.RewardRank);
        Assert.Equal((byte)2, willows.RewardRank);
    }

    [Fact]
    public void AFateIdHintOnALootRecordWins()
    {
        var records = Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteEnd(End(Willows, T0 + 100), 20, true, 37, 80, 1);
            t.NoteLoot(Loot(Card, 1, T0 + 120, fate: Willows));
        });
        Assert.Contains(records.Single(r => r.GameId == Willows).ReceivedItems!, i => i[0] == (int)Card);
        Assert.Empty(records.Single(r => r.GameId == Louhi).ReceivedItems!);
    }

    [Fact]
    public void LootOutsideTheRewardPeriodIsNotClaimed()
    {
        var records = Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteLoot(Loot(Card, 1, T0 + FateRewardTracker.PostMs + 1000));
            t.NoteLoot(Loot(Lockbox, 1, T0 - FateRewardTracker.PreMs - 1000));
        });
        // Inside the radius and a success: still the player's completion, with nothing received.
        Assert.Empty(Assert.Single(records).ReceivedItems!);
    }

    [Fact]
    public void ALootRecordLinkedToAKillIsNotARewardItem()
    {
        Assert.False(FateRewardTracker.IsRewardLoot(Loot(Lockbox, 1, T0, defeat: Guid.NewGuid())));
        Assert.True(FateRewardTracker.IsRewardLoot(Loot(Lockbox, 1, T0)));
        Assert.False(FateRewardTracker.IsRewardLoot(Context(T0) with { Kind = ObservationKind.Chain }));
    }

    [Fact]
    public void LeavingTheZoneBeforeTheWindowStillRecordsWhatWasSeen()
    {
        var tracker = new FateRewardTracker();
        tracker.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
        var records = new List<Observation>();
        tracker.FlushOtherVisits(Other, records.Add);
        var r = Assert.Single(records);
        Assert.False(r.RewardWindowSeen);
        Assert.True(r.RewardEndSeen);
        Assert.Equal(Visit, r.VisitId);
    }

    [Fact]
    public void AFateWatchedFromOutsideProducesNothing()
    {
        // Outside the radius, no window, no items: not the player's completion.
        var records = Run(t => t.NoteEnd(End(Louhi, T0), 35, false, 37, 80, 1));
        Assert.Empty(records);
    }

    [Fact]
    public void AFailedFateWithAnEmptyWindowProducesNothing()
    {
        var failed = RewardWindowRead.FromFields(Louhi, false, 1, 2, 0, 0, 0, [], []);
        var records = Run(t =>
        {
            t.NoteEnd(End(Louhi, T0, FateOutcome.Failed), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 300), failed, 35, 37, 80, 1);
        });
        Assert.Empty(records);
    }

    [Fact]
    public void ARejectedWindowReadIsNotUsed()
    {
        var bad = RewardWindowRead.FromFields(0, true, 1, 0, 1, 0, 0, [new RewardItem(Lockbox, 1, 0)], []);
        var records = Run(t => t.NoteWindow(Context(T0), bad, null, null, null, 1));
        Assert.Empty(records);
    }

    [Fact]
    public void ClearDropsEverything()
    {
        var tracker = new FateRewardTracker();
        tracker.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
        tracker.NoteLoot(Loot(Card, 1, T0 + 100));
        tracker.Clear();
        var records = new List<Observation>();
        tracker.Tick(T0 + 60_000, records.Add);
        Assert.Empty(records);
        Assert.Equal(0, tracker.PendingCount);
    }

    // ---- inventory full rows ----

    [Theory]
    [InlineData(460u, true)]
    [InlineData(1872u, true)]
    [InlineData(7970u, true)]
    [InlineData(11596u, true)]
    [InlineData(657u, false)]
    [InlineData(549u, false)]
    public void InventoryFullRowsAreTheSheetRows(uint row, bool full) => Assert.Equal(full, InventoryFullRows.IsFull(row));

    // ---- wire format and privacy ----

    [Fact]
    public void TheRecordIsNumbersOnlyOnTheWire()
    {
        var records = Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 3);
            t.NoteWindow(Context(T0 + 400), Window(Louhi, 0, more: new RewardItem(Card, 1, 0)), 35, 37, 80, 3);
            Pay(t, T0);
        });
        var json = JsonSerializer.Serialize(Assert.Single(records), ObservationJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(24, root.GetProperty("kind").GetInt32());
        Assert.Equal(Louhi, root.GetProperty("gameId").GetUInt32());
        Assert.Equal(1, root.GetProperty("rewardRank").GetInt32());
        Assert.Equal(35, root.GetProperty("fateLevel").GetInt32());
        Assert.Equal(2, root.GetProperty("receivedItems").GetArrayLength());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("playerClassJobId").ValueKind);
        foreach (var property in root.EnumerateObject().Where(p => p.Name != "visitId")) // the random visit id is a GUID
            Assert.NotEqual(JsonValueKind.String, property.Value.ValueKind); // no text anywhere in the record
        Assert.False(root.TryGetProperty("name", out _));
        Assert.False(root.TryGetProperty("fateName", out _));
    }

    [Fact]
    public void TheLocalLogLinesCarryNumbersOnly()
    {
        var window = Window(Louhi, 0);
        var line = FateRewardLog.Line(window);
        Assert.StartsWith("window fate=1203 success=True", line);
        Assert.Contains("items=[21801x9f0 21802x40f0]", line);
        var record = Assert.Single(Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 400), window, 35, 37, 80, 1);
            Pay(t, T0);
        }));
        Assert.Contains("rank=1", FateRewardLog.Line(record));
    }

    // ---- reasons and currency (window left side) ----

    private const uint Poetics = 28;

    private static RewardWindowRead WithCurrency(uint fate, uint medal, uint poetics) =>
        RewardWindowRead.FromFields(fate, true, 1, medal, 0, 2, 0,
            [new RewardItem(Poetics, poetics, 64), default, default, default, default],
            [new RewardItem(Lockbox, 9, 0), new RewardItem(Crystals, 40, 0), default]);

    [Theory]
    [InlineData("You cannot carry any more Allagan tomestones of poetics.", true)]
    [InlineData("You cannot carry any more . Attempt abandoned.", false)]
    [InlineData("You obtain 9 Pagos lockboxes.", false)]
    public void CannotCarryChatLine(string line, bool expected) => Assert.Equal(expected, EnglishNotices.IsCannotCarry(line));

    [Theory]
    [InlineData("Unable to obtain. You already possess one of that unique item.", true)]
    [InlineData("You cannot carry any more Allagan tomestones of poetics.", false)]
    public void UniqueOwnedChatLine(string line, bool expected) => Assert.Equal(expected, EnglishNotices.IsUniqueOwned(line));

    [Fact]
    public void CurrencyRowsAreReceivedByDefault()
    {
        // The poetics row has no "You obtain" line; it is paid straight in.
        var r = Assert.Single(Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 400), WithCurrency(Louhi, 0, 111), 35, 37, 80, 1);
            Pay(t, T0);
        }));
        Assert.Empty(r.NotReceived!);
        Assert.Equal([[(int)Poetics, 111]], r.CurrencyPaid);
    }

    [Fact]
    public void CurrencyAtItsCapIsNotReceivedWithReasonCapped()
    {
        var r = Assert.Single(Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 400), WithCurrency(Louhi, 0, 111), 35, 37, 80, 1);
            Pay(t, T0);
            t.NoteCapped(Visit, Poetics, T0 + 1000); // "You cannot carry any more Allagan tomestones of poetics."
        }));
        Assert.Equal([[(int)Poetics, 111, NotReceivedReason.Capped]], r.NotReceived);
        Assert.Empty(r.CurrencyPaid!);
    }

    [Fact]
    public void ACrystalAtItsCapGetsReasonCapped()
    {
        var r = Assert.Single(Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 400), Window(Louhi, 0), 35, 37, 80, 1);
            Pay(t, T0, 3);
            t.NoteCapped(Visit, Crystals, T0 + 1000);
        }));
        Assert.Equal([[(int)Crystals, 37, NotReceivedReason.Capped]], r.NotReceived);
    }

    [Fact]
    public void AnOwnedUniqueItemGetsReasonUnique()
    {
        var r = Assert.Single(Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 400), Window(Louhi, 0, more: new RewardItem(Card, 1, 0)), 35, 37, 80, 1);
            Pay(t, T0);
            t.NoteUnique(Visit, T0 + 1000);
        }));
        Assert.Equal([[(int)Card, 1, NotReceivedReason.Unique]], r.NotReceived);
    }

    [Fact]
    public void ACappedNoticeOutsideThePeriodIsIgnored()
    {
        var r = Assert.Single(Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 400), WithCurrency(Louhi, 0, 111), 35, 37, 80, 1);
            Pay(t, T0);
            t.NoteCapped(Visit, Poetics, T0 + 30_000);
        }));
        Assert.Empty(r.NotReceived!);
    }

    [Fact]
    public void ReasonsAreInTheLocalLog()
    {
        var r = Assert.Single(Run(t =>
        {
            t.NoteEnd(End(Louhi, T0), 35, true, 37, 80, 1);
            t.NoteWindow(Context(T0 + 400), WithCurrency(Louhi, 0, 111), 35, 37, 80, 1);
            Pay(t, T0);
            t.NoteCapped(Visit, Poetics, T0 + 1000);
        }));
        Assert.Contains("notReceived=[28x111r1]", FateRewardLog.Line(r));
    }
}

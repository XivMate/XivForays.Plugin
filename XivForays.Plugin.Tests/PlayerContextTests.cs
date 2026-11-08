using System.Text.Json;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>Kind 19 PlayerContext (task 2026-11-08-1300): change rules, due rule, status allow-list, privacy.</summary>
public class PlayerContextTests
{
    private static PlayerContext Sample() => new()
    {
        CaptureRevision = ContextRules.CaptureRevision,
        EorzeaTime = 36_000_000_000, FrameworkUtc = 1794139827, InstanceStartTs = 1794130000, DirectorRowId = 21,
        ClassJobId = 19, Level = 100, SyncedLevel = 70, LevelSynced = true, HpPct = 100, MpPct = 100, InCombat = false,
        Eureka = new EurekaContext
        {
            MaxElementalLevel = 60, Experience = 1200, ExperienceNeeded = 5000, Board = [1, 0, 2, 0, 3, 0], Magicite = 3,
            MagiaAether = 1, MagiaAetherCharge = 150
        },
        Chain = [42.5f, 60f], LastChainNumber = 12, LastChainAtMs = 1794139800000, GroupSize = 3,
        Group = [[24, 70, 4, 58, 58, 0], [33, 70, 3, 60, 50, 1]], Statuses = [[50, 0, 120]]
    };

    [Fact]
    public void TimersHpAndExperienceAloneAreNotChanges()
    {
        var a = Sample();
        var b = a with
        {
            HpPct = 40, MpPct = 10, Chain = [30f, 60f], Statuses = [[50, 0, 20]],
            Eureka = a.Eureka! with { Experience = 2000, MagiaAetherCharge = 300 }, EorzeaTime = a.EorzeaTime + 20
        };
        Assert.Equal(0, ContextRules.Diff(a, b));
    }

    [Fact]
    public void EachPartIsItsOwnChangeBit()
    {
        var a = Sample();
        Assert.Equal(ContextRules.BoardBit, ContextRules.Diff(a, a with { Eureka = a.Eureka! with { Board = [0, 1, 2, 0, 3, 0] } }));
        Assert.Equal(ContextRules.ChainBit, ContextRules.Diff(a, a with { Chain = [59f, 60f] })); // a new kill extends it
        Assert.Equal(ContextRules.ChainBit, ContextRules.Diff(a, a with { Chain = null })); // chain broke
        Assert.Equal(ContextRules.ChainBit, ContextRules.Diff(a, a with { LastChainNumber = 13 }));
        Assert.Equal(ContextRules.GroupBit, ContextRules.Diff(a, a with { GroupSize = 4, Group = [[24, 70, 4, 58, 58, 0], [33, 70, 3, 60, 50, 1], [21, 70, 1, 60, 60, 0]] }));
        // A member syncing down to a FATE / NM, or levelling up, is a group change.
        Assert.Equal(ContextRules.GroupBit, ContextRules.Diff(a, a with { Group = [[24, 70, 4, 58, 40, 1], [33, 70, 3, 60, 50, 1]] }));
        Assert.Equal(ContextRules.GroupBit, ContextRules.Diff(a, a with { Group = [[24, 70, 4, 59, 59, 0], [33, 70, 3, 60, 50, 1]] }));
        Assert.Equal(ContextRules.StatusBit, ContextRules.Diff(a, a with { Statuses = [] }));
        Assert.Equal(ContextRules.DeadBit, ContextRules.Diff(a, a with { Dead = true }));
        Assert.Equal(ContextRules.JobBit, ContextRules.Diff(a, a with { ClassJobId = 20 }));
        Assert.Equal(ContextRules.InstanceBit, ContextRules.Diff(a, a with { InstanceStartTs = 1794140000 }));
        Assert.Equal(ContextRules.InstanceBit, ContextRules.Diff(a with { CurrentWorldId = 33 }, a with { CurrentWorldId = 34 }));
        Assert.Equal(ContextRules.CombatBit, ContextRules.Diff(a, a with { InCombat = true }));
        Assert.Equal(ContextRules.EurekaBit, ContextRules.Diff(a, a with { Eureka = a.Eureka! with { MaxElementalLevel = 59 } }));

        var top = new Observation { PlayerElementalLevel = 59, PlayerEffectiveElementalLevel = 59, PlayerElementalSynced = false };
        Assert.Equal(ContextRules.LevelBit, ContextRules.Diff(a, a, top, top with { PlayerElementalLevel = 60 }));
        Assert.Equal(ContextRules.LevelBit, ContextRules.Diff(a, a, top, top with { PlayerElementalSynced = true }));
        Assert.Equal(0, ContextRules.Diff(null, a));
    }

    [Fact]
    public void RecordsAreDueAtStartOnChangeAtMostEveryFiveSecondsAndEveryMinute()
    {
        Assert.Equal(1, ContextRules.Due(1000, 0, hasLast: false, changed: 0));
        Assert.Equal(0, ContextRules.Due(4999, 0, true, ContextRules.ChainBit));
        Assert.Equal(2, ContextRules.Due(5000, 0, true, ContextRules.ChainBit));
        Assert.Equal(0, ContextRules.Due(59_999, 0, true, 0));
        Assert.Equal(3, ContextRules.Due(60_000, 0, true, 0));
    }

    [Theory]
    [InlineData("Sprint", true)]
    [InlineData("Sneak", true)]
    [InlineData("Brink of Death", true)]
    [InlineData("Protect L", true)]
    [InlineData("Lost Reraise", true)]
    [InlineData("Phantom Guard", true)]
    [InlineData("Well Fed", true)]
    [InlineData("Medicated", false)]
    [InlineData("Regen", false)]
    [InlineData("Dance Partner", false)]
    [InlineData("", false)]
    public void PlayerStatusesAreAllowListed(string name, bool allowed) =>
        Assert.Equal(allowed, ContextRules.IsAllowedStatusName(name));

    [Fact]
    public void ContextRecordsCarryNoStringsAndKeepSchemaOne()
    {
        var record = new Observation
        {
            Kind = ObservationKind.PlayerContext, ObservedAtMs = 1794139827000, VisitId = Guid.NewGuid(), TerritoryId = 827,
            MapId = 520, PlayerElementalLevel = 60, PlayerEffectiveElementalLevel = 60,
            Situation = Sample() with { Reason = 2, Changed = 8, CurrentWorldId = 33 }
        };
        var json = JsonSerializer.Serialize(new ObservationBatch(1, Guid.NewGuid(), [record]), ObservationJson.Options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var item = document.RootElement.GetProperty("observations")[0];
        Assert.Equal(19, item.GetProperty("kind").GetInt32());
        var situation = item.GetProperty("situation");
        Assert.Equal(1, situation.GetProperty("situationVersion").GetInt32());
        Assert.Equal(ContextRules.CaptureRevision, situation.GetProperty("captureRevision").GetInt32());
        Assert.Equal(6, situation.GetProperty("eureka").GetProperty("board").GetArrayLength());
        Assert.Equal(33u, situation.GetProperty("currentWorldId").GetUInt32());
        Walk(item, null);
        // Group rows: exactly [job, level, role, forayLevel, effectiveForayLevel, synced], small numbers only, so no
        // content id, object id, entity id or world of another player can ride along.
        foreach (var row in situation.GetProperty("group").EnumerateArray())
        {
            Assert.Equal(6, row.GetArrayLength());
            foreach (var cell in row.EnumerateArray())
                Assert.InRange(cell.GetInt64(), 0, 255);
        }
        foreach (var banned in new[] { "name", "contentId", "objectId", "entityId", "world", "homeWorld" })
            Assert.DoesNotContain("\"" + banned, json, StringComparison.OrdinalIgnoreCase);

        static void Walk(JsonElement e, string? key)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                        Walk(p.Value, p.Name);
                    break;
                case JsonValueKind.Array:
                    foreach (var i in e.EnumerateArray())
                        Walk(i, key);
                    break;
                case JsonValueKind.String:
                    Assert.True(key == "visitId", $"string under {key}");
                    break;
            }
        }
    }

    private const uint Hydatos = 827, Southern = 920, SouthHorn = 1252, Limsa = 129;

    [Fact]
    public void EurekaMemberRowsCarryElementalLevelEffectiveLevelAndSync()
    {
        // PartyMember content key/values in Eureka: 2 effective elemental level, 3 synced, 4 elemental level.
        Assert.Equal(new[] { 33, 70, 3, 60, 52, 1 }, ContextRules.MemberRow(Hydatos, 33, 70, 3, [(2u, 52u), (3u, 1u), (4u, 60u)]));
        Assert.Equal(new[] { 33, 70, 3, 60, 60, 0 }, ContextRules.MemberRow(Hydatos, 33, 70, 3, [(4u, 60u), (2u, 60u), (3u, 0u)]));
        // Not loaded for this member: zeros, never a guess.
        Assert.Equal(new[] { 33, 70, 3, 0, 0, 0 }, ContextRules.MemberRow(Hydatos, 33, 70, 3, []));
        // A stray sync flag without an effective level is not "synced".
        Assert.Equal(new[] { 33, 70, 3, 0, 0, 0 }, ContextRules.MemberRow(Hydatos, 33, 70, 3, [(3u, 1u)]));
    }

    [Fact]
    public void BozjaAndOccultMemberRowsUseTheirOwnKeysAndOtherZonesSendNone()
    {
        Assert.Equal(new[] { 21, 80, 1, 25, 0, 0 }, ContextRules.MemberRow(Southern, 21, 80, 1, [(5u, 25u)]));
        // The Eureka keys mean nothing in Bozja.
        Assert.Equal(new[] { 21, 80, 1, 0, 0, 0 }, ContextRules.MemberRow(Southern, 21, 80, 1, [(2u, 52u), (4u, 60u)]));
        Assert.Equal(new[] { 24, 100, 4, 20, 12, 1 }, ContextRules.MemberRow(SouthHorn, 24, 100, 4, [(6u, 12u), (7u, 20u)]));
        Assert.Equal(new[] { 24, 100, 4, 20, 20, 0 }, ContextRules.MemberRow(SouthHorn, 24, 100, 4, [(6u, 20u), (7u, 20u)]));
        Assert.Equal(new[] { 24, 100, 4, 0, 0, 0 }, ContextRules.MemberRow(Limsa, 24, 100, 4, [(2u, 52u), (5u, 25u), (7u, 20u)]));
    }

    [Fact]
    public void GroupRowsSortOnEveryColumn()
    {
        var rows = ContextRules.SortRows([[33, 70, 3, 60, 50, 1], [24, 70, 4, 58, 58, 0], [33, 70, 3, 59, 59, 0]]);
        Assert.Equal(new[] { 24, 70, 4, 58, 58, 0 }, rows[0]);
        Assert.Equal(new[] { 33, 70, 3, 59, 59, 0 }, rows[1]);
        Assert.Equal(new[] { 33, 70, 3, 60, 50, 1 }, rows[2]);
        Assert.Equal("24/70/4 foray 58/58; 33/70/3 foray 50/60 synced", ContextLog.GroupLine(Sample().Group));
        Assert.Equal("-", ContextLog.GroupLine([]));
    }

    [Fact]
    public void GroupRowsInTheUploadAreSixNumbersEach()
    {
        var record = new Observation
        {
            Kind = ObservationKind.PlayerContext, ObservedAtMs = 1794139827000, VisitId = Guid.NewGuid(), TerritoryId = Hydatos,
            Situation = Sample()
        };
        var json = JsonSerializer.Serialize(new ObservationBatch(1, Guid.NewGuid(), [record]), ObservationJson.Options);
        using var document = JsonDocument.Parse(json);
        var group = document.RootElement.GetProperty("observations")[0].GetProperty("situation").GetProperty("group");
        Assert.Equal(2, group.GetArrayLength());
        foreach (var row in group.EnumerateArray())
        {
            Assert.Equal(6, row.GetArrayLength());
            foreach (var cell in row.EnumerateArray())
                Assert.Equal(JsonValueKind.Number, cell.ValueKind);
        }
        Assert.Equal(new[] { 33, 70, 3, 60, 50, 1 }, group[1].EnumerateArray().Select(c => c.GetInt32()).ToArray());
    }

    [Fact]
    public void ContextLogLineIsNameFree()
    {
        var record = new Observation
        {
            Kind = ObservationKind.PlayerContext, PlayerElementalLevel = 60, PlayerEffectiveElementalLevel = 58,
            Situation = Sample() with { Reason = 2, Changed = ContextRules.BoardBit }
        };
        Assert.Equal("context 19 reason 2 changed 0x008 job 19 lvl 100 el 58/60 kl 0/0 board 1,0,2,0,3,0 chain 42.5 #12 group 3 statuses 1 dead 0 instance 1794130000",
            ContextLog.Line(record));
    }
}

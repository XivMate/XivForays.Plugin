using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using XivMate.DataGathering.Forays.Dalamud.Core;

namespace XivMate.DataGathering.Forays.Dalamud.Services;

/// <summary>
/// Reads the game's FATE reward window data from the reward agent's queue (FFXIVClientStructs AgentFateReward). Read
/// only: nothing is called, changed or hooked. The entry holds the FATE id, the medal (rank), experience, currency and
/// the listed items as numbers; its name text is never touched. Must run on the framework thread.
/// </summary>
internal static class FateRewardReader
{
    /// <summary>The reward entry at the front of the queue, or null when none is queued or the read looks wrong.</summary>
    public static unsafe RewardWindowRead? ReadFront()
    {
        var agent = AgentFateReward.Instance();
        if (agent == null)
            return null;
        var count = agent->Rewards.Count;
        if (count is <= 0 or > 16)
            return null;

        ref var reward = ref agent->Rewards[0];
        if (reward.Type != AgentFateReward.RewardType.FateReward)
            return null;

        var items = new List<RewardItem>();
        foreach (ref var item in reward.Items)
            if (item.ItemId != 0)
                items.Add(new RewardItem(item.ItemId, item.Amount, item.Flags));
        var extra = new List<RewardItem>();
        foreach (ref var item in reward.AdditionalItems)
            if (item.ItemId != 0)
                extra.Add(new RewardItem(item.ItemId, item.Amount, 0));

        var read = RewardWindowRead.FromFields(reward.Id, reward.IsSuccess, reward.EurekaFate, reward.Medal,
            reward.Experience, reward.ExperienceFlags, reward.CurrencyAmount, items, extra);
        return read.IsPlausible ? read : null;
    }
}

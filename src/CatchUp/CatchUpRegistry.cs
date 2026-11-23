using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace MidRunJoin.CatchUp;

/// <summary>
/// Default CallingBell-style catch-up flow:
/// Scans all completed rooms in MapPointHistory and builds a single native RewardsSet
/// using the joining player's own RNG (deterministic across all peers):
/// - Monster/Elite/Boss combats: native CardReward + rolled Gold (+ RelicReward for Elite, + CardRemovalReward for Boss)
/// - Unknown / Shop: +10 Gold each
/// - Treasure: rolled Treasure Gold + random RelicReward
/// - RestSite: floor(count / 2) CardUpgradeReward
/// - Ancient: 1 native "choose 1 of 3" AncientRelicChoiceReward per visited Ancient,
///            rolled strictly from that specific Ancient's relic pool!
/// All Gold is stacked into a single GoldReward slot.
/// </summary>
public sealed class HistoryBellCatchUpFlow : ICatchUpFlow
{
    public const string FlowId = "history_bell";

    public string Id => FlowId;
    public string DisplayName => ModLoc.FlowHistoryBellName;
    public string Description => ModLoc.FlowHistoryBellDesc;

    public List<Reward> BuildRewards(RunState state, Player guest)
    {
        var counts = CatchUpHistoryScanner.Scan(state);
        var breakdown = HistoryBellFormula.Compute(counts);
        var rng = guest.PlayerRng.Rewards;

        int totalGold = breakdown.FlatBonusGold;
        for (int i = 0; i < counts.MonsterCombats; i++)
            totalGold += rng.NextInt(HistoryBellFormula.MonsterGoldMin, HistoryBellFormula.MonsterGoldMaxExclusive);
        for (int i = 0; i < counts.EliteCombats; i++)
            totalGold += rng.NextInt(HistoryBellFormula.EliteGoldMin, HistoryBellFormula.EliteGoldMaxExclusive);
        for (int i = 0; i < counts.BossCombats; i++)
            totalGold += rng.NextInt(HistoryBellFormula.BossGoldMin, HistoryBellFormula.BossGoldMaxExclusive);
        for (int i = 0; i < counts.TreasureRooms; i++)
            totalGold += rng.NextInt(HistoryBellFormula.TreasureGoldMin, HistoryBellFormula.TreasureGoldMaxExclusive);

        var rewards = new List<Reward>();

        // 1. All gold stacked into one single GoldReward slot
        if (totalGold > 0)
            rewards.Add(new GoldReward(totalGold, guest));

        // 2. Random relics (Elites + Treasures)
        for (int i = 0; i < breakdown.RandomRelicRewards; i++)
            rewards.Add(new RelicReward(guest));

        // 3. Specific visited Ancients: 1 native "choose 1 of 3" reward per visited Ancient,
        //    drawn strictly from that specific Ancient's relic pool.
        var visitedAncientIds = CatchUpHistoryScanner.GetVisitedAncientIds(state);
        int ancientChoiceCount = 0;
        if (visitedAncientIds.Count > 0)
        {
            var excludedIds = new HashSet<ModelId>(guest.Relics.Select(r => r.Id));
            foreach (var ancientId in visitedAncientIds)
            {
                var ancient = ResolveAncient(ancientId);
                var pool = GetRelicPoolForAncient(ancient, state, guest, excludedIds);
                if (pool.Count == 0)
                    continue;

                var candidates = new List<RelicModel>();
                int pickCount = Math.Min(HistoryBellFormula.AncientRelicChoicesPerPick, pool.Count);
                for (int i = 0; i < pickCount; i++)
                {
                    int pick = rng.NextInt(pool.Count);
                    var chosen = pool[pick];
                    pool.RemoveAt(pick);
                    excludedIds.Add(chosen.Id);
                    candidates.Add(chosen);
                }

                if (candidates.Count > 0)
                {
                    rewards.Add(new AncientRelicChoiceReward(ancient, candidates, guest));
                    ancientChoiceCount++;
                }
            }
        }

        // 4. Card Upgrades (1 per 2 Rest Sites, floor)
        for (int i = 0; i < breakdown.CardUpgradeRewards; i++)
            rewards.Add(new CardUpgradeReward(guest));

        // 5. Card Removals (1 per Boss defeated)
        for (int i = 0; i < breakdown.CardRemovalRewards; i++)
            rewards.Add(new CardRemovalReward(guest));

        // 6. Combat Card Rewards (Monsters, Elites, Bosses)
        for (int i = 0; i < breakdown.MonsterCardRewards; i++)
            rewards.Add(new CardReward(CardCreationOptions.ForRoom(guest, RoomType.Monster), 3, guest));
        for (int i = 0; i < breakdown.EliteCardRewards; i++)
            rewards.Add(new CardReward(CardCreationOptions.ForRoom(guest, RoomType.Elite), 3, guest));
        for (int i = 0; i < breakdown.BossCardRewards; i++)
            rewards.Add(new CardReward(CardCreationOptions.ForRoom(guest, RoomType.Boss), 3, guest));

        Log.Info($"[MidRunJoin] HistoryBell built {rewards.Count} reward(s) for guest {guest.NetId}: gold={totalGold}, relics={breakdown.RandomRelicRewards}+{ancientChoiceCount} ancient(1-of-3), upgrades={breakdown.CardUpgradeRewards}, removals={breakdown.CardRemovalRewards}, cards={breakdown.TotalCardRewards}");
        return rewards;
    }

    public static AncientEventModel? ResolveAncient(ModelId? id)
    {
        if (id == null)
            return null;
        return ModelDb.AllAncients.FirstOrDefault(a => a.Id == id)
            ?? ModelDb.GetByIdOrNull<EventModel>(id) as AncientEventModel;
    }

    public static List<RelicModel> GetRelicPoolForAncient(
        AncientEventModel? ancient,
        RunState state,
        Player guest,
        HashSet<ModelId> excludedIds)
    {
        var byId = new Dictionary<ModelId, RelicModel>();
        if (ancient != null)
        {
            try
            {
                var mutableAncient = (AncientEventModel)ancient.ToMutable();
                AccessTools.Property(typeof(EventModel), nameof(EventModel.Owner))?.SetValue(mutableAncient, guest);
                foreach (var opt in mutableAncient.AllPossibleOptions)
                {
                    var relic = opt.Relic;
                    if (relic == null || excludedIds.Contains(relic.Id) || byId.ContainsKey(relic.Id))
                        continue;
                    if (!relic.IsAllowed(state))
                        continue;
                    byId[relic.Id] = relic.IsMutable ? relic : relic.ToMutable();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[MidRunJoin] Failed to inspect AllPossibleOptions for Ancient {ancient.Id.Entry}, falling back to global Ancient pool: {ex.Message}");
            }
        }

        if (byId.Count == 0)
        {
            foreach (var relic in ModelDb.AllRelics)
            {
                if (relic.Rarity == RelicRarity.Ancient && relic.IsAllowed(state) && !excludedIds.Contains(relic.Id))
                    byId[relic.Id] = relic.ToMutable();
            }
        }

        return byId.Values
            .OrderBy(r => r.Id.Entry, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>
/// Minimal catch-up flow: no extra rewards; guest joins directly with starter kit.
/// </summary>
public sealed class NoneCatchUpFlow : ICatchUpFlow
{
    public const string FlowId = "none";

    public string Id => FlowId;
    public string DisplayName => ModLoc.FlowNoneName;
    public string Description => ModLoc.FlowNoneDesc;

    public List<Reward> BuildRewards(RunState state, Player guest) => new();
}

/// <summary>
/// Registry of available catch-up flows, enabling UI selection and future configuration/extensions.
/// </summary>
public static class CatchUpRegistry
{
    private static readonly List<ICatchUpFlow> _flows = new()
    {
        new HistoryBellCatchUpFlow(),
        new NoneCatchUpFlow()
    };

    public static IReadOnlyList<ICatchUpFlow> All => _flows;
    public static ICatchUpFlow Default => _flows[0];

    public static void Register(ICatchUpFlow flow)
    {
        if (flow == null) throw new ArgumentNullException(nameof(flow));
        if (string.IsNullOrWhiteSpace(flow.Id)) throw new ArgumentException("Flow Id cannot be empty.", nameof(flow));
        int existing = _flows.FindIndex(f => f.Id.Equals(flow.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _flows[existing] = flow;
        else
            _flows.Add(flow);
    }

    public static ICatchUpFlow GetById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Default;
        return _flows.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Default;
    }

    public static int GetIndexById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return 0;
        int idx = _flows.FindIndex(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx : 0;
    }

    public static ICatchUpFlow GetByIndex(int index) =>
        index >= 0 && index < _flows.Count ? _flows[index] : Default;
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace MidRunJoin.CatchUp;

/// <summary>
/// Default catch-up flow:
/// Scans all visited rooms in MapPointHistory and delegates combat drops to STS2's native
/// <see cref="RewardsSet"/> room reward generator + <see cref="Hook.ModifyRewards"/> (so vanilla
/// and modded modifiers such as Vintage / BigGameHunter / custom reward hooks apply naturally),
/// then merges all generated rewards into a single reward screen for the joining player:
/// - Monster/Elite/Boss combats: native encounter Gold + Card/Relic/Potion/modded drops (+ CardRemoval per Boss)
/// - Unknown / Shop: +10 Gold each
/// - Treasure: rolled Treasure Gold + random RelicReward
/// - RestSite: floor(count / 2) CardUpgradeReward
/// - Ancient: 1 native "choose 1 of 3" AncientRelicChoiceReward per visited Ancient
/// All Gold is merged into a single GoldReward slot, and PotionRewards are capped to the player's potion slots.
/// </summary>
public sealed class HistoryBellCatchUpFlow : ICatchUpFlow
{
    public const string FlowId = "history_bell";

    private static readonly MethodInfo? GenerateRewardsForMethod =
        AccessTools.Method(typeof(RewardsSet), "GenerateRewardsFor", [typeof(Player), typeof(AbstractRoom)]);

    public string Id => FlowId;
    public string DisplayName => ModLoc.FlowHistoryBellName;
    public string Description => ModLoc.FlowHistoryBellDesc;

    public List<Reward> BuildRewards(RunState state, Player guest) =>
        BuildRewardsCore(state, guest, excludePlayerPresentId: null);

    public List<Reward> BuildDeltaRewards(RunState state, Player guest) =>
        BuildRewardsCore(state, guest, excludePlayerPresentId: guest.NetId);

    /// <summary>
    /// Safe-sync reward generation used when at least one peer in the lobby does NOT have MidRunJoin
    /// installed. Uses a detached deterministic <see cref="MegaCrit.Sts2.Core.Random.Rng"/> so neither
    /// <see cref="Player.PlayerRng"/> nor <see cref="RunState.SharedRelicGrabBag"/> is mutated, and
    /// emits only reward types natively broadcast by STS2's <c>RewardSynchronizer</c> (Gold, Cards,
    /// pre-picked Relics, Potions, Ancient 1-of-3 relics, and Card Removals), skipping
    /// <c>CardUpgradeReward</c> which relies on lockstep <c>PlayerChoiceSynchronizer</c>.
    /// </summary>
    public List<Reward> BuildSafeSyncRewards(RunState state, Player guest, bool isRestored)
    {
        ulong? excludeId = isRestored ? guest.NetId : null;
        var counts = CatchUpHistoryScanner.Scan(state, excludeId);
        var breakdown = HistoryBellFormula.Compute(counts);
        var rng = Compat.CreateDeterministicRng(
            state.Rng.StringSeed,
            (int)(guest.NetId & 0x7FFFFFFF),
            isRestored ? "MidRunJoin.SafeSyncDelta" : "MidRunJoin.SafeSync");

        int totalGold = breakdown.FlatBonusGold;
        var visitedCombats = CatchUpHistoryScanner.GetVisitedCombatRooms(state, excludeId);
        var cardRewards = new List<Reward>();
        int relicCount = counts.TreasureRooms;
        int potionCount = 0;

        foreach (var (roomType, _) in visitedCombats)
        {
            var (minGold, maxGold) = roomType switch
            {
                RoomType.Elite => (HistoryBellFormula.EliteGoldMin, HistoryBellFormula.EliteGoldMaxExclusive),
                RoomType.Boss => (HistoryBellFormula.BossGoldMin, HistoryBellFormula.BossGoldMaxExclusive),
                _ => (HistoryBellFormula.MonsterGoldMin, HistoryBellFormula.MonsterGoldMaxExclusive)
            };
            totalGold += rng.NextInt(minGold, maxGold);

            var cardReward = Compat.CreateCardReward(
                Compat.WithIsFromCombatIfSupported(CardCreationOptions.ForRoom(guest, roomType)),
                3,
                guest);
            cardReward.SetRng(rng);
            cardRewards.Add(cardReward);

            if (roomType == RoomType.Elite)
                relicCount++;

            if (potionCount < Math.Max(1, guest.MaxPotionCount) && rng.NextFloat() < 0.40f)
                potionCount++;
        }

        for (int i = 0; i < counts.TreasureRooms; i++)
            totalGold += rng.NextInt(HistoryBellFormula.TreasureGoldMin, HistoryBellFormula.TreasureGoldMaxExclusive);

        var rewards = new List<Reward>();

        if (totalGold > 0)
        {
            var goldReward = new GoldReward(totalGold, guest);
            goldReward.SetRng(rng);
            rewards.Add(goldReward);
        }

        var excludedRelics = new HashSet<ModelId>(guest.Relics.Select(r => r.Id));
        var safeRelicPool = BuildSafeRelicPool(state, guest, excludedRelics);
        for (int i = 0; i < relicCount && safeRelicPool.Count > 0; i++)
        {
            int pick = rng.NextInt(safeRelicPool.Count);
            var chosen = safeRelicPool[pick];
            safeRelicPool.RemoveAt(pick);
            excludedRelics.Add(chosen.Id);
            rewards.Add(new RelicReward(chosen.ToMutable(), guest));
        }

        var visitedAncientIds = CatchUpHistoryScanner.GetVisitedAncientIds(state, excludeId);
        int ancientChoiceCount = 0;
        if (visitedAncientIds.Count > 0)
        {
            foreach (var ancientId in visitedAncientIds)
            {
                var ancient = ResolveAncient(ancientId);
                var pool = GetRelicPoolForAncient(ancient, state, guest, excludedRelics);
                if (pool.Count == 0)
                    continue;

                var candidates = new List<RelicModel>();
                int pickCount = Math.Min(HistoryBellFormula.AncientRelicChoicesPerPick, pool.Count);
                for (int i = 0; i < pickCount; i++)
                {
                    int pick = rng.NextInt(pool.Count);
                    var chosen = pool[pick];
                    pool.RemoveAt(pick);
                    excludedRelics.Add(chosen.Id);
                    candidates.Add(chosen);
                }

                if (candidates.Count > 0)
                {
                    rewards.Add(new AncientRelicChoiceReward(ancient, candidates, guest));
                    ancientChoiceCount++;
                }
            }
        }

        for (int i = 0; i < breakdown.CardRemovalRewards; i++)
            rewards.Add(new CardRemovalReward(guest));

        for (int i = 0; i < potionCount; i++)
        {
            var potionReward = new PotionReward(guest);
            potionReward.SetRng(rng);
            rewards.Add(potionReward);
        }

        rewards.AddRange(cardRewards);

        Log.Info($"[MidRunJoin] HistoryBell built {rewards.Count} safe-sync reward(s) for guest {guest.NetId} (restored={isRestored}): gold={totalGold}, relics={relicCount}+{ancientChoiceCount} ancient(1-of-3), potions={potionCount}, removals={breakdown.CardRemovalRewards}, cards={cardRewards.Count}");
        return rewards;
    }

    private static List<RelicModel> BuildSafeRelicPool(
        RunState state,
        Player guest,
        HashSet<ModelId> excludedIds)
    {
        var pool = new List<RelicModel>();
        foreach (var relic in ModelDb.AllRelics)
        {
            if (relic.Rarity is not (RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare))
                continue;
            if (excludedIds.Contains(relic.Id))
                continue;
            if (!relic.IsAllowed(state))
                continue;
            if (relic.Pool != null &&
                guest.Character.RelicPool != null &&
                relic.Pool != guest.Character.RelicPool &&
                relic.Pool.GetType().Name.Contains("Character", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            pool.Add(relic);
        }
        pool.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id.Entry, b.Id.Entry));
        return pool;
    }

    private List<Reward> BuildRewardsCore(RunState state, Player guest, ulong? excludePlayerPresentId)
    {
        var counts = CatchUpHistoryScanner.Scan(state, excludePlayerPresentId);
        var breakdown = HistoryBellFormula.Compute(counts);
        var rng = guest.PlayerRng.Rewards;

        int totalGold = breakdown.FlatBonusGold;
        var relicRewards = new List<Reward>();
        var potionRewards = new List<Reward>();
        var cardAndOtherRewards = new List<Reward>();

        // 1. Combat rooms: invoke STS2's native room reward generation + Hook.ModifyRewards per visited combat
        var visitedCombats = CatchUpHistoryScanner.GetVisitedCombatRooms(state, excludePlayerPresentId);
        foreach (var (roomType, encounterId) in visitedCombats)
        {
            var roomRewards = GenerateNativeCombatRoomRewards(state, guest, roomType, encounterId);
            foreach (var reward in roomRewards)
            {
                if (reward is GoldReward gold)
                {
                    if (!gold.IsPopulated)
                        gold.Populate();
                    totalGold += Math.Max(0, gold.Amount);
                }
                else if (reward is PotionReward)
                {
                    if (potionRewards.Count < Math.Max(1, guest.MaxPotionCount))
                        potionRewards.Add(reward);
                }
                else if (reward is RelicReward)
                {
                    relicRewards.Add(reward);
                }
                else
                {
                    cardAndOtherRewards.Add(reward);
                }
            }
        }

        // 2. Treasure rooms: rolled treasure gold + random relic
        for (int i = 0; i < counts.TreasureRooms; i++)
        {
            totalGold += rng.NextInt(HistoryBellFormula.TreasureGoldMin, HistoryBellFormula.TreasureGoldMaxExclusive);
            relicRewards.Add(new RelicReward(guest));
        }

        var rewards = new List<Reward>();

        // Merged Gold (1 single slot)
        if (totalGold > 0)
            rewards.Add(new GoldReward(totalGold, guest));

        // Relics (from native Elite drops, Treasures, and modifiers like Vintage)
        rewards.AddRange(relicRewards);

        // 3. Specific visited Ancients: 1 native "choose 1 of 3" reward per visited Ancient,
        //    drawn strictly from that specific Ancient's relic pool.
        var visitedAncientIds = CatchUpHistoryScanner.GetVisitedAncientIds(state, excludePlayerPresentId);
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
            rewards.Add(Compat.CreateCardUpgradeReward(guest));

        // 5. Card Removals (1 per Boss defeated)
        for (int i = 0; i < breakdown.CardRemovalRewards; i++)
            rewards.Add(new CardRemovalReward(guest));

        // 6. Native Potion drops (capped to player's potion slots)
        rewards.AddRange(potionRewards);

        // 7. Native Card drops & any custom modded rewards
        rewards.AddRange(cardAndOtherRewards);

        Log.Info($"[MidRunJoin] HistoryBell built {rewards.Count} merged reward(s) for guest {guest.NetId}: gold={totalGold}, relics={relicRewards.Count}+{ancientChoiceCount} ancient(1-of-3), potions={potionRewards.Count}, upgrades={breakdown.CardUpgradeRewards}, removals={breakdown.CardRemovalRewards}, cards/other={cardAndOtherRewards.Count}");
        return rewards;
    }

    private static List<Reward> GenerateNativeCombatRoomRewards(
        RunState state,
        Player guest,
        RoomType roomType,
        ModelId? encounterId)
    {
        try
        {
            var encounter = ResolveEncounter(roomType, encounterId);
            if (encounter != null && GenerateRewardsForMethod != null && RunManager.Instance != null)
            {
                var combatRoom = new CombatRoom(encounter.ToMutable(), state);
                var rewardsSet = new RewardsSet(guest);
                var generated = (List<Reward>)GenerateRewardsForMethod.Invoke(rewardsSet, [guest, combatRoom])!;
                Hook.ModifyRewards(state, guest, generated, combatRoom);
                return generated;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[MidRunJoin] Native room reward generation failed for {roomType} ({encounterId?.Entry ?? "default"}), using fallback: {ex.Message}");
        }

        // Fallback if RunManager.Instance is unavailable (e.g. detached offline tests)
        var fallback = new List<Reward>();
        var (minGold, maxGold) = roomType switch
        {
            RoomType.Elite => (HistoryBellFormula.EliteGoldMin, HistoryBellFormula.EliteGoldMaxExclusive - 1),
            RoomType.Boss => (HistoryBellFormula.BossGoldMin, HistoryBellFormula.BossGoldMaxExclusive - 1),
            _ => (HistoryBellFormula.MonsterGoldMin, HistoryBellFormula.MonsterGoldMaxExclusive - 1)
        };
        fallback.Add(new GoldReward(minGold, maxGold, guest));
        fallback.Add(Compat.CreateCardReward(Compat.WithIsFromCombatIfSupported(CardCreationOptions.ForRoom(guest, roomType)), 3, guest));
        if (roomType == RoomType.Elite)
            fallback.Add(new RelicReward(guest));
        return fallback;
    }

    private static EncounterModel? ResolveEncounter(RoomType roomType, ModelId? encounterId)
    {
        if (encounterId != null)
        {
            try
            {
                var byId = SaveUtil.EncounterOrDeprecated(encounterId);
                if (byId != null && byId.RoomType == roomType)
                    return byId;
            }
            catch
            {
                // Fall through to room-type default.
            }
        }

        return ModelDb.AllEncounters
            .Where(e => e.RoomType == roomType)
            .OrderBy(e => e.Id.Entry, StringComparer.Ordinal)
            .FirstOrDefault();
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

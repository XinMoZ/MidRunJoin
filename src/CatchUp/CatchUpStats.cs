using System;
using System.Collections.Generic;

namespace MidRunJoin.CatchUp;

/// <summary>
/// Pure room-history counts extracted from a run's MapPointHistory.
/// Kept dependency-free so reward formulas can be unit-tested offline.
/// </summary>
public readonly record struct CatchUpRoomCounts(
    int MonsterCombats,
    int EliteCombats,
    int BossCombats,
    int UnknownAndShopRooms,
    int TreasureRooms,
    int RestSites,
    int AncientRooms);

/// <summary>
/// Pure breakdown of deterministic reward counts computed from <see cref="CatchUpRoomCounts"/>.
/// </summary>
public readonly record struct BellRewardBreakdown(
    int FlatBonusGold,
    int MonsterCardRewards,
    int EliteCardRewards,
    int BossCardRewards,
    int RandomRelicRewards,
    int AncientRelicRewards,
    int CardUpgradeRewards,
    int CardRemovalRewards)
{
    public int TotalCardRewards => MonsterCardRewards + EliteCardRewards + BossCardRewards;
    public int TotalRelicRewards => RandomRelicRewards + AncientRelicRewards;
}

public static class HistoryBellFormula
{
    public const int UnknownOrShopGold = 10;
    public const int MonsterGoldMin = 10;
    public const int MonsterGoldMaxExclusive = 21; // 10..20
    public const int EliteGoldMin = 25;
    public const int EliteGoldMaxExclusive = 36;   // 25..35
    public const int BossGoldMin = 95;
    public const int BossGoldMaxExclusive = 106;   // 95..105
    public const int TreasureGoldMin = 42;
    public const int TreasureGoldMaxExclusive = 54; // 42..53
    public const int AncientRelicsPerAncient = 1;
    public const int AncientRelicChoicesPerPick = 3;

    public static BellRewardBreakdown Compute(CatchUpRoomCounts counts) => new(
        FlatBonusGold: Math.Max(0, counts.UnknownAndShopRooms) * UnknownOrShopGold,
        MonsterCardRewards: Math.Max(0, counts.MonsterCombats),
        EliteCardRewards: Math.Max(0, counts.EliteCombats),
        BossCardRewards: Math.Max(0, counts.BossCombats),
        RandomRelicRewards: Math.Max(0, counts.EliteCombats) + Math.Max(0, counts.TreasureRooms),
        AncientRelicRewards: Math.Max(0, counts.AncientRooms) * AncientRelicsPerAncient,
        CardUpgradeRewards: Math.Max(0, counts.RestSites) / 2,
        CardRemovalRewards: Math.Max(0, counts.BossCombats));
}

/// <summary>
/// Pure HP synchronization formula for mid-run joining players:
/// averages the HP percentages of all existing players and multiplies by the joining player's MaxHp.
/// </summary>
public static class CatchUpHealthFormula
{
    public static int ComputeSyncedCurrentHp(IReadOnlyList<(int CurrentHp, int MaxHp)> existingPlayers, int guestMaxHp)
    {
        if (guestMaxHp <= 0)
            return 1;
        if (existingPlayers == null || existingPlayers.Count == 0)
            return guestMaxHp;

        double sumPct = 0.0;
        int validCount = 0;
        foreach (var (cur, max) in existingPlayers)
        {
            if (max <= 0)
                continue;
            sumPct += Math.Clamp((double)cur / max, 0.0, 1.0);
            validCount++;
        }

        if (validCount == 0)
            return guestMaxHp;

        double avgPct = sumPct / validCount;
        int target = (int)Math.Round(guestMaxHp * avgPct, MidpointRounding.AwayFromZero);
        return Math.Clamp(target, 1, guestMaxHp);
    }
}

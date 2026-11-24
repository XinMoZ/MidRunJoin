using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace MidRunJoin.CatchUp;

/// <summary>
/// Pluggable catch-up reward strategy interface for mid-run joining players.
/// Implementations produce a deterministic list of native STS2 <see cref="Reward"/> objects
/// offered via <see cref="MegaCrit.Sts2.Core.Commands.RewardsCmd.OfferCustom"/> (CallingBell-style).
/// </summary>
public interface ICatchUpFlow
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    List<Reward> BuildRewards(RunState state, Player guest);
    List<Reward> BuildDeltaRewards(RunState state, Player guest) => BuildRewards(state, guest);
    List<Reward> BuildSafeSyncRewards(RunState state, Player guest, bool isRestored) =>
        isRestored ? BuildDeltaRewards(state, guest) : BuildRewards(state, guest);
}

public static class CatchUpHistoryScanner
{
    /// <summary>
    /// Enumerates completed history entries along the host's route (seat 0).
    /// When a split-path mod allows teammates to visit different rooms, entries that contain
    /// player stats but do not include the host's NetId are excluded so catch-up rewards
    /// strictly follow the host's path instead of double-counting divergent teammate rooms.
    /// When <paramref name="excludePlayerPresentId"/> is specified (for a restored stashed player),
    /// floors where that player already participated are skipped so only the missed delta rooms remain.
    /// </summary>
    public static IEnumerable<(int ActIndex, MegaCrit.Sts2.Core.Runs.History.MapPointHistoryEntry Entry)> EnumerateHostHistoryEntries(
        RunState state,
        ulong? excludePlayerPresentId = null)
    {
        ulong hostId = state.Players.Count > 0 ? state.Players[0].NetId : 0;
        var hostEntries = new List<(int ActIndex, MegaCrit.Sts2.Core.Runs.History.MapPointHistoryEntry Entry)>();
        for (int actIdx = 0; actIdx < state.MapPointHistory.Count; actIdx++)
        {
            foreach (var entry in state.MapPointHistory[actIdx])
            {
                if (hostId != 0 &&
                    entry.PlayerStats.Count > 0 &&
                    !entry.PlayerStats.Any(ps => ps.PlayerId == hostId))
                {
                    continue;
                }
                hostEntries.Add((actIdx, entry));
            }
        }

        if (!excludePlayerPresentId.HasValue)
        {
            foreach (var item in hostEntries)
                yield return item;
            yield break;
        }

        // For a restored (stashed) player, only include host floors strictly AFTER the last floor
        // they actually participated in before rejoining. Backfilled placeholder entries (added by
        // Candidate.Prepare so unmodded peers never crash in MapPointHistoryEntry.GetEntry) have
        // MaxHp == 0, whereas real participated floors always record MaxHp > 0.
        ulong pid = excludePlayerPresentId.Value;
        bool hasRealHpStats = hostEntries.Any(h => h.Entry.PlayerStats.Any(ps => ps.PlayerId == pid && ps.MaxHp > 0));
        bool ParticipatedIn(MegaCrit.Sts2.Core.Runs.History.MapPointHistoryEntry entry) =>
            hasRealHpStats
                ? entry.PlayerStats.Any(ps => ps.PlayerId == pid && ps.MaxHp > 0)
                : entry.PlayerStats.Any(ps => ps.PlayerId == pid);

        int lastPresentBeforeRejoin = -1;
        for (int i = 0; i < hostEntries.Count - 1; i++)
        {
            if (ParticipatedIn(hostEntries[i].Entry))
                lastPresentBeforeRejoin = i;
        }

        int startIdx = lastPresentBeforeRejoin + 1;
        for (int i = startIdx; i < hostEntries.Count; i++)
        {
            if (ParticipatedIn(hostEntries[i].Entry))
                continue;
            yield return hostEntries[i];
        }
    }

    /// <summary>
    /// Counts all completed rooms recorded along the host's route in <see cref="RunState.MapPointHistory"/>.
    /// </summary>
    public static CatchUpRoomCounts Scan(RunState state, ulong? excludePlayerPresentId = null)
    {
        int monsters = 0;
        int elites = 0;
        int bosses = 0;
        int unknownOrShop = 0;
        int treasures = 0;
        int rests = 0;
        int ancients = 0;

        foreach (var (_, entry) in EnumerateHostHistoryEntries(state, excludePlayerPresentId))
        {
            switch (entry.MapPointType)
            {
                case MapPointType.Monster:
                    monsters++;
                    break;
                case MapPointType.Elite:
                    elites++;
                    break;
                case MapPointType.Boss:
                    bosses++;
                    break;
                case MapPointType.Shop:
                    unknownOrShop++;
                    break;
                case MapPointType.Treasure:
                    treasures++;
                    break;
                case MapPointType.RestSite:
                    rests++;
                    break;
                case MapPointType.Ancient:
                    ancients++;
                    break;
                case MapPointType.Unknown:
                    if (entry.HasRoomOfType(RoomType.Boss))
                        bosses++;
                    else if (entry.HasRoomOfType(RoomType.Elite))
                        elites++;
                    else if (entry.HasRoomOfType(RoomType.Monster))
                        monsters++;
                    else if (entry.HasRoomOfType(RoomType.Treasure))
                        treasures++;
                    else
                        unknownOrShop++;
                    break;
            }
        }

        // Standard Act 1 runs start with Neow; only apply fallback for fresh guests (not restored players).
        if (!excludePlayerPresentId.HasValue && ancients == 0 && state.ExtraFields.StartedWithNeow)
            ancients = 1;

        return new CatchUpRoomCounts(
            MonsterCombats: monsters,
            EliteCombats: elites,
            BossCombats: bosses,
            UnknownAndShopRooms: unknownOrShop,
            TreasureRooms: treasures,
            RestSites: rests,
            AncientRooms: ancients);
    }

    /// <summary>
    /// Returns the ordered list of specific Ancient <see cref="ModelId"/>s visited along the host's route in
    /// <see cref="RunState.MapPointHistory"/> (or <c>null</c> if a history entry lacked a ModelId).
    /// </summary>
    public static IReadOnlyList<ModelId?> GetVisitedAncientIds(RunState state, ulong? excludePlayerPresentId = null)
    {
        var ids = new List<ModelId?>();
        foreach (var (actIdx, entry) in EnumerateHostHistoryEntries(state, excludePlayerPresentId))
        {
            if (entry.MapPointType != MapPointType.Ancient)
                continue;
            var modelId = entry.Rooms.FirstOrDefault(r => r.ModelId != null)?.ModelId;
            if (modelId == null && actIdx == 0 && state.ExtraFields.StartedWithNeow)
                modelId = ModelDb.Event<Neow>().Id;
            ids.Add(modelId);
        }

        if (!excludePlayerPresentId.HasValue && ids.Count == 0 && state.ExtraFields.StartedWithNeow)
            ids.Add(ModelDb.Event<Neow>().Id);

        return ids;
    }

    /// <summary>
    /// Returns the ordered list of completed combat rooms (Monster / Elite / Boss) along the host's route in
    /// <see cref="RunState.MapPointHistory"/> along with their recorded <see cref="EncounterModel"/> ID (if present),
    /// so catch-up reward generation can invoke STS2's native room reward drop logic for each visited combat.
    /// </summary>
    public static List<(RoomType RoomType, ModelId? EncounterId)> GetVisitedCombatRooms(
        RunState state,
        ulong? excludePlayerPresentId = null)
    {
        var combats = new List<(RoomType, ModelId?)>();
        foreach (var (_, entry) in EnumerateHostHistoryEntries(state, excludePlayerPresentId))
        {
            RoomType? resolvedType = entry.MapPointType switch
            {
                MapPointType.Monster => RoomType.Monster,
                MapPointType.Elite => RoomType.Elite,
                MapPointType.Boss => RoomType.Boss,
                MapPointType.Unknown when entry.HasRoomOfType(RoomType.Boss) => RoomType.Boss,
                MapPointType.Unknown when entry.HasRoomOfType(RoomType.Elite) => RoomType.Elite,
                MapPointType.Unknown when entry.HasRoomOfType(RoomType.Monster) => RoomType.Monster,
                _ => null
            };
            if (!resolvedType.HasValue)
                continue;

            var matchingRoom = entry.FirstRoomOfType(resolvedType.Value);
            combats.Add((resolvedType.Value, matchingRoom?.ModelId));
        }
        return combats;
    }
}

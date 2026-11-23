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
}

public static class CatchUpHistoryScanner
{
    /// <summary>
    /// Counts all completed rooms recorded in <see cref="RunState.MapPointHistory"/>.
    /// </summary>
    public static CatchUpRoomCounts Scan(RunState state)
    {
        int monsters = 0;
        int elites = 0;
        int bosses = 0;
        int unknownOrShop = 0;
        int treasures = 0;
        int rests = 0;
        int ancients = 0;

        foreach (var actHistory in state.MapPointHistory)
        {
            foreach (var entry in actHistory)
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
        }

        // Standard Act 1 runs start with Neow; if history did not explicitly record an Ancient entry, count it once.
        if (ancients == 0 && state.ExtraFields.StartedWithNeow)
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
    /// Returns the ordered list of specific Ancient <see cref="ModelId"/>s visited in this run's
    /// <see cref="RunState.MapPointHistory"/> (or <c>null</c> if a history entry lacked a ModelId).
    /// </summary>
    public static IReadOnlyList<ModelId?> GetVisitedAncientIds(RunState state)
    {
        var ids = new List<ModelId?>();
        for (int actIdx = 0; actIdx < state.MapPointHistory.Count; actIdx++)
        {
            foreach (var entry in state.MapPointHistory[actIdx])
            {
                if (entry.MapPointType != MapPointType.Ancient)
                    continue;
                var modelId = entry.Rooms.FirstOrDefault(r => r.ModelId != null)?.ModelId;
                if (modelId == null && actIdx == 0 && state.ExtraFields.StartedWithNeow)
                    modelId = ModelDb.Event<Neow>().Id;
                ids.Add(modelId);
            }
        }

        if (ids.Count == 0 && state.ExtraFields.StartedWithNeow)
            ids.Add(ModelDb.Event<Neow>().Id);

        return ids;
    }

    /// <summary>
    /// Returns the ordered list of completed combat rooms (Monster / Elite / Boss) in
    /// <see cref="RunState.MapPointHistory"/> along with their recorded <see cref="EncounterModel"/> ID (if present),
    /// so catch-up reward generation can invoke STS2's native room reward drop logic for each visited combat.
    /// </summary>
    public static List<(RoomType RoomType, ModelId? EncounterId)> GetVisitedCombatRooms(RunState state)
    {
        var combats = new List<(RoomType, ModelId?)>();
        foreach (var actHistory in state.MapPointHistory)
        {
            foreach (var entry in actHistory)
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
        }
        return combats;
    }
}

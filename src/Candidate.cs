using System;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace MidRunJoin;

internal static class Candidate
{
    internal static SerializableRun Prepare(SerializableRun checkpoint, ulong guestId)
    {
        if (RunManager.Instance.IsInProgress || checkpoint.GameMode != GameMode.Standard ||
            checkpoint.IsCustomGame || checkpoint.DailyTime.HasValue || checkpoint.Modifiers.Count > 0)
            throw new InvalidOperationException("请先退出冒险，并选择标准联机存档。");
        if (checkpoint.Players.Count is < 1 or >= 4)
            throw new InvalidOperationException("本局没有可用席位。");
        if (checkpoint.Players.Any(p => p.NetId == guestId))
            throw new InvalidOperationException("这位玩家已在队伍中，可以直接重连。");
        var map = checkpoint.Acts.ElementAtOrDefault(checkpoint.CurrentActIndex)?.SavedMap;
        if (checkpoint.PreFinishedRoom != null || checkpoint.VisitedMapCoords.Count == 0 ||
            map?.Points.Any(p => p.Coord.Equals(checkpoint.VisitedMapCoords[^1]) &&
                                 p.PointType == MapPointType.Monster) != true)
            throw new InvalidOperationException("请在进入普通怪物节点后、出牌前退出到主菜单。");

        var type = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
        var candidate = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(checkpoint, type), type)!;
        var state = RunState.FromSerializable(candidate);
        var guest = Player.CreateForNewRun<Ironclad>(UnlockState.none, guestId);
        // Only the detached state gets a seat, seeded player streams and a boundary history entry.
        state.AddPlayerDebug(guest, -1);
        new AscensionManager(state.GameMode, state.AscensionLevel).ApplyEffectsTo(guest);
        var seed = StringHelper.GetDeterministicHashCode(state.Rng.StringSeed) + (ulong)checkpoint.Players.Count;
        guest.PopulateRelicGrabBagIfNecessary(new Rng(seed, "MidRunJoin.Relics"));
        candidate.Players.Add(guest.ToSerializable());
        return candidate;
    }
}

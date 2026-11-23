using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace MidRunJoin.CatchUp;

/// <summary>
/// Triggers CallingBell-style catch-up rewards via STS2's native <see cref="RewardsCmd.OfferCustom"/>
/// for newly joined guests after <see cref="NGame.LoadRun"/> completes.
///
/// Uses a sentinel value (240..255, fitting STS2's 8-bit <c>WriteInt(..., 8)</c> serialization)
/// in the guest's starter relic <see cref="MegaCrit.Sts2.Core.Models.RelicModel.FloorAddedToDeck"/>
/// that travels inside STS2's own <c>SerializableRun</c> lobby payload (zero custom packets,
/// zero room-jumping, zero Steam/ENet race conditions) and is restored to <c>1</c> immediately upon offering.
/// </summary>
[HarmonyPatch]
public static class CatchUpCoordinator
{
    private const int MarkerBase = 240;

    public static int EncodeFlowMarker(string flowId) =>
        MarkerBase + CatchUpRegistry.GetIndexById(flowId);

    public static bool TryConsumeMarker(Player player, out string flowId)
    {
        foreach (var relic in player.Relics)
        {
            if (relic.FloorAddedToDeck >= MarkerBase)
            {
                int idx = relic.FloorAddedToDeck - MarkerBase;
                relic.FloorAddedToDeck = 1;
                flowId = CatchUpRegistry.GetByIndex(idx).Id;
                return true;
            }
        }
        flowId = "";
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(NGame), nameof(NGame.LoadRun))]
    private static void AfterLoadRun(ref Task __result)
    {
        __result = OfferPendingCatchUpRewardsAsync(__result);
    }

    private static async Task OfferPendingCatchUpRewardsAsync(Task loadTask)
    {
        await loadTask;
        try
        {
            var state = RunManager.Instance.DebugOnlyGetState();
            if (state == null)
                return;

            // Offer in deterministic seat order on every peer so RewardsSetSynchronizer stays 100% aligned.
            var pending = new List<(Player Guest, string FlowId)>();
            foreach (var player in state.Players)
            {
                if (TryConsumeMarker(player, out string flowId))
                    pending.Add((player, flowId));
            }

            if (pending.Count == 0)
                return;

            foreach (var (guest, flowId) in pending)
            {
                var flow = CatchUpRegistry.GetById(flowId);
                var rewards = flow.BuildRewards(state, guest);
                if (rewards.Count == 0)
                {
                    Log.Info($"[MidRunJoin] CatchUp flow '{flow.Id}' produced 0 rewards for guest {guest.NetId}.");
                    continue;
                }
                Log.Info($"[MidRunJoin] Offering {rewards.Count} CallingBell-style catch-up reward(s) to guest {guest.NetId} (flow={flow.Id}).");
                _ = TaskHelper.RunSafely(RewardsCmd.OfferCustom(guest, rewards));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[MidRunJoin] Failed to offer catch-up rewards: {ex}");
        }
    }
}

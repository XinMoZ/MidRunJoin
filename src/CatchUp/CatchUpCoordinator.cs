using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace MidRunJoin.CatchUp;

/// <summary>
/// Triggers per-player run-start modifiers (those implementing <see cref="ModifierModel.GenerateNeowOption"/>)
/// and merged visited-room catch-up rewards via STS2's native <see cref="RewardsCmd.OfferCustom"/>
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

            // Offer in deterministic seat order on every peer so synchronizers stay 100% aligned.
            var pending = new List<(Player Guest, string FlowId)>();
            foreach (var player in state.Players)
            {
                if (TryConsumeMarker(player, out string flowId))
                    pending.Add((player, flowId));
            }

            if (pending.Count == 0)
                return;

            _ = TaskHelper.RunSafely(RunAllPendingGuestCatchUpsAsync(state, pending));
        }
        catch (Exception ex)
        {
            Log.Error($"[MidRunJoin] Failed to schedule catch-up rewards: {ex}");
        }
    }

    private static async Task RunAllPendingGuestCatchUpsAsync(
        RunState state,
        List<(Player Guest, string FlowId)> pending)
    {
        foreach (var (guest, flowId) in pending)
        {
            try
            {
                await ApplyPerPlayerRunStartModifiersAsync(state, guest);

                var flow = CatchUpRegistry.GetById(flowId);
                var rewards = flow.BuildRewards(state, guest);
                if (rewards.Count == 0)
                {
                    Log.Info($"[MidRunJoin] CatchUp flow '{flow.Id}' produced 0 rewards for guest {guest.NetId}.");
                    continue;
                }
                Log.Info($"[MidRunJoin] Offering {rewards.Count} merged catch-up reward(s) to guest {guest.NetId} (flow={flow.Id}).");
                _ = TaskHelper.RunSafely(RewardsCmd.OfferCustom(guest, rewards));
            }
            catch (Exception ex)
            {
                Log.Error($"[MidRunJoin] Failed catch-up for guest {guest.NetId}: {ex}");
            }
        }
    }

    /// <summary>
    /// Detects and executes per-player one-time run-start modifiers (such as AllStar, Specialized,
    /// Insanity, Draft, SealedDeck, or modded modifiers implementing <see cref="ModifierModel.GenerateNeowOption"/>)
    /// for a newly joined player. Run-wide <see cref="ModifierModel.AfterRunCreated"/> hooks are NOT
    /// re-executed because they are not scoped to an individual player.
    /// </summary>
    public static List<(ModifierModel Modifier, Func<Task> Action)> CollectPerPlayerRunStartModifiers(
        RunState state,
        Player guest)
    {
        var actions = new List<(ModifierModel, Func<Task>)>();
        if (state.Modifiers.Count == 0)
            return actions;

        var neowContext = (EventModel)ModelDb.Event<Neow>().ToMutable();
        AccessTools.Property(typeof(EventModel), nameof(EventModel.Owner))?.SetValue(neowContext, guest);
        AccessTools.Property(typeof(EventModel), nameof(EventModel.Rng))?.SetValue(neowContext, guest.PlayerRng.Rewards);

        foreach (var modifier in state.Modifiers)
        {
            try
            {
                var action = modifier.GenerateNeowOption(neowContext);
                if (action != null)
                    actions.Add((modifier, action));
            }
            catch (Exception ex)
            {
                Log.Warn($"[MidRunJoin] Skipping GenerateNeowOption for modifier {modifier.Id.Entry}: {ex.Message}");
            }
        }
        return actions;
    }

    private static async Task ApplyPerPlayerRunStartModifiersAsync(RunState state, Player guest)
    {
        var perPlayerModifiers = CollectPerPlayerRunStartModifiers(state, guest);
        if (perPlayerModifiers.Count == 0)
            return;

        if (perPlayerModifiers.Any(m => m.Modifier.ClearsPlayerDeck))
        {
            guest.Deck.Clear();
            Log.Info($"[MidRunJoin] Cleared starter deck for guest {guest.NetId} due to ClearsPlayerDeck modifier(s).");
        }

        foreach (var (modifier, action) in perPlayerModifiers)
        {
            try
            {
                Log.Info($"[MidRunJoin] Executing per-player run-start modifier '{modifier.Id.Entry}' for guest {guest.NetId}.");
                await action();
            }
            catch (Exception ex)
            {
                Log.Warn($"[MidRunJoin] Per-player modifier '{modifier.Id.Entry}' failed for guest {guest.NetId}: {ex.Message}");
            }
        }
    }
}

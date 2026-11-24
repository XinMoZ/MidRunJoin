using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
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
/// zero room-jumping, zero Steam/ENet race conditions) and is restored to <c>1</c> immediately upon offering:
/// - 240..243: Full catch-up (fresh guest)
/// - 244..247: Safe-sync catch-up (fresh guest, when an unmodded peer is present)
/// - 248..251: Full catch-up (restored stashed player)
/// - 252..255: Safe-sync catch-up (restored stashed player, when an unmodded peer is present)
/// </summary>
[HarmonyPatch]
public static class CatchUpCoordinator
{
    private const int MarkerBase = 240;
    private const int SafeSyncFreshBase = 244;
    private const int RestoredMarkerBase = 248;
    private const int SafeSyncRestoredBase = 252;
    private const int MaxFlowSlotsPerBand = 4;

    /// <summary>
    /// Recorded right before <c>LoadRunLobby.BeginRunLocally</c>. When <c>false</c> (at least one peer
    /// in the lobby is unmodded or lacks MidRunJoin), catch-up automatically runs in safe-sync mode:
    /// only the local joining guest (<see cref="LocalContext.IsMe(Player)"/>) runs catch-up, using
    /// detached RNG and <c>RewardSynchronizer</c>-native rewards so unmodded peers never desync.
    /// </summary>
    internal static bool LastLobbyAllPeersModded { get; set; } = true;

    public static int EncodeFlowMarker(string flowId, bool isRestored = false, bool safeSync = false)
    {
        int idx = CatchUpRegistry.GetIndexById(flowId);
        if (idx < 0 || idx >= MaxFlowSlotsPerBand)
            idx = 0;
        int bandBase = (isRestored, safeSync) switch
        {
            (false, false) => MarkerBase,
            (false, true) => SafeSyncFreshBase,
            (true, false) => RestoredMarkerBase,
            (true, true) => SafeSyncRestoredBase
        };
        return bandBase + idx;
    }

    public static bool TryConsumeMarker(Player player, out string flowId) =>
        TryConsumeMarker(player, out flowId, out _, out _);

    public static bool TryConsumeMarker(Player player, out string flowId, out bool isRestored) =>
        TryConsumeMarker(player, out flowId, out isRestored, out _);

    public static bool TryConsumeMarker(Player player, out string flowId, out bool isRestored, out bool safeSync)
    {
        foreach (var relic in player.Relics)
        {
            if (relic.FloorAddedToDeck >= MarkerBase)
            {
                int raw = relic.FloorAddedToDeck;
                relic.FloorAddedToDeck = 1;
                DecodeMarker(raw, out int idx, out isRestored, out safeSync);
                flowId = CatchUpRegistry.GetByIndex(idx).Id;
                return true;
            }
        }
        flowId = "";
        isRestored = false;
        safeSync = false;
        return false;
    }

    public static void DecodeMarker(int marker, out int flowIndex, out bool isRestored, out bool safeSync)
    {
        if (marker >= SafeSyncRestoredBase)
        {
            flowIndex = marker - SafeSyncRestoredBase;
            isRestored = true;
            safeSync = true;
        }
        else if (marker >= RestoredMarkerBase)
        {
            flowIndex = marker - RestoredMarkerBase;
            isRestored = true;
            safeSync = false;
        }
        else if (marker >= SafeSyncFreshBase)
        {
            flowIndex = marker - SafeSyncFreshBase;
            isRestored = false;
            safeSync = true;
        }
        else
        {
            flowIndex = Math.Max(0, marker - MarkerBase);
            isRestored = false;
            safeSync = false;
        }
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

            bool forceSafeSync = !LastLobbyAllPeersModded;
            LastLobbyAllPeersModded = true;

            // Offer in deterministic seat order on every peer so synchronizers stay 100% aligned.
            var pending = new List<(Player Guest, string FlowId, bool IsRestored, bool SafeSync)>();
            foreach (var player in state.Players)
            {
                if (TryConsumeMarker(player, out string flowId, out bool isRestored, out bool safeSync))
                    pending.Add((player, flowId, isRestored, safeSync || forceSafeSync));
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
        List<(Player Guest, string FlowId, bool IsRestored, bool SafeSync)> pending)
    {
        foreach (var (guest, flowId, isRestored, safeSync) in pending)
        {
            try
            {
                if (safeSync)
                {
                    // In safe-sync mode (at least one peer lacks MidRunJoin), ONLY the joining guest
                    // locally generates & opens safe-sync rewards (detached RNG, RewardSynchronizer-only
                    // types). Non-local peers do nothing so unmodded peers and unmodded guests never desync.
                    if (!LocalContext.IsMe(guest))
                    {
                        Log.Info($"[MidRunJoin] Safe-sync mode: skipping non-local catch-up for guest {guest.NetId}.");
                        continue;
                    }

                    var safeFlow = CatchUpRegistry.GetById(flowId);
                    var safeRewards = safeFlow.BuildSafeSyncRewards(state, guest, isRestored);
                    if (safeRewards.Count == 0)
                    {
                        Log.Info($"[MidRunJoin] Safe-sync flow '{safeFlow.Id}' (restored={isRestored}) produced 0 rewards for guest {guest.NetId}.");
                        continue;
                    }
                    Log.Info($"[MidRunJoin] Offering {safeRewards.Count} safe-sync catch-up reward(s) locally to guest {guest.NetId} (flow={safeFlow.Id}, restored={isRestored}).");
                    _ = TaskHelper.RunSafely(RewardsCmd.OfferCustom(guest, safeRewards));
                    continue;
                }

                // Only fresh guests trigger one-time run-start modifiers; restored stashed players already ran them.
                if (!isRestored)
                    await ApplyPerPlayerRunStartModifiersAsync(state, guest);

                var flow = CatchUpRegistry.GetById(flowId);
                var rewards = isRestored
                    ? flow.BuildDeltaRewards(state, guest)
                    : flow.BuildRewards(state, guest);
                if (rewards.Count == 0)
                {
                    Log.Info($"[MidRunJoin] CatchUp flow '{flow.Id}' (restored={isRestored}) produced 0 rewards for guest {guest.NetId}.");
                    continue;
                }
                Log.Info($"[MidRunJoin] Offering {rewards.Count} merged catch-up reward(s) to guest {guest.NetId} (flow={flow.Id}, restored={isRestored}).");
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

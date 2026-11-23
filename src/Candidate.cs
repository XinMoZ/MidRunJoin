using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using MidRunJoin.CatchUp;

namespace MidRunJoin;

public readonly record struct GuestSpec(ulong GuestId, CharacterModel Character);

internal static class Candidate
{
    internal static SerializableRun Prepare(SerializableRun checkpoint, ulong guestId) =>
        Prepare(checkpoint, [new GuestSpec(guestId, ModelDb.Character<Ironclad>())], null, null);

    internal static SerializableRun Prepare(SerializableRun checkpoint, ulong guestId, CharacterModel character) =>
        Prepare(checkpoint, [new GuestSpec(guestId, character)], null, null);

    internal static SerializableRun Prepare(
        SerializableRun checkpoint,
        IReadOnlyList<GuestSpec> guests,
        IReadOnlyCollection<ulong>? removedPlayerIds = null,
        string? catchUpFlowId = null)
    {
        if (RunManager.Instance.IsInProgress)
            throw new InvalidOperationException(ModLoc.ErrExitRunFirst);
        if (checkpoint.GameMode == GameMode.Daily || checkpoint.DailyTime.HasValue)
            throw new InvalidOperationException(ModLoc.ErrDailyNotSupported);
        if (checkpoint.Players.Count < 1)
            throw new InvalidOperationException(ModLoc.ErrEmptySave);

        var addList = guests ?? Array.Empty<GuestSpec>();
        var removeSet = removedPlayerIds != null ? new HashSet<ulong>(removedPlayerIds) : new HashSet<ulong>();

        if (addList.Count == 0 && removeSet.Count == 0)
            throw new InvalidOperationException(ModLoc.ErrNoRosterDelta);

        ulong hostId = checkpoint.Players[0].NetId;
        if (removeSet.Contains(hostId))
            throw new InvalidOperationException(ModLoc.ErrCannotRemoveHost);

        var existingIds = new HashSet<ulong>(checkpoint.Players.Select(p => p.NetId));
        foreach (ulong rid in removeSet)
        {
            if (!existingIds.Contains(rid))
                throw new InvalidOperationException(ModLoc.ErrPlayerNotInSave(rid));
        }

        var remainingIds = new HashSet<ulong>(checkpoint.Players.Where(p => !removeSet.Contains(p.NetId)).Select(p => p.NetId));
        if (remainingIds.Count == 0)
            throw new InvalidOperationException(ModLoc.ErrNeedOneRemaining);

        foreach (var guest in addList)
        {
            if (guest.GuestId == 0)
                throw new InvalidOperationException(ModLoc.ErrZeroPlayerId);
            if (!remainingIds.Add(guest.GuestId))
                throw new InvalidOperationException(ModLoc.ErrGuestAlreadyInParty(guest.GuestId));
        }

        var type = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
        var candidate = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(checkpoint, type), type)!;

        // 1. Apply removals (preserve original seat order of remaining players)
        if (removeSet.Count > 0)
        {
            candidate.Players.RemoveAll(p => removeSet.Contains(p.NetId));
            if (candidate.PreFinishedRoom?.ExtraRewards != null)
            {
                foreach (ulong rid in removeSet)
                    candidate.PreFinishedRoom.ExtraRewards.Remove(rid);
            }
        }

        // 2. Apply additions on a detached RunState so original players' RNG & shared relic bag remain untouched
        if (addList.Count > 0)
        {
            var existingHpSnapshot = candidate.Players.Select(p => (p.CurrentHp, p.MaxHp)).ToList();
            var state = RunState.FromSerializable(candidate);
            var ascension = Compat.CreateAscensionManager(state.GameMode, state.AscensionLevel);
            int seatOffset = candidate.Players.Count;
            for (int i = 0; i < addList.Count; i++)
            {
                var spec = addList[i];
                var guest = Player.CreateForNewRun(spec.Character, UnlockState.none, spec.GuestId);
                state.AddPlayerDebug(guest, -1);
                ascension.ApplyEffectsTo(guest);
                int syncedHp = CatchUpHealthFormula.ComputeSyncedCurrentHp(existingHpSnapshot, guest.Creature.MaxHp);
                guest.Creature.SetCurrentHpInternal(syncedHp);
                guest.PopulateRelicGrabBagIfNecessary(Compat.CreateDeterministicRng(state.Rng.StringSeed, seatOffset + i, "MidRunJoin.Relics"));
                var serializedGuest = guest.ToSerializable();
                bool hasCatchUpFlow = !string.IsNullOrWhiteSpace(catchUpFlowId) &&
                    !catchUpFlowId.Equals(NoneCatchUpFlow.FlowId, StringComparison.OrdinalIgnoreCase);
                if ((hasCatchUpFlow || candidate.Modifiers.Count > 0) && serializedGuest.Relics.Count > 0)
                {
                    string markerFlow = hasCatchUpFlow ? catchUpFlowId! : NoneCatchUpFlow.FlowId;
                    serializedGuest.Relics[0].FloorAddedToDeck = CatchUpCoordinator.EncodeFlowMarker(markerFlow);
                }
                candidate.Players.Add(serializedGuest);

                // Ensure both the latest MapPointHistoryEntry (CurrentMapPointHistoryEntry) and the host's
                // latest entry (in case a split-path mod records separate per-player route entries) have a
                // PlayerStats slot for the newly added guest.
                var latestActHistory = candidate.MapPointHistory.LastOrDefault(act => act.Count > 0);
                var latestEntry = latestActHistory?.LastOrDefault();
                if (latestEntry != null && !latestEntry.PlayerStats.Any(ps => ps.PlayerId == spec.GuestId))
                {
                    latestEntry.PlayerStats.Add(new PlayerMapPointHistoryEntry { PlayerId = spec.GuestId });
                }
                var latestHostEntry = latestActHistory?.LastOrDefault(e => e.PlayerStats.Any(ps => ps.PlayerId == hostId));
                if (latestHostEntry != null && !ReferenceEquals(latestHostEntry, latestEntry) &&
                    !latestHostEntry.PlayerStats.Any(ps => ps.PlayerId == spec.GuestId))
                {
                    latestHostEntry.PlayerStats.Add(new PlayerMapPointHistoryEntry { PlayerId = spec.GuestId });
                }
            }
        }

        return candidate;
    }
}

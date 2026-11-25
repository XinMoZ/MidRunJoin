using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using MidRunJoin.CatchUp;

namespace MidRunJoin;

public readonly record struct GuestSpec(
    ulong GuestId,
    CharacterModel Character,
    SerializablePlayer? RestoredPlayer = null);

internal static class Candidate
{
    /// <summary>
    /// Returns <c>true</c> when loading <paramref name="run"/> enters directly into the start of a combat
    /// (<see cref="SerializableRun.PreFinishedRoom"/> is <c>null</c> and the latest visited map point is
    /// a Monster, Elite, or Boss encounter). Offering catch-up rewards during an active combat mutates
    /// combat decks/relics mid-fight and causes multiplayer desync, so catch-up is blocked in this state.
    /// </summary>
    internal static bool IsCombatStartSave(SerializableRun run)
    {
        if (run.PreFinishedRoom != null)
            return false;
        if (run.VisitedMapCoords == null || run.VisitedMapCoords.Count == 0)
            return false;

        var latestCoord = run.VisitedMapCoords[^1];
        if (run.CurrentActIndex >= 0 && run.CurrentActIndex < run.Acts.Count)
        {
            var savedMap = run.Acts[run.CurrentActIndex].SavedMap;
            if (savedMap != null)
            {
                if (savedMap.BossPoint != null && savedMap.BossPoint.Coord.Equals(latestCoord))
                    return true;
                if (savedMap.SecondBossPoint != null && savedMap.SecondBossPoint.Coord.Equals(latestCoord))
                    return true;
                if (savedMap.StartingPoint != null && savedMap.StartingPoint.Coord.Equals(latestCoord))
                    return IsCombatPointType(savedMap.StartingPoint.PointType);
                var point = savedMap.Points?.FirstOrDefault(p => p.Coord.Equals(latestCoord));
                if (point != null)
                    return IsCombatPointType(point.PointType);
            }
        }

        var latestEntry = run.MapPointHistory?.LastOrDefault(act => act.Count > 0)?.LastOrDefault();
        return latestEntry != null && IsCombatPointType(latestEntry.MapPointType);
    }

    private static bool IsCombatPointType(MapPointType pt) =>
        pt is MapPointType.Monster or MapPointType.Elite or MapPointType.Boss;

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

        bool isCombatStart = IsCombatStartSave(checkpoint);
        bool hasCatchUpFlow = !string.IsNullOrWhiteSpace(catchUpFlowId) &&
            !catchUpFlowId.Equals(NoneCatchUpFlow.FlowId, StringComparison.OrdinalIgnoreCase);
        if (addList.Count > 0 && hasCatchUpFlow && isCombatStart)
            throw new InvalidOperationException(ModLoc.ErrCombatStartCatchUpBlocked);

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
                SerializablePlayer serializedGuest;
                bool isRestored = spec.RestoredPlayer != null;

                if (isRestored)
                {
                    // Clone the stashed player's personal progress (deck, relics, potions, gold, HP, RNG, grab bag)
                    var playerTypeInfo = JsonSerializationUtility.GetTypeInfo<SerializablePlayer>();
                    serializedGuest = JsonSerializer.Deserialize(
                        JsonSerializer.SerializeToUtf8Bytes(spec.RestoredPlayer!, playerTypeInfo),
                        playerTypeInfo)!;
                    serializedGuest.NetId = spec.GuestId;
                }
                else
                {
                    var guestUnlockState = state.UnlockState
                        ?? (state.Players.Count > 0 ? state.Players[0].UnlockState : UnlockState.all);
                    var guest = Player.CreateForNewRun(spec.Character, guestUnlockState, spec.GuestId);
                    state.AddPlayerDebug(guest, -1);
                    ascension.ApplyEffectsTo(guest);
                    int syncedHp = CatchUpHealthFormula.ComputeSyncedCurrentHp(existingHpSnapshot, guest.Creature.MaxHp);
                    guest.Creature.SetCurrentHpInternal(syncedHp);
                    guest.PopulateRelicGrabBagIfNecessary(Compat.CreateDeterministicRng(state.Rng.StringSeed, seatOffset + i, "MidRunJoin.Relics"));
                    serializedGuest = guest.ToSerializable();
                }

                if (!isCombatStart && (hasCatchUpFlow || (!isRestored && candidate.Modifiers.Count > 0)) && serializedGuest.Relics.Count > 0)
                {
                    string markerFlow = hasCatchUpFlow ? catchUpFlowId! : NoneCatchUpFlow.FlowId;
                    serializedGuest.Relics[0].FloorAddedToDeck = CatchUpCoordinator.EncodeFlowMarker(markerFlow, isRestored);
                }
                candidate.Players.Add(serializedGuest);

                // Backfill missing PlayerStats entries across all acts/floors so unmodded peers
                // (existing teammates or unmodded guests) never crash in MapPointHistoryEntry.GetEntry
                // during Game Over settlement or Run History hover tips.
                // - The current join/rejoin boundary entry receives the guest's actual MaxHp/CurrentHp (> 0).
                // - Earlier/missed floors receive empty placeholders (MaxHp == 0) so CatchUpHistoryScanner
                //   can still distinguish real participated floors when computing delta catch-up rewards.
                var latestActHistory = candidate.MapPointHistory.LastOrDefault(act => act.Count > 0);
                var latestEntry = latestActHistory?.LastOrDefault();
                var latestHostEntry = latestActHistory?.LastOrDefault(e => e.PlayerStats.Any(ps => ps.PlayerId == hostId));
                foreach (var actHistory in candidate.MapPointHistory)
                {
                    foreach (var entry in actHistory)
                    {
                        bool isBoundary = ReferenceEquals(entry, latestEntry) || ReferenceEquals(entry, latestHostEntry);
                        var existingStat = entry.PlayerStats.FirstOrDefault(ps => ps.PlayerId == spec.GuestId);
                        if (existingStat == null)
                        {
                            entry.PlayerStats.Add(new PlayerMapPointHistoryEntry
                            {
                                PlayerId = spec.GuestId,
                                MaxHp = isBoundary ? serializedGuest.MaxHp : 0,
                                CurrentHp = isBoundary ? serializedGuest.CurrentHp : 0
                            });
                        }
                        else if (isBoundary && existingStat.MaxHp == 0)
                        {
                            existingStat.MaxHp = serializedGuest.MaxHp;
                            existingStat.CurrentHp = serializedGuest.CurrentHp;
                        }
                    }
                }
            }
        }

        return candidate;
    }
}

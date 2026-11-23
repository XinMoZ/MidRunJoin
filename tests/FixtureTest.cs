using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using MidRunJoin;
using MidRunJoin.CatchUp;

namespace MidRunJoinSmoke;

[ModInitializer(nameof(Init)), HarmonyPatch]
public static class FixtureTest
{
    private static bool lobbyChecked;
    private static int checkCount;
    private static string expectedGuest3Char = "";
    private static string expectedGuest4Char = "";

    public static void Init()
    {
        if (CommandLineHelper.HasArg("midrunjoin-smoke") && CommandLineHelper.HasArg("fastmp"))
            new Harmony("xinmo.MidRunJoin.Smoke").CreateClassProcessor(typeof(FixtureTest)).Patch();
        if (CommandLineHelper.HasArg("midrunjoin-e2e"))
            E2ETest.Init();
    }

    [HarmonyPrefix, HarmonyPatch(typeof(NMainMenu), "CheckCommandLineArgs")]
    private static bool OpenFixture(NMainMenu __instance)
    {
        try
        {
            var menu = (NMultiplayerSubmenu)AccessTools.Method(typeof(NMainMenu), "OpenMultiplayerSubmenu")
                .Invoke(__instance, null)!;
            var checkpoint = CreateMapCheckpoint([1, 2], ascension: 0, hostGoldPerFloor: [45, 75]);

            CheckRejectionAndAllSaveStatesMatrix(checkpoint);
            CheckPlayerRemovalAndCombinedEdit(checkpoint);
            CheckMultiGuestAndAllCharacters();
            CheckCatchUpFlowsAndRegistry(checkpoint);
            CheckAscensionScaling();
            CheckZeroImpactAndDeterminism(checkpoint);
            CheckMultiGuestDialogAndSelection(menu, checkpoint);
        }
        catch (Exception e)
        {
            Log.Error($"[MidRunJoinSmoke] FAIL: {e}");
            __instance.GetTree().Quit(1);
        }
        return false;
    }

    private static MethodInfo GetPrepareMulti() =>
        AccessTools.Method(AccessTools.TypeByName("MidRunJoin.Candidate"), "Prepare",
            [typeof(SerializableRun), typeof(IReadOnlyList<GuestSpec>), typeof(IReadOnlyCollection<ulong>), typeof(string)])!;

    private static SerializableRun InvokePrepare(SerializableRun run, IReadOnlyList<GuestSpec> guests, IReadOnlyCollection<ulong>? removedIds = null, string? flowId = null)
    {
        try
        {
            return (SerializableRun)GetPrepareMulti().Invoke(null, [run, guests, removedIds, flowId])!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    private static SerializableRun InvokePrepare(SerializableRun run, params GuestSpec[] guests) =>
        InvokePrepare(run, (IReadOnlyList<GuestSpec>)guests, null, null);

    private static SerializableRun CloneRun(SerializableRun run)
    {
        var type = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
        return System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(run, type), type)!;
    }

    private static void ExpectInvalidOp(Action action, string caseName)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            checkCount++;
            return;
        }
        throw new InvalidOperationException($"Expected InvalidOperationException for case: {caseName}");
    }

    private static void CheckRejectionAndAllSaveStatesMatrix(SerializableRun validMapCp)
    {
        var ironclad = ModelDb.Character<Ironclad>();

        // 1. Daily run still rejected (leaderboard integrity)
        var nonStd = CloneRun(validMapCp);
        nonStd.GameMode = GameMode.Daily;
        ExpectInvalidOp(() => InvokePrepare(nonStd, new GuestSpec(3, ironclad)), "GameMode == Daily");

        // 2. Custom game flag is now ACCEPTED (for Custom Mode & gameplay mods!)
        var custom = CloneRun(validMapCp);
        custom.IsCustomGame = true;
        var customCand = InvokePrepare(custom, new GuestSpec(3, ironclad));
        Require(customCand.Players.Count == 3 && customCand.IsCustomGame, "Custom game save should be accepted");

        // 3. DailyTime present still rejected
        var daily = CloneRun(validMapCp);
        daily.DailyTime = DateTimeOffset.UtcNow;
        ExpectInvalidOp(() => InvokePrepare(daily, new GuestSpec(3, ironclad)), "DailyTime.HasValue");

        // 4. Modifiers present (e.g. gameplay mods / custom rules) are now ACCEPTED
        var firstMod = ModelDb.GoodModifiers.FirstOrDefault();
        if (firstMod != null)
        {
            var modded = CloneRun(validMapCp);
            modded.Modifiers.Add(firstMod.ToMutable().ToSerializable());
            var moddedCand = InvokePrepare(modded, new GuestSpec(3, ironclad));
            Require(moddedCand.Players.Count == 3 && moddedCand.Modifiers.Count == 1, "Modded/modifier save should be accepted");
        }

        // 5. Neither adding nor removing anyone
        ExpectInvalidOp(() => InvokePrepare(validMapCp), "No changes");

        // 6. Zero players in checkpoint
        var zero = CloneRun(validMapCp);
        zero.Players.Clear();
        ExpectInvalidOp(() => InvokePrepare(zero, new GuestSpec(3, ironclad)), "Players.Count == 0");

        // 7. Duplicate existing player IDs & duplicate within guests & zero ID
        ExpectInvalidOp(() => InvokePrepare(validMapCp, new GuestSpec(1, ironclad)), "Duplicate seat 1");
        ExpectInvalidOp(() => InvokePrepare(validMapCp, new GuestSpec(2, ironclad)), "Duplicate seat 2");
        ExpectInvalidOp(() => InvokePrepare(validMapCp, new GuestSpec(3, ironclad), new GuestSpec(3, ironclad)), "Duplicate within guests");
        ExpectInvalidOp(() => InvokePrepare(validMapCp, new GuestSpec(0, ironclad)), "GuestId == 0");

        // 8. ALL SAVE STATES ACCEPTED: mid-combat (PreFinishedRoom == null && VisitedMapCoords > 0), act-start map, and PreFinishedRoom != null
        var midCombat = CloneRun(validMapCp);
        midCombat.PreFinishedRoom = null;
        Require(midCombat.VisitedMapCoords.Count > 0, "midCombat should have visited coords");
        var midCombatCand = InvokePrepare(midCombat, new GuestSpec(3, ironclad));
        Require(midCombatCand.Players.Count == 3, "Mid-combat save should now be accepted");

        var actStart = CloneRun(validMapCp);
        actStart.PreFinishedRoom = null;
        actStart.VisitedMapCoords.Clear();
        var actStartCand = InvokePrepare(actStart, new GuestSpec(3, ironclad));
        Require(actStartCand.Players.Count == 3, "Act-start map save should be accepted");

        Log.Info($"[MidRunJoinSmoke] PASS: RejectionAndAllSaveStatesMatrix ({checkCount} checks)");
    }

    private static void CheckPlayerRemovalAndCombinedEdit(SerializableRun cp2)
    {
        var silent = ModelDb.Character<Silent>();
        var cp3 = CreateMapCheckpoint([1, 2, 3], ascension: 0);

        // Cannot remove host (seat 0)
        ExpectInvalidOp(() => InvokePrepare(cp3, [], [1UL]), "Removing host must be rejected");
        // Cannot remove non-existent player
        ExpectInvalidOp(() => InvokePrepare(cp3, [], [99UL]), "Removing unknown player must be rejected");

        // Pure removal: 3P -> 2P (remove player 2)
        string before3 = JsonSerializationUtility.ToJson(cp3);
        var removed2 = InvokePrepare(cp3, [], [2UL]);
        Require(before3 == JsonSerializationUtility.ToJson(cp3), "Removal mutated original checkpoint");
        Require(removed2.Players.Select(p => p.NetId).SequenceEqual(new ulong[] { 1, 3 }), "3P->2P seat order mismatch");

        // Pure removal: 3P -> 1P (remove players 2 and 3)
        var removed23 = InvokePrepare(cp3, [], [2UL, 3UL]);
        Require(removed23.Players.Select(p => p.NetId).SequenceEqual(new ulong[] { 1 }), "3P->1P seat order mismatch");

        // Combined edit: remove player 2 AND add player 4 (Silent) simultaneously
        var swapped = InvokePrepare(cp3, [new GuestSpec(4, silent)], [2UL]);
        var restored = RunState.FromSerializable(swapped);
        Require(restored.Players.Select(p => p.NetId).SequenceEqual(new ulong[] { 1, 3, 4 }), "Combined remove+add seat order mismatch");
        Require(restored.Players[2].Character.Id == silent.Id, "Combined add guest char mismatch");

        Log.Info($"[MidRunJoinSmoke] PASS: PlayerRemoval & CombinedEdit ({checkCount} checks)");
    }

    private static void CheckMultiGuestAndAllCharacters()
    {
        var allChars = ModelDb.AllCharacters.ToArray();
        Require(allChars.Length >= 3, $"Expected >=3 characters, got {allChars.Length}");

        // 1P -> +3 guests (4P total, 3 distinct characters!)
        var cp1 = CreateMapCheckpoint([1], ascension: 0);
        var cand1To4 = InvokePrepare(cp1,
            new GuestSpec(2, allChars[0]),
            new GuestSpec(3, allChars[1]),
            new GuestSpec(4, allChars[2]));
        var restored1To4 = RunState.FromSerializable(cand1To4);
        Require(restored1To4.Players.Select(p => p.NetId).SequenceEqual(new ulong[] { 1, 2, 3, 4 }), "1P->4P seat order");
        Require(restored1To4.Players[1].Character.Id == allChars[0].Id, "1P->4P guest 2 char");
        Require(restored1To4.Players[2].Character.Id == allChars[1].Id, "1P->4P guest 3 char");
        Require(restored1To4.Players[3].Character.Id == allChars[2].Id, "1P->4P guest 4 char");

        // 2P -> +2 guests (4P total)
        var cp2 = CreateMapCheckpoint([1, 2], ascension: 0);
        var cand2To4 = InvokePrepare(cp2, new GuestSpec(3, allChars[1]), new GuestSpec(4, allChars[2]));
        Require(cand2To4.Players.Select(p => p.NetId).SequenceEqual(new ulong[] { 1, 2, 3, 4 }), "2P->4P seat order");

        // Limit-agnostic multi-guest expansion: 4P -> 6P and 2P -> 8P work without any hardcoded player cap
        var cp4 = CreateMapCheckpoint([1, 2, 3, 4], ascension: 0);
        var cand4To6 = InvokePrepare(cp4, new GuestSpec(5, allChars[1]), new GuestSpec(6, allChars[2]));
        Require(cand4To6.Players.Select(p => p.NetId).SequenceEqual(new ulong[] { 1, 2, 3, 4, 5, 6 }), "Limit-agnostic 4P->6P seat order");
        var cand2To8 = InvokePrepare(cp2,
            Enumerable.Range(3, 6).Select(i => new GuestSpec((ulong)i, allChars[i % allChars.Length])).ToArray());
        Require(cand2To8.Players.Select(p => p.NetId).SequenceEqual(new ulong[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "Limit-agnostic 2P->8P seat order");

        // Every character in ModelDb.AllCharacters
        var distinctRelics = new HashSet<string>();
        foreach (var ch in allChars)
        {
            var c1 = InvokePrepare(cp2, new GuestSpec(3, ch));
            var c2 = InvokePrepare(cp2, new GuestSpec(3, ch));
            Require(JsonSerializationUtility.ToJson(c1) == JsonSerializationUtility.ToJson(c2),
                $"Retry diverged for character {ch.Id.Entry}");

            var restored = RunState.FromSerializable(c1);
            var guest = restored.Players[2];
            Require(guest.Character.Id == ch.Id, $"Guest character mismatch: expected {ch.Id}, got {guest.Character.Id}");
            Require(guest.Deck.Cards.Count > 0, $"Guest {ch.Id.Entry} deck empty");
            Require(guest.Relics.Count > 0, $"Guest {ch.Id.Entry} relics empty");
            Require(guest.RelicGrabBag.IsPopulated, $"Guest {ch.Id.Entry} relic grab bag not populated");
            distinctRelics.Add(guest.Relics[0].Id.Entry);
        }
        Require(distinctRelics.Count >= 2, "Expected distinct starter relics across characters");
        Log.Info($"[MidRunJoinSmoke] PASS: MultiGuest (1P->4P, 2P->4P) & AllCharacters ({allChars.Length} chars, {checkCount} checks)");
    }

    private static void CheckCatchUpFlowsAndRegistry(SerializableRun checkpoint)
    {
        // Build a rich multi-room history with SPECIFIC Ancients (Neow + Darv + Orobas) AND uneven existing HP:
        // Peer 1: 40/80 (50%), Peer 2: 60/75 (80%) -> avg 65% -> new Silent (MaxHp=70) should spawn at 46/70!
        var rich = CloneRun(checkpoint);
        rich.Players[0].MaxHp = 80;
        rich.Players[0].CurrentHp = 40;
        rich.Players[1].MaxHp = 75;
        rich.Players[1].CurrentHp = 60;

        var tmpState = RunState.FromSerializable(CloneRun(checkpoint));
        var actHistory = new List<MapPointHistoryEntry>();
        MapPointHistoryEntry MakeEntry(MapPointType mpt, RoomType rt, ModelId? modelId = null)
        {
            var e = new MapPointHistoryEntry(mpt, tmpState);
            e.Rooms.Add(new MapPointRoomHistoryEntry { RoomType = rt, ModelId = modelId });
            return e;
        }
        var neow = ModelDb.Event<Neow>();
        var darv = ModelDb.Event<Darv>();
        var orobas = ModelDb.Event<Orobas>();
        actHistory.Add(MakeEntry(MapPointType.Ancient, RoomType.Event, neow.Id));
        actHistory.Add(MakeEntry(MapPointType.Ancient, RoomType.Event, darv.Id));
        actHistory.Add(MakeEntry(MapPointType.Ancient, RoomType.Event, orobas.Id));
        actHistory.Add(MakeEntry(MapPointType.Monster, RoomType.Monster));
        actHistory.Add(MakeEntry(MapPointType.Monster, RoomType.Monster));
        actHistory.Add(MakeEntry(MapPointType.Elite, RoomType.Elite));
        actHistory.Add(MakeEntry(MapPointType.Boss, RoomType.Boss));
        actHistory.Add(MakeEntry(MapPointType.Unknown, RoomType.Event));
        actHistory.Add(MakeEntry(MapPointType.Unknown, RoomType.Event));
        actHistory.Add(MakeEntry(MapPointType.Shop, RoomType.Shop));
        actHistory.Add(MakeEntry(MapPointType.Treasure, RoomType.Treasure));
        actHistory.Add(MakeEntry(MapPointType.RestSite, RoomType.RestSite));
        actHistory.Add(MakeEntry(MapPointType.RestSite, RoomType.RestSite));
        actHistory.Add(MakeEntry(MapPointType.RestSite, RoomType.RestSite));
        rich.MapPointHistory = [actHistory];

        var cand = InvokePrepare(rich, [new GuestSpec(3, ModelDb.Character<Silent>())], null, HistoryBellCatchUpFlow.FlowId);
        var state1 = RunState.FromSerializable(CloneRun(cand));
        var state2 = RunState.FromSerializable(CloneRun(cand));
        var guest1 = state1.GetPlayer(3)!;
        var guest2 = state2.GetPlayer(3)!;

        // Verify HP percentage sync: avg(40/80, 60/75) = 65% of Silent MaxHp
        int expectedSyncedHp = CatchUpHealthFormula.ComputeSyncedCurrentHp([(40, 80), (60, 75)], guest1.Creature.MaxHp);
        Require(guest1.Creature.CurrentHp == expectedSyncedHp,
            $"Guest HP sync mismatch: expected {expectedSyncedHp}/{guest1.Creature.MaxHp}, got {guest1.Creature.CurrentHp}/{guest1.Creature.MaxHp}");

        // Verify marker travels inside starter relic FloorAddedToDeck and is consumed idempotently
        Require(CatchUpCoordinator.TryConsumeMarker(guest1, out string flowId) && flowId == HistoryBellCatchUpFlow.FlowId,
            $"Expected catch-up marker '{HistoryBellCatchUpFlow.FlowId}'");
        Require(!CatchUpCoordinator.TryConsumeMarker(guest1, out _), "Marker must only be consumed once");

        // Verify HistoryBellCatchUpFlow builds exact expected native Rewards
        var defaultFlow = CatchUpRegistry.Default;
        Require(defaultFlow.Id == HistoryBellCatchUpFlow.FlowId, $"Unexpected default flow: {defaultFlow.Id}");
        var rewards1 = defaultFlow.BuildRewards(state1, guest1);
        var rewards2 = defaultFlow.BuildRewards(state2, guest2);

        Require(rewards1.Count == rewards2.Count, "Bell rewards count diverged across peers");
        var goldRewards = rewards1.OfType<GoldReward>().ToList();
        Require(goldRewards.Count == 1, $"All gold must stack into 1 GoldReward, got {goldRewards.Count}");
        Require(rewards1.OfType<RelicReward>().Count() == 2, $"Expected 2 random RelicRewards (1 Elite + 1 Treasure), got {rewards1.OfType<RelicReward>().Count()}");

        var ancientChoices = rewards1.OfType<AncientRelicChoiceReward>().ToList();
        Require(ancientChoices.Count == 3, $"Expected 3 AncientRelicChoiceRewards (Neow, Darv, Orobas), got {ancientChoices.Count}");
        Require(ancientChoices[0].Ancient?.Id == neow.Id && ancientChoices[0].Candidates.Count == 3, "1st ancient choice must be 3 relics from Neow");
        Require(ancientChoices[1].Ancient?.Id == darv.Id && ancientChoices[1].Candidates.Count == 3, "2nd ancient choice must be 3 relics from Darv");
        Require(ancientChoices[2].Ancient?.Id == orobas.Id && ancientChoices[2].Candidates.Count == 3, "3rd ancient choice must be 3 relics from Orobas");

        // Verify every candidate belongs strictly to its corresponding Ancient's own AllPossibleOptions pool!
        var neowPoolIds = new HashSet<ModelId>(HistoryBellCatchUpFlow.GetRelicPoolForAncient(neow, state1, guest1, new()).Select(r => r.Id));
        var darvPoolIds = new HashSet<ModelId>(HistoryBellCatchUpFlow.GetRelicPoolForAncient(darv, state1, guest1, new()).Select(r => r.Id));
        var orobasPoolIds = new HashSet<ModelId>(HistoryBellCatchUpFlow.GetRelicPoolForAncient(orobas, state1, guest1, new()).Select(r => r.Id));
        Require(ancientChoices[0].Candidates.All(c => neowPoolIds.Contains(c.Id)), "Neow choice contained non-Neow relic");
        Require(ancientChoices[1].Candidates.All(c => darvPoolIds.Contains(c.Id)), "Darv choice contained non-Darv relic");
        Require(ancientChoices[2].Candidates.All(c => orobasPoolIds.Contains(c.Id)), "Orobas choice contained non-Orobas relic");

        // Verify all vanilla Ancients produce valid non-empty pools without throwing
        foreach (var anc in ModelDb.AllAncients)
        {
            var pool = HistoryBellCatchUpFlow.GetRelicPoolForAncient(anc, state1, guest1, new());
            Require(pool.Count > 0, $"Ancient {anc.Id.Entry} produced empty relic pool");
        }

        Require(rewards1.OfType<CardUpgradeReward>().Count() == 1, $"Expected 1 CardUpgradeReward (floor(3/2)), got {rewards1.OfType<CardUpgradeReward>().Count()}");
        Require(rewards1.OfType<CardRemovalReward>().Count() == 1, $"Expected 1 CardRemovalReward (1 Boss), got {rewards1.OfType<CardRemovalReward>().Count()}");
        Require(rewards1.OfType<CardReward>().Count() == 4, $"Expected 4 CardRewards (2 Monster + 1 Elite + 1 Boss), got {rewards1.OfType<CardReward>().Count()}");

        // Verify native Hook.ModifyRewards integration: with Vintage modifier active, Monster combats
        // convert their CardRewards into RelicRewards automatically via STS2's own modifier hook!
        var vintageRun = CloneRun(rich);
        vintageRun.Modifiers.Add(ModelDb.Modifier<MegaCrit.Sts2.Core.Models.Modifiers.Vintage>().ToMutable().ToSerializable());
        vintageRun.Modifiers.Add(ModelDb.Modifier<MegaCrit.Sts2.Core.Models.Modifiers.AllStar>().ToMutable().ToSerializable());
        var vintageCand = InvokePrepare(vintageRun, [new GuestSpec(3, ModelDb.Character<Silent>())], null, HistoryBellCatchUpFlow.FlowId);
        var vintageState = RunState.FromSerializable(vintageCand);
        var vintageGuest = vintageState.GetPlayer(3)!;
        var vintageRewards = defaultFlow.BuildRewards(vintageState, vintageGuest);
        Require(vintageRewards.OfType<RelicReward>().Count() == 4,
            $"Vintage should convert 2 Monster CardRewards into RelicRewards (expected 4 RelicRewards total, got {vintageRewards.OfType<RelicReward>().Count()})");
        Require(vintageRewards.OfType<CardReward>().Count() == 2,
            $"Vintage should leave only Elite+Boss CardRewards (expected 2, got {vintageRewards.OfType<CardReward>().Count()})");

        // Verify per-player run-start modifier detection: AllStar is detected (GenerateNeowOption != null), Vintage is not
        var perPlayerMods = CatchUpCoordinator.CollectPerPlayerRunStartModifiers(vintageState, vintageGuest);
        Require(perPlayerMods.Count == 1 && perPlayerMods[0].Modifier is MegaCrit.Sts2.Core.Models.Modifiers.AllStar,
            $"Expected only AllStar as per-player startup modifier, got {perPlayerMods.Count}");

        // Verify split-path mod safety: divergent entries visited ONLY by non-host player 2 (host 1 absent)
        // must be ignored so catch-up strictly follows the host's route!
        var splitRun = CloneRun(rich);
        MapPointHistoryEntry MakeNonHostOnlyEntry(MapPointType mpt, RoomType rt, ModelId? modelId = null)
        {
            var e = new MapPointHistoryEntry { MapPointType = mpt };
            e.Rooms.Add(new MapPointRoomHistoryEntry { RoomType = rt, ModelId = modelId });
            e.PlayerStats.Add(new PlayerMapPointHistoryEntry { PlayerId = 2 });
            return e;
        }
        splitRun.MapPointHistory[0].Add(MakeNonHostOnlyEntry(MapPointType.Monster, RoomType.Monster));
        splitRun.MapPointHistory[0].Add(MakeNonHostOnlyEntry(MapPointType.Elite, RoomType.Elite));
        splitRun.MapPointHistory[0].Add(MakeNonHostOnlyEntry(MapPointType.Boss, RoomType.Boss));
        splitRun.MapPointHistory[0].Add(MakeNonHostOnlyEntry(MapPointType.Treasure, RoomType.Treasure));
        splitRun.MapPointHistory[0].Add(MakeNonHostOnlyEntry(MapPointType.RestSite, RoomType.RestSite));
        splitRun.MapPointHistory[0].Add(MakeNonHostOnlyEntry(MapPointType.Ancient, RoomType.Event, neow.Id));

        var splitCand = InvokePrepare(splitRun, [new GuestSpec(3, ModelDb.Character<Silent>())], null, HistoryBellCatchUpFlow.FlowId);
        var splitState = RunState.FromSerializable(splitCand);
        var hostCounts = CatchUpHistoryScanner.Scan(state1);
        var splitCounts = CatchUpHistoryScanner.Scan(splitState);
        Require(splitCounts == hostCounts,
            $"Split-path scan diverged from host route: expected {hostCounts}, got {splitCounts}");
        Require(CatchUpHistoryScanner.GetVisitedCombatRooms(splitState).Count == CatchUpHistoryScanner.GetVisitedCombatRooms(state1).Count,
            "Split-path combat room list included non-host rooms");
        Require(CatchUpHistoryScanner.GetVisitedAncientIds(splitState).Count == CatchUpHistoryScanner.GetVisitedAncientIds(state1).Count,
            "Split-path ancient list included non-host ancients");

        // None flow
        var noneFlow = CatchUpRegistry.GetById(NoneCatchUpFlow.FlowId);
        Require(noneFlow.BuildRewards(state1, guest1).Count == 0, "NoneCatchUpFlow must produce 0 rewards");

        // Custom ICatchUpFlow pluggability
        var custom = new TestCustomFlow();
        CatchUpRegistry.Register(custom);
        Require(ReferenceEquals(CatchUpRegistry.GetById("test_custom"), custom), "Custom ICatchUpFlow registration failed");
        Require(CatchUpRegistry.GetById("unknown_id_xyz").Id == CatchUpRegistry.Default.Id, "Unknown flow id must fall back to Default");

        Log.Info($"[MidRunJoinSmoke] PASS: HistoryBellCatchUpFlow & Registry ({checkCount} checks)");
    }

    private static void CheckAscensionScaling()
    {
        var cpA0 = CreateMapCheckpoint([1, 2], ascension: 0);
        var cpA10 = CreateMapCheckpoint([1, 2], ascension: 10);
        var ironclad = ModelDb.Character<Ironclad>();

        var guestA0 = RunState.FromSerializable(InvokePrepare(cpA0, new GuestSpec(3, ironclad))).Players[2];
        var guestA10 = RunState.FromSerializable(InvokePrepare(cpA10, new GuestSpec(3, ironclad))).Players[2];

        bool changed = guestA10.Creature.MaxHp != guestA0.Creature.MaxHp
                       || guestA10.Creature.CurrentHp != guestA0.Creature.CurrentHp
                       || guestA10.Deck.Cards.Count != guestA0.Deck.Cards.Count
                       || guestA10.Gold != guestA0.Gold;
        Require(changed, "Ascension 10 did not affect guest");
        Log.Info($"[MidRunJoinSmoke] PASS: AscensionScaling (A0 deck={guestA0.Deck.Cards.Count} vs A10 deck={guestA10.Deck.Cards.Count})");
    }

    private static void CheckZeroImpactAndDeterminism(SerializableRun checkpoint)
    {
        var ironclad = ModelDb.Character<Ironclad>();
        var silent = ModelDb.Character<Silent>();
        string beforeJson = JsonSerializationUtility.ToJson(checkpoint);
        var candidate = InvokePrepare(checkpoint, new GuestSpec(3, ironclad), new GuestSpec(4, silent));
        string afterJson = JsonSerializationUtility.ToJson(checkpoint);
        Require(beforeJson == afterJson, "Checkpoint mutated in-place by Candidate.Prepare");
        Require(JsonSerializationUtility.ToJson(candidate) ==
                JsonSerializationUtility.ToJson(InvokePrepare(checkpoint, new GuestSpec(3, ironclad), new GuestSpec(4, silent))),
            "Multi-guest retry diverged");

        var origState = RunState.FromSerializable(CloneRun(checkpoint));
        var candState = RunState.FromSerializable(CloneRun(candidate));
        for (int i = 0; i < 32; i++)
        {
            Require(origState.Rng.UnknownMapPoint.NextInt(10000) == candState.Rng.UnknownMapPoint.NextInt(10000),
                $"UnknownMapPoint RNG diverged at step {i}");
            Require(origState.Rng.UpFront.NextInt(10000) == candState.Rng.UpFront.NextInt(10000),
                $"UpFront RNG diverged at step {i}");
            Require(origState.Players[0].PlayerRng.Rewards.NextInt(10000) == candState.Players[0].PlayerRng.Rewards.NextInt(10000),
                $"Player 1 Rewards RNG diverged at step {i}");
            Require(origState.Players[1].PlayerRng.Rewards.NextInt(10000) == candState.Players[1].PlayerRng.Rewards.NextInt(10000),
                $"Player 2 Rewards RNG diverged at step {i}");
        }

        string candJson = JsonSerializationUtility.ToJson(candidate);
        using (var origDoc = System.Text.Json.JsonDocument.Parse(beforeJson))
        using (var candDoc = System.Text.Json.JsonDocument.Parse(candJson))
        {
            Require(origDoc.RootElement.GetProperty("shared_relic_grab_bag").GetRawText() ==
                    candDoc.RootElement.GetProperty("shared_relic_grab_bag").GetRawText(),
                "SharedRelicGrabBag mutated");
            Require(origDoc.RootElement.GetProperty("odds").GetRawText() ==
                    candDoc.RootElement.GetProperty("odds").GetRawText(),
                "SerializableOdds mutated");
            Require(origDoc.RootElement.GetProperty("rng").GetRawText() ==
                    candDoc.RootElement.GetProperty("rng").GetRawText(),
                "SerializableRng mutated");
        }

        // Network packet roundtrip for 4-player expanded candidate
        var writer = new PacketWriter();
        candidate.Serialize(writer);
        var reader = new PacketReader();
        reader.Reset(writer.Buffer);
        var packet = new SerializableRun();
        packet.Deserialize(reader);
        var restored = RunState.FromSerializable(packet);
        Require(restored.Players.Select(p => p.NetId).SequenceEqual(new ulong[] { 1, 2, 3, 4 }), "4P seat order changed");
        Require(restored.CurrentMapPointHistoryEntry?.GetEntry(3).PlayerId == 3, "Guest 3 boundary history missing");
        Require(restored.CurrentMapPointHistoryEntry?.GetEntry(4).PlayerId == 4, "Guest 4 boundary history missing");
        Require(restored.MapPointHistory[0][0].PlayerStats.Count == 2,
            $"Earlier floor history should keep 2 players, got {restored.MapPointHistory[0][0].PlayerStats.Count}");

        // Post-run settlement safety: GetEntry(guest) on earlier floors visited before guest joined must NOT throw
        // and must NOT mutate PlayerStats (fixes late-joining players getting stuck on NGameOverScreen summary!).
        var earlyFloor = restored.MapPointHistory[0][0];
        var guestEarlyStats = earlyFloor.GetEntry(3);
        Require(guestEarlyStats.PlayerId == 3 && guestEarlyStats.GoldGained == 0,
            "SafeGetEntry should return detached empty stats for late-joining guest on earlier floor");
        Require(earlyFloor.PlayerStats.Count == 2,
            $"SafeGetEntry must not mutate earlier floor PlayerStats (got {earlyFloor.PlayerStats.Count})");
        int guestTotalGold = restored.MapPointHistory.SelectMany(act => act).Sum(e => e.GetEntry(3).GoldGained);
        Require(guestTotalGold == 0, $"Unexpected guest total gold across history: {guestTotalGold}");

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) InvokePrepare(checkpoint, new GuestSpec(3, ironclad), new GuestSpec(4, silent));
        sw.Stop();
        Log.Info($"[MidRunJoinSmoke] PASS: ZeroImpact + 4P Determinism + PacketRoundtrip (Prepare2 avg={sw.Elapsed.TotalMilliseconds / 5.0:F1} ms, {checkCount} checks)");
    }

    private static void CheckMultiGuestDialogAndSelection(NMultiplayerSubmenu menu, SerializableRun checkpoint)
    {
        // 0. Verify floating overlay button attached to NMultiplayerSubmenu._loadButton
        var loadButton = (Control)AccessTools.Field(typeof(NMultiplayerSubmenu), "_loadButton").GetValue(menu)!;
        var floatingBtn = loadButton.GetNodeOrNull<Button>("MidRunJoinOverlayButton");
        Require(floatingBtn != null, "Floating MidRunJoinOverlayButton missing from _loadButton");
        Require(floatingBtn!.MouseFilter == Control.MouseFilterEnum.Stop, "Floating button must stop mouse propagation");

        // 1. Open and Cancel + verify bounded vanilla modal size & Confirm button visibility
        var dialog = ShiftLoad(menu, checkpoint);
        var panel = (PanelContainer)dialog.FindChild("RosterPanel", recursive: true, owned: false);
        var confirmBtn = (Button)dialog.FindChild("ConfirmButton", recursive: true, owned: false);
        var cancelBtn = (Button)dialog.FindChild("CancelButton", recursive: true, owned: false);
        Require(panel.CustomMinimumSize.Y is >= 320 and <= 780, $"Dialog height unexpectedly inflated: {panel.CustomMinimumSize}");
        Require(confirmBtn.Visible, "Dialog Confirm button must be visible");

        // Verify existing member removal toggle exists for non-host player 2 (and not for host player 1)
        var existingScroll = (ScrollContainer)dialog.FindChild("ExistingMembersScroll", recursive: true, owned: false);
        var removeP2Btn = existingScroll.FindChild("RemoveExisting_2", recursive: true, owned: false) as Button;
        Require(removeP2Btn != null, "RemoveExisting_2 button missing");
        Require(existingScroll.FindChild("RemoveExisting_1", recursive: true, owned: false) == null, "Host must not have remove button");
        removeP2Btn!.EmitSignal(BaseButton.SignalName.Pressed);
        // Undo removal cleanly
        removeP2Btn = existingScroll.FindChild("RemoveExisting_2", recursive: true, owned: false) as Button;
        Require(removeP2Btn != null && (removeP2Btn.Text.Contains("撤销") || removeP2Btn.Text.Contains("Undo")), "Remove toggle did not switch to Undo");
        removeP2Btn!.EmitSignal(BaseButton.SignalName.Pressed);
        cancelBtn.EmitSignal(BaseButton.SignalName.Pressed);
        Require(!dialog.Visible, "Cancel button did not close modal");

        // 2. Open on valid checkpoint -> test invalid inputs & multi-guest add (guest 3 + guest 4)
        dialog = ShiftLoad(menu, checkpoint);
        confirmBtn = (Button)dialog.FindChild("ConfirmButton", recursive: true, owned: false);
        var msg = (Label)dialog.FindChild("StatusLabel", recursive: true, owned: false);
        var inputRow = (HBoxContainer)dialog.FindChild("GuestInputRow", recursive: true, owned: false);
        var input = inputRow.GetChildren().OfType<LineEdit>().Single();
        var charPicker = inputRow.GetChildren().OfType<OptionButton>().Single();
        var addButton = (Button)inputRow.FindChild("AddGuestButton", recursive: true, owned: false);

        var allChars = ModelDb.AllCharacters.ToArray();
        Require(charPicker.ItemCount == allChars.Length + 1, "Character picker should contain all characters plus Random");
        string randomItemText = charPicker.GetItemText(allChars.Length);
        Require(randomItemText == "随机" || randomItemText == "Random", $"Unexpected random character option text: {randomItemText}");

        foreach (string badText in new[] { "", "   ", "0", "-1", "abc" })
        {
            input.Text = badText;
            confirmBtn.EmitSignal(BaseButton.SignalName.Pressed);
            Require(dialog.Visible, $"Invalid input '{badText}' unexpectedly closed dialog");
        }

        // Duplicate existing player ID
        input.Text = "2";
        confirmBtn.EmitSignal(BaseButton.SignalName.Pressed);
        Require(dialog.Visible, "Existing player accepted");

        // Add Guest 3 (index 1) via Add button
        charPicker.Selected = 1;
        expectedGuest3Char = allChars[1].Id.Entry;
        input.Text = "3";
        addButton.EmitSignal(BaseButton.SignalName.Pressed);
        Require(input.Text == "", "Input should clear after adding guest 3");

        // Duplicate guest 3 rejected
        input.Text = "3";
        addButton.EmitSignal(BaseButton.SignalName.Pressed);
        Require(msg.Text.Contains("已在队伍中或已添加到列表") || msg.Text.Contains("already in the party"),
            $"Duplicate guest 3 msg unexpected: {msg.Text}");

        // Enter Guest 4 (index 2) and submit directly via ConfirmButton (tests auto-include + multi-guest!)
        charPicker.Selected = 2;
        expectedGuest4Char = allChars[2].Id.Entry;
        input.Text = "  4  ";
        confirmBtn.EmitSignal(BaseButton.SignalName.Pressed);
        Require(!dialog.Visible, "Valid 2-guest submission rejected");

        // 3. Multi-removal GUI check on a 4P save: verify all 4 rows are visible and 2+ members can be removed at once
        var cp4 = InvokePrepare(checkpoint, [new GuestSpec(3, allChars[1]), new GuestSpec(4, allChars[2])], null, NoneCatchUpFlow.FlowId);
        var rmDialog = ShiftLoad(menu, cp4);
        var rmScroll = (ScrollContainer)rmDialog.FindChild("ExistingMembersScroll", recursive: true, owned: false);
        Require(rmScroll.CustomMinimumSize.Y >= 140, $"4P existing members list too short ({rmScroll.CustomMinimumSize.Y}px), would clip 3rd/4th players");
        var rmBtn2 = rmScroll.FindChild("RemoveExisting_2", recursive: true, owned: false) as Button;
        var rmBtn3 = rmScroll.FindChild("RemoveExisting_3", recursive: true, owned: false) as Button;
        var rmBtn4 = rmScroll.FindChild("RemoveExisting_4", recursive: true, owned: false) as Button;
        Require(rmBtn2 != null && rmBtn3 != null && rmBtn4 != null, "4P dialog missing RemoveExisting buttons for seats 2/3/4");
        rmBtn2!.EmitSignal(BaseButton.SignalName.Pressed);
        // Re-fetch after RebuildExistingUi and remove a 2nd player (seat 3)
        rmBtn3 = rmScroll.FindChild("RemoveExisting_3", recursive: true, owned: false) as Button;
        Require(rmBtn3 != null, "RemoveExisting_3 disappeared after removing seat 2");
        rmBtn3!.EmitSignal(BaseButton.SignalName.Pressed);
        var rmMsg = (Label)rmDialog.FindChild("StatusLabel", recursive: true, owned: false);
        Require(rmMsg.Text.Contains("移除 2") || rmMsg.Text.Contains("Removing 2"), $"Multi-remove summary mismatch: {rmMsg.Text}");
        var rmCancelBtn = (Button)rmDialog.FindChild("CancelButton", recursive: true, owned: false);
        rmCancelBtn.EmitSignal(BaseButton.SignalName.Pressed);

        Log.Info($"[MidRunJoinSmoke] PASS: MultiGuestDialog + MultiRemoveGUI (3={expectedGuest3Char}, 4={expectedGuest4Char}, {checkCount} checks)");
    }

    private static Control ShiftLoad(NMultiplayerSubmenu menu, SerializableRun checkpoint)
    {
        var openDialog = AccessTools.Method(typeof(MidRunJoin.Entry).Assembly.GetType("MidRunJoin.JoinLobby")!, "OpenGuestDialog")!;
        openDialog.Invoke(null, [menu, checkpoint]);
        return menu.GetNode<Control>("MidRunJoinRosterModal");
    }

    private static SerializableRun CreateMapCheckpoint(ulong[] playerIds, int ascension, int[]? hostGoldPerFloor = null)
    {
        Player[] players = playerIds.Select(id => Player.CreateForNewRun<Ironclad>(UnlockState.none, id)).ToArray();
        var acts = ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList();
        var state = RunState.CreateForNewRun(players, acts, [], GameMode.Standard, false, ascension, "MIDJOIN");
        foreach (var act in state.Acts)
            act.GenerateRooms(state);
        foreach (var player in players)
            player.PopulateRelicGrabBagIfNecessary(state.Rng.UpFront);
        var map = SerializableActMap.FromActMap(state.Act.CreateMap(state, false));
        var savedActs = state.Acts.Select(a => a.ToSave()).ToList();
        savedActs[0].SavedMap = map;

        var floor1 = new MapPointHistoryEntry(MapPointType.Ancient, state);
        floor1.Rooms.Add(new MapPointRoomHistoryEntry { RoomType = RoomType.Event, ModelId = ModelDb.Event<Neow>().Id });
        if (hostGoldPerFloor is { Length: > 0 })
            floor1.GetEntry(playerIds[0]).GoldGained = hostGoldPerFloor[0];

        var floor2 = new MapPointHistoryEntry(MapPointType.Monster, state);
        floor2.Rooms.Add(new MapPointRoomHistoryEntry { RoomType = RoomType.Monster });
        if (hostGoldPerFloor is { Length: > 1 })
            floor2.GetEntry(playerIds[0]).GoldGained = hostGoldPerFloor[1];

        return new SerializableRun
        {
            SchemaVersion = SaveManager.Instance.GetLatestSchemaVersion<SerializableRun>(),
            Players = players.Select(p => p.ToSerializable()).ToList(),
            Acts = savedActs,
            GameMode = GameMode.Standard,
            Ascension = ascension,
            PreFinishedRoom = new SerializableRoom { RoomType = RoomType.Monster, IsPreFinished = true },
            VisitedMapCoords = [map.StartingPoint.Coord, map.Points.First(p => p.PointType == MapPointType.Monster).Coord],
            MapPointHistory = [[floor1, floor2]],
            SerializableRng = state.Rng.ToSerializable(),
            SerializableOdds = state.Odds.ToSerializable(),
            SerializableSharedRelicGrabBag = state.SharedRelicGrabBag.ToSerializable()
        };
    }

    [HarmonyPostfix, HarmonyPatch(typeof(NMultiplayerLoadGameScreen), "_Process")]
    private static void CheckLobby(NMultiplayerLoadGameScreen __instance, LoadRunLobby? ____runLobby)
    {
        if (lobbyChecked || ____runLobby is not { } lobby)
            return;
        lobbyChecked = true;
        var original = lobby.Players.ToArray();
        try
        {
            var restored = RunState.FromSerializable(lobby.Run);
            Require(restored.Players.Count == 4, $"Lobby run expected 4 players, got {restored.Players.Count}");
            Require(restored.Players[2].Character.Id.Entry == expectedGuest3Char,
                $"Lobby guest 3 char expected {expectedGuest3Char}, got {restored.Players[2].Character.Id.Entry}");
            Require(restored.Players[3].Character.Id.Entry == expectedGuest4Char,
                $"Lobby guest 4 char expected {expectedGuest4Char}, got {restored.Players[3].Character.Id.Entry}");

            void SetLobby(params (ulong id, bool ready)[] entries)
            {
                lobby.Players.Clear();
                foreach (var (id, ready) in entries)
                    lobby.Players.Add(new LoadRunLobbyPlayer { id = id, isReady = ready });
            }

            SetLobby((1, true), (2, true), (3, true));
            Require(!lobby.IsAboutToBeginGame(), "Missing guest 4 accepted");

            SetLobby((1, true), (2, true), (3, true), (5, true));
            Require(!lobby.IsAboutToBeginGame(), "Wrong guest 4 ID accepted");

            SetLobby((1, true), (2, true), (3, true), (4, false));
            Require(!lobby.IsAboutToBeginGame(), "Unready guest 4 accepted");

            SetLobby((1, true), (2, true), (3, false), (4, true));
            Require(!lobby.IsAboutToBeginGame(), "Unready guest 3 accepted");

            SetLobby((1, true), (2, true), (3, true), (4, true));
            Require(lobby.IsAboutToBeginGame(), "Complete 4P ready roster rejected");

            // Verify non-expanded (vanilla) run passthrough
            var expandedField = AccessTools.Field(AccessTools.TypeByName("MidRunJoin.JoinLobby"), "Expanded");
            var table = expandedField.GetValue(null)!;
            var removeMethod = table.GetType().GetMethod("Remove", [typeof(SerializableRun)])!;
            Require((bool)removeMethod.Invoke(table, [lobby.Run])!, "Expanded table did not contain lobby.Run");
            SetLobby((1, true), (2, true), (3, true), (4, true));
            Require(lobby.IsAboutToBeginGame(), "Non-expanded lobby passthrough broken");

            Log.Info($"[MidRunJoinSmoke] PASS: 4P LobbyMatrix + Passthrough ({checkCount} total checks)");
            Log.Info("[MidRunJoinSmoke] PASS");
            __instance.GetTree().Quit(0);
        }
        catch (Exception e)
        {
            Log.Error($"[MidRunJoinSmoke] FAIL: {e}");
            __instance.GetTree().Quit(1);
        }
        finally
        {
            lobby.Players.Clear();
            lobby.Players.AddRange(original);
        }
    }

    private static void Require(bool condition, string error)
    {
        checkCount++;
        if (!condition)
            throw new InvalidOperationException(error);
    }

    private sealed class TestCustomFlow : ICatchUpFlow
    {
        public string Id => "test_custom";
        public string DisplayName => "Custom Test Flow";
        public string Description => "Test";
        public List<Reward> BuildRewards(RunState state, Player guest) => [];
    }
}

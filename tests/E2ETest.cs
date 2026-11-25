using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.ValueProps;
using MidRunJoin.CatchUp;

namespace MidRunJoinSmoke;

[HarmonyPatch]
internal static class E2ETest
{
    private static string _phase = "";
    private static string _guest3Char = "SILENT";
    private static string _guest4Char = "DEFECT";
    private static double _cooldown;
    private static bool _done;
    private static bool _lobbyReadySent;
    private static bool _hostDialogHandled;
    private static bool _phase1KillSent;
    private static bool _phase1RewardsProceedSent;
    private static readonly HashSet<int> _mapVoteSentForVisitedCount = new();

    // Phase 2 state (2P -> 4P multi-guest + CallingBell catch-up rewards + 4P combat + rewards + 3rd room)
    private static bool _guestBellRewardsHandled;
    private static bool _scalingChecked;
    private static int _turn1CardsPlayed;
    private static bool _turn1Ended;
    private static bool _turn2KillSent;
    private static bool _phase2CombatEntered;
    private static bool _phase2RewardsProceedSent;
    private static int _phase2InitialVisitedCount = -1;
    private static Random? _rng;

    // Cancel2P state
    private static bool _cancel2pRewardsProceedSent;
    private static bool _cancel2pVerified;

    // Phase 3 state (normal 4P save/reload roundtrip without Shift)
    private static bool _phase3Verified;

    // Late-game progression state (Act 1 full clear -> Act 2)
    private static int _lastKillFloor = -1;
    private static int _lastRewardsFloor = -1;
    private static int _lastRestFloor = -1;
    private static int _lastTreasureFloor = -1;
    private static bool _lateGameMode;

    internal static void Init()
    {
        if (!CommandLineHelper.TryGetValue("midrunjoin-e2e", out var phase) || string.IsNullOrWhiteSpace(phase))
            return;
        _phase = phase.Trim().ToLowerInvariant();
        if (CommandLineHelper.TryGetValue("midrunjoin-guest-char", out var guestChar) && !string.IsNullOrWhiteSpace(guestChar))
            _guest3Char = guestChar.Trim().ToUpperInvariant();
        if (CommandLineHelper.TryGetValue("midrunjoin-guest4-char", out var guest4Char) && !string.IsNullOrWhiteSpace(guest4Char))
            _guest4Char = guest4Char.Trim().ToUpperInvariant();
        new Harmony("xinmo.MidRunJoin.E2E").CreateClassProcessor(typeof(E2ETest)).Patch();
        Log.Info($"[MidRunJoinE2E] Enabled phase={_phase} guest3={_guest3Char} guest4={_guest4Char}");
    }

    // Phase 2, Cancel2P, Phase 3, & LateGame P2 Host: intercept fastmp=load StartHost to open MidRunJoin's dialog (simulating floating button click).
    [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyPatch(typeof(NMultiplayerSubmenu), nameof(NMultiplayerSubmenu.StartHost))]
    private static bool InterceptHostLoadForDialog(NMultiplayerSubmenu __instance, SerializableRun run, ref bool __state)
    {
        if ((_phase != "phase2" && _phase != "cancel2p" && _phase != "phase3" && _phase != "lategame_p2") || _hostDialogHandled)
            return true;
        if (!CommandLineHelper.TryGetValue("fastmp", out var mode) || mode != "load")
            return true;
        _hostDialogHandled = true;
        __state = true;
        var openDialog = AccessTools.Method(typeof(MidRunJoin.Entry).Assembly.GetType("MidRunJoin.JoinLobby")!, "OpenGuestDialog")!;
        openDialog.Invoke(null, [__instance, run]);
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(NMultiplayerSubmenu), nameof(NMultiplayerSubmenu.StartHost))]
    private static void SubmitHostDialog(NMultiplayerSubmenu __instance, SerializableRun run, bool __state)
    {
        if (!__state)
            return;
        try
        {
            var dialog = __instance.GetNode<Control>("MidRunJoinRosterModal");
            var confirmBtn = (Button)dialog.FindChild("ConfirmButton", recursive: true, owned: false);
            var cancelBtn = (Button)dialog.FindChild("CancelButton", recursive: true, owned: false);
            if (_phase == "cancel2p")
            {
                string beforeJson = JsonSerializationUtility.ToJson(run);
                Log.Info("[MidRunJoinE2E] Cancel2P host canceling dialog, then starting normal 2P host");
                cancelBtn.EmitSignal(BaseButton.SignalName.Pressed);
                Require(beforeJson == JsonSerializationUtility.ToJson(run), "Cancel mutated checkpoint run");
                __instance.StartHost(run);
                return;
            }

            if (_phase == "phase3")
            {
                // Phase 3: test removing absent player 4 (4P -> 3P) via 2-step GUI button (Remove -> Delete)!
                var existingScroll = (ScrollContainer)dialog.FindChild("ExistingMembersScroll", recursive: true, owned: false);
                var remove4 = existingScroll.FindChild("RemoveExisting_4", recursive: true, owned: false) as Button;
                Require(remove4 != null, "Phase3 missing RemoveExisting_4 button");
                remove4!.EmitSignal(BaseButton.SignalName.Pressed);
                var confirmDelete4 = existingScroll.FindChild("ConfirmDelete_4", recursive: true, owned: false) as Button;
                Require(confirmDelete4 != null, "Phase3 missing ConfirmDelete_4 button");
                confirmDelete4!.EmitSignal(BaseButton.SignalName.Pressed);
                Log.Info("[MidRunJoinE2E] Phase3 host removing absent peer 4 (4P -> 3P)");
                confirmBtn.EmitSignal(BaseButton.SignalName.Pressed);
                Require(!dialog.Visible, "Phase3 dialog remained visible after Confirm");
                return;
            }

            // Phase 2: Multi-guest submission: add guest 3 (_guest3Char) + guest 4 (_guest4Char) with default CallingBell flow
            var inputRow = (HBoxContainer)dialog.FindChild("GuestInputRow", recursive: true, owned: false);
            var input = inputRow.GetChildren().OfType<LineEdit>().Single();
            var picker = inputRow.GetChildren().OfType<OptionButton>().Single();
            var addButton = (Button)inputRow.FindChild("AddGuestButton", recursive: true, owned: false);

            var characters = ModelDb.AllCharacters.ToArray();
            int idx3 = Math.Max(0, Array.FindIndex(characters, c => c.Id.Entry.Equals(_guest3Char, StringComparison.OrdinalIgnoreCase)));
            int idx4 = Math.Max(0, Array.FindIndex(characters, c => c.Id.Entry.Equals(_guest4Char, StringComparison.OrdinalIgnoreCase)));

            picker.Selected = idx3;
            input.Text = "3";
            addButton.EmitSignal(BaseButton.SignalName.Pressed);

            picker.Selected = idx4;
            input.Text = "4";
            Log.Info($"[MidRunJoinE2E] Host submitting multi-guest 3={characters[idx3].Id.Entry}, 4={characters[idx4].Id.Entry} (flow=default)");
            confirmBtn.EmitSignal(BaseButton.SignalName.Pressed);
            Require(!dialog.Visible, "Host expanded lobby dialog remained visible after Confirm");
        }
        catch (Exception ex)
        {
            Fail(__instance, $"Host dialog submission failed: {ex}");
        }
    }

    // Phase 1 & LateGame P1: Character select lobby ready-up (2 players)
    [HarmonyPostfix, HarmonyPatch(typeof(NCharacterSelectScreen), "_Process")]
    private static void TickCharSelect(NCharacterSelectScreen __instance)
    {
        if (_done || (_phase != "phase1" && _phase != "lategame_p1") || _lobbyReadySent)
            return;
        var lobby = __instance.Lobby;
        if (lobby == null || lobby.Players.Count < 2)
            return;
        _lobbyReadySent = true;
        SaveManager.Instance.MarkFtueAsComplete("accept_tutorials_ftue");
        SaveManager.Instance.MarkFtueAsComplete("combat_reward_ftue");
        SaveManager.Instance.MarkFtueAsComplete("map_select_ftue");
        Log.Info($"[MidRunJoinE2E] {_phase} CharSelect ready-up (players={lobby.Players.Count})");
        lobby.SetReady(true);
    }

    // LoadRunLobby ready-up: 2 players for cancel2p, 4 players for phase2/lategame_p2, 3 players for phase3
    [HarmonyPostfix, HarmonyPatch(typeof(NMultiplayerLoadGameScreen), "_Process")]
    private static void TickLoadLobby(NMultiplayerLoadGameScreen __instance, LoadRunLobby? ____runLobby)
    {
        if (_done || _lobbyReadySent)
            return;
        int expectedPlayers = _phase switch
        {
            "cancel2p" => 2,
            "phase2" => 4,
            "lategame_p2" => 4,
            "phase3" => 3,
            _ => 0
        };
        if (expectedPlayers == 0 || ____runLobby is not { } lobby || lobby.Players.Count < expectedPlayers)
            return;
        _lobbyReadySent = true;
        SaveManager.Instance.MarkFtueAsComplete("accept_tutorials_ftue");
        SaveManager.Instance.MarkFtueAsComplete("combat_reward_ftue");
        SaveManager.Instance.MarkFtueAsComplete("map_select_ftue");
        Log.Info($"[MidRunJoinE2E] {_phase} LoadLobby ready-up (players={lobby.Players.Count}, runPlayers={lobby.Run.Players.Count})");
        lobby.SetReady(true);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(NRun), "_Process")]
    private static void TickGame(NRun __instance, double delta)
    {
        if (_done || string.IsNullOrEmpty(_phase))
            return;
        if (!RunManager.Instance.IsInProgress)
            return;
        _cooldown -= delta;
        if (_cooldown > 0)
            return;

        try
        {
            var state = RunManager.Instance.DebugOnlyGetState();
            if (state == null)
                return;
            var me = LocalContext.GetMe(state);
            if (me == null)
                return;

            switch (_phase)
            {
                case "phase1":
                    TickPhase1(__instance, state, me);
                    break;
                case "cancel2p":
                    TickCancel2P(__instance, state, me);
                    break;
                case "phase2":
                    _lateGameMode = false;
                    TickPhase2(__instance, state, me);
                    break;
                case "lategame_p1":
                    TickLateGameP1(__instance, state, me);
                    break;
                case "lategame_p2":
                    _lateGameMode = true;
                    TickPhase2(__instance, state, me);
                    break;
                case "phase3":
                    TickPhase3(__instance, state, me);
                    break;
            }
        }
        catch (Exception ex)
        {
            Fail(__instance, ex.ToString());
        }
    }

    // Phase 1: 2P run -> beat 1st Monster -> enter 2nd Monster MID-COMBAT (PreFinishedRoom == null) -> verify real mid-combat save!
    private static void TickPhase1(Node node, RunState state, Player me)
    {
        int visited = state.VisitedMapCoords.Count;
        int monsterCount = state.VisitedMapCoords.Count(c => state.Map.GetPoint(c)?.PointType == MapPointType.Monster);

        if (monsterCount == 0)
        {
            if (HandleEventIfActive(state, me))
                return;
            if (NMapScreen.Instance?.IsOpen == true)
                VoteForNextNode(state, me, visited, preferMonster: true, requireMonsterChild: true);
            return;
        }

        if (monsterCount == 1)
        {
            if (CombatManager.Instance.IsInProgress)
            {
                if (IsLocalPlayPhase(me) && me.NetId == 1 && !_phase1KillSent)
                {
                    _phase1KillSent = true;
                    Log.Info("[MidRunJoinE2E] Phase1 host sending 'kill all' for 1st monster");
                    RunConsoleCommand("kill all");
                    _cooldown = 1.0;
                }
                return;
            }

            if (!_phase1RewardsProceedSent && NOverlayStack.Instance?.Peek() is NRewardsScreen)
            {
                _phase1RewardsProceedSent = true;
                SaveManager.Instance.MarkFtueAsComplete("combat_reward_ftue");
                Log.Info($"[MidRunJoinE2E] Phase1 peer={me.NetId} proceeding from 1st monster rewards");
                RunManager.Instance.ProceedFromTerminalRewardsScreen();
                _cooldown = 1.0;
                return;
            }

            if (NMapScreen.Instance?.IsOpen == true || _phase1RewardsProceedSent)
                VoteForNextNode(state, me, visited, preferMonster: true, requireMonsterChild: false);
            return;
        }

        if (monsterCount >= 2 && CombatManager.Instance.IsInProgress && IsLocalPlayPhase(me))
        {
            if (me.NetId == 1)
            {
                var saved = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(1);
                Require(saved.SaveData != null, "Host mid-combat save missing");
                Require(saved.SaveData!.Players.Count == 2, $"Expected 2 players in Phase1 save, got {saved.SaveData.Players.Count}");
                Require(saved.SaveData.PreFinishedRoom == null, "Expected PreFinishedRoom == null for mid-combat save");
                Log.Info($"[MidRunJoinE2E] Phase1 host verified MID-COMBAT save: players=2 visited={saved.SaveData.VisitedMapCoords.Count}");
            }
            Log.Info($"[MidRunJoinE2E] PHASE1_PASS peer={me.NetId}");
            _done = true;
        }
    }

    // Cancel2P: Host canceled Shift-dialog and loaded normally -> proceed from pre-finished screen -> vote next monster -> verify 2P scaling!
    private static void TickCancel2P(Node node, RunState state, Player me)
    {
        if (_cancel2pVerified)
            return;

        if (!CombatManager.Instance.IsInProgress)
        {
            if (!_cancel2pRewardsProceedSent && NOverlayStack.Instance?.Peek() is NRewardsScreen)
            {
                _cancel2pRewardsProceedSent = true;
                SaveManager.Instance.MarkFtueAsComplete("combat_reward_ftue");
                RunManager.Instance.ProceedFromTerminalRewardsScreen();
                _cooldown = 0.8;
                return;
            }
            if ((NMapScreen.Instance?.IsOpen == true || _cancel2pRewardsProceedSent) && _mapVoteSentForVisitedCount.Count == 0)
            {
                VoteForNextNode(state, me, state.VisitedMapCoords.Count, preferMonster: true, requireMonsterChild: false);
            }
            return;
        }

        var cs = CombatManager.Instance.DebugOnlyGetState();
        if (cs == null || !IsLocalPlayPhase(me))
            return;

        _cancel2pVerified = true;
        Require(state.Players.Count == 2, $"Cancel2P expected 2 players, got {state.Players.Count}");
        Require(cs.Players.Count == 2, $"Cancel2P expected 2 combat players, got {cs.Players.Count}");
        foreach (var enemy in cs.Enemies)
        {
            int baseHp = enemy.MonsterMaxHpBeforeModification ?? 0;
            int expected2P = (int)Creature.ScaleHpForMultiplayer(baseHp, cs.Encounter, 2, state.CurrentActIndex);
            Require(enemy.MaxHp == expected2P, $"Cancel2P enemy {enemy.Name} MaxHp={enemy.MaxHp} != expected 2P {expected2P}");
        }
        Log.Info($"[MidRunJoinE2E] CANCEL2P_PASS peer={me.NetId} players=2 enemies={cs.Enemies.Count}");
        _done = true;
        // Let host & client both log PASS before Run-E2E stops processes cleanly (prevents premature disconnect pause)
    }

    // Phase 2: 4P (2P + peer3 SILENT + peer4 DEFECT) -> CallingBell-style catch-up rewards -> 4P Combat -> 3rd room!
    private static void TickPhase2(Node node, RunState state, Player me)
    {
        // Guests (peer 3 & peer 4) automatically receive non-terminal CallingBell-style NRewardsScreen overlay on load!
        if ((me.NetId == 3 || me.NetId == 4) && !_guestBellRewardsHandled && NOverlayStack.Instance?.Peek() is NRewardsScreen bellRewards)
        {
            bool isTerminal = (bool)AccessTools.Field(typeof(NRewardsScreen), "_isTerminal").GetValue(bellRewards)!;
            if (!isTerminal)
            {
                _guestBellRewardsHandled = true;
                var rewardsSet = (RewardsSet)AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet").GetValue(bellRewards)!;
                Require(rewardsSet.Rewards.Count >= 3, $"Expected >= 3 CallingBell catch-up rewards, got {rewardsSet.Rewards.Count}");
                _ = TaskHelper.RunSafely(ClaimGoldAndDismissBellRewardsAsync(node, bellRewards, rewardsSet, me));
                _cooldown = 0.8;
                return;
            }
        }

        if (_phase2InitialVisitedCount < 0)
            _phase2InitialVisitedCount = state.VisitedMapCoords.Count;

        var cm = CombatManager.Instance;
        var cs = cm.DebugOnlyGetState();

        // Step E: Once 4P map vote after 2nd monster transitions us into the 3rd room, verify 4P save & pass!
        if (_phase2CombatEntered && _turn1Ended && state.VisitedMapCoords.Count > _phase2InitialVisitedCount)
        {
            if (state.CurrentRoom == null)
                return;
            if (state.CurrentRoom is CombatRoom && !IsLocalPlayPhase(me))
                return;

            Require(state.Players.Count == 4, $"Next room expected 4 players, got {state.Players.Count}");
            if (me.NetId == 3 || me.NetId == 4)
                Require(_guestBellRewardsHandled, $"Guest peer {me.NetId} never received CallingBell catch-up rewards!");
            if (me.NetId == 1)
            {
                var saved = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(1);
                if (saved.SaveData == null || saved.SaveData.VisitedMapCoords.Count < state.VisitedMapCoords.Count)
                {
                    _cooldown = 0.4;
                    return;
                }
                Require(saved.SaveData.Players.Count == 4, $"Expected 4 players in Phase2 save, got {saved.SaveData.Players.Count}");
                Log.Info($"[MidRunJoinE2E] SAVE4P_OK peer=1 players=4 visited={saved.SaveData.VisitedMapCoords.Count} room={state.CurrentRoom.RoomType}");
            }
            Log.Info($"[MidRunJoinE2E] ROOM_NEXT_ENTERED peer={me.NetId} visited={state.VisitedMapCoords.Count} room={state.CurrentRoom.RoomType}");
            Log.Info($"[MidRunJoinE2E] PHASE2_PASS peer={me.NetId} turn1CardsPlayed={_turn1CardsPlayed}");
            _done = true;
            return;
        }

        // Step C / D: Map voting before or after 2nd monster
        if (!cm.IsInProgress || cs == null)
        {
            if (_phase2CombatEntered && !_phase2RewardsProceedSent && NOverlayStack.Instance?.Peek() is NRewardsScreen)
            {
                _phase2RewardsProceedSent = true;
                Log.Info($"[MidRunJoinE2E] REWARDS_OK peer={me.NetId} proceeding from 4P combat rewards");
                SaveManager.Instance.MarkFtueAsComplete("combat_reward_ftue");
                RunManager.Instance.ProceedFromTerminalRewardsScreen();
                _cooldown = 0.8;
                return;
            }
            if (NMapScreen.Instance?.IsOpen == true || _phase2RewardsProceedSent)
            {
                VoteForNextNode(state, me, state.VisitedMapCoords.Count, preferMonster: true, requireMonsterChild: false);
            }
            return;
        }

        // Inside 4P Monster combat!
        _phase2CombatEntered = true;
        if (!IsLocalPlayPhase(me))
            return;

        if (!_scalingChecked)
        {
            _scalingChecked = true;
            Verify4PlayerScaling(state, cs, me);
        }

        if (cs.RoundNumber == 1 && !_turn1Ended)
        {
            if (cm.IsExecutingCardOrPotionEffect(me))
            {
                _cooldown = 0.25;
                return;
            }
            _rng ??= new Random((int)(me.NetId * 7919 + 42));
            // Skip cards that open interactive hand-selection prompts (e.g. Silent's Survivor discard)
            // because this headless bot does not drive card-selection sub-screens during Turn 1 combat.
            var playable = me.PlayerCombatState!.Hand.Cards
                .Where(c => c.CanPlay(out _, out _) && !string.Equals(c.Id.Entry, "SURVIVOR", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (playable.Count > 0 && _turn1CardsPlayed < 3)
            {
                var card = playable[_rng.Next(playable.Count)];
                Creature? target = PickTarget(card, cs, me, _rng);
                if (card.IsValidTarget(target))
                {
                    _turn1CardsPlayed++;
                    Log.Info($"[MidRunJoinE2E] PLAYED peer={me.NetId} card={card.Id.Entry} target={target?.Name ?? "none"}");
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
                    _cooldown = 0.4;
                    return;
                }
            }
            Require(_turn1CardsPlayed >= 1, $"Peer {me.NetId} did not play any card on Turn 1");
            _turn1Ended = true;
            Log.Info($"[MidRunJoinE2E] END_TURN_1 peer={me.NetId} cardsPlayed={_turn1CardsPlayed}");
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, me.PlayerCombatState!.TurnNumber));
            _cooldown = 0.5;
            return;
        }

        if (cs.RoundNumber >= 2 && _turn1Ended && !_turn2KillSent)
        {
            _turn2KillSent = true;
            Log.Info($"[MidRunJoinE2E] TURN_2_REACHED peer={me.NetId} round={cs.RoundNumber}");
            if (me.NetId == 1)
            {
                Log.Info("[MidRunJoinE2E] KILL_SENT peer=1 cmd='kill all'");
                RunConsoleCommand("kill all");
            }
            _cooldown = 0.5;
        }
    }

    // Phase 3: Player Removal 4P -> 3P (Host removed absent peer 4 via GUI!)
    private static void TickPhase3(Node node, RunState state, Player me)
    {
        if (_phase3Verified || state.CurrentRoom == null)
            return;
        if (state.CurrentRoom is CombatRoom && !IsLocalPlayPhase(me))
            return;

        _phase3Verified = true;
        Require(state.Players.Count == 3, $"Phase3 expected 3 players after removing peer 4, got {state.Players.Count}");
        Require(state.Players.Select(p => p.NetId).SequenceEqual(new ulong[] { 1, 2, 3 }), "Phase3 seat order mismatch");
        Require(state.Players[2].Character.Id.Entry.Equals(_guest3Char, StringComparison.OrdinalIgnoreCase), "Phase3 guest3 char mismatch");
        Require(!state.Players[2].Relics.Any(r => r is TheCourier), "Phase3 guest3 should not have temporary TheCourier");

        var cs = CombatManager.Instance.DebugOnlyGetState();
        if (CombatManager.Instance.IsInProgress && cs != null && cs.Enemies.Count > 0)
        {
            decimal? actualBlockMult = TryVerifyMultiplayerBlockScaling(state, cs, expectedPlayers: 3);
            foreach (var enemy in cs.Enemies)
            {
                int baseHp = enemy.MonsterMaxHpBeforeModification ?? 0;
                int expected3P = (int)Creature.ScaleHpForMultiplayer(baseHp, cs.Encounter, 3, state.CurrentActIndex);
                Require(enemy.MaxHp == expected3P, $"Phase3 enemy {enemy.Name} MaxHp={enemy.MaxHp} != expected 3P {expected3P}");
            }
            Log.Info($"[MidRunJoinE2E] PHASE3_SCALING_3P_OK peer={me.NetId} blockMult={actualBlockMult?.ToString() ?? "n/a"}");
        }

        Log.Info($"[MidRunJoinE2E] PHASE3_PASS peer={me.NetId} players=3 guest3={_guest3Char} room={state.CurrentRoom.RoomType} visited={state.VisitedMapCoords.Count}");
        _done = true;
    }

    private static async Task ClaimGoldAndDismissBellRewardsAsync(Node node, NRewardsScreen screen, RewardsSet rewardsSet, Player me)
    {
        try
        {
            int beforeGold = me.Gold;
            int beforeRelics = me.Relics.Count;
            var goldReward = rewardsSet.Rewards.OfType<GoldReward>().FirstOrDefault();
            Require(goldReward != null, $"Guest {me.NetId} CallingBell rewards missing GoldReward");
            await RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(goldReward!);
            Require(me.Gold > beforeGold, $"Guest {me.NetId} gold did not increase after claiming GoldReward ({beforeGold} -> {me.Gold})");

            int relicsClaimed = 0;
            if (_lateGameMode)
            {
                int relicCount = rewardsSet.Rewards.OfType<RelicReward>().Count();
                int upgradeCount = rewardsSet.Rewards.Count(r => r.GetType().Name.Contains("CardUpgradeReward", StringComparison.Ordinal));
                int removalCount = rewardsSet.Rewards.OfType<CardRemovalReward>().Count();
                int cardCount = rewardsSet.Rewards.OfType<CardReward>().Count();
                Log.Info($"[MidRunJoinE2E] LATEGAME_BELL_BREAKDOWN peer={me.NetId} total={rewardsSet.Rewards.Count} relics={relicCount} upgrades={upgradeCount} removals={removalCount} cards={cardCount}");
                Require(relicCount >= 1, $"LateGame guest {me.NetId} expected >= 1 RelicReward, got {relicCount}");
                Require(upgradeCount >= 1, $"LateGame guest {me.NetId} expected >= 1 CardUpgradeReward (from RestSites), got {upgradeCount}");
                Require(removalCount >= 1, $"LateGame guest {me.NetId} expected >= 1 CardRemovalReward (from Act 1 Boss), got {removalCount}");
                Require(cardCount >= 4, $"LateGame guest {me.NetId} expected >= 4 CardRewards, got {cardCount}");

                // Claim 1 non-interactive RelicReward (or first RelicReward without pickup modal) to prove real relic sync
                var relicReward = rewardsSet.Rewards.OfType<RelicReward>().FirstOrDefault(r => r.Relic != null && !r.Relic.HasUponPickupEffect)
                    ?? rewardsSet.Rewards.OfType<RelicReward>().FirstOrDefault();
                if (relicReward != null)
                {
                    await RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(relicReward);
                    Require(me.Relics.Count > beforeRelics, $"LateGame guest {me.NetId} relic count did not increase ({beforeRelics} -> {me.Relics.Count})");
                    relicsClaimed = me.Relics.Count - beforeRelics;
                }
            }

            RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
            if (GodotObject.IsInstanceValid(screen) && NOverlayStack.Instance?.Peek() == screen)
                NOverlayStack.Instance.Remove(screen);
            Log.Info($"[MidRunJoinE2E] CATCHUP_BELL_REWARDS_OK peer={me.NetId} totalRewards={rewardsSet.Rewards.Count} goldGained={me.Gold - beforeGold} relicsClaimed={relicsClaimed}");
        }
        catch (Exception ex)
        {
            Fail(node, $"CallingBell catch-up reward claim failed: {ex}");
        }
    }

    // LateGame Phase 1: 2P blitz through ALL of Act 1 (Monsters, Elites, RestSites, Treasure, Shop, Act 1 Boss -> Act 2!) using 'kill all'
    private static void TickLateGameP1(Node node, RunState state, Player me)
    {
        int floorKey = state.CurrentActIndex * 100 + state.VisitedMapCoords.Count;

        // Target reached: We defeated Act 1 Boss, entered Act 2 (CurrentActIndex >= 1), cleared Act 2 start, and entered Act 2 combat!
        if (state.CurrentActIndex >= 1 && state.CurrentRoom is CombatRoom && CombatManager.Instance.IsInProgress && IsLocalPlayPhase(me))
        {
            if (me.NetId == 1)
            {
                var saved = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(1);
                Require(saved.SaveData != null, "LateGame P1 host save missing");
                Require(saved.SaveData!.CurrentActIndex >= 1, $"Expected Act 2+ save, got CurrentActIndex={saved.SaveData.CurrentActIndex}");
                var counts = CatchUpHistoryScanner.Scan(state);
                Log.Info($"[MidRunJoinE2E] LATEGAME_P1_SAVE act={state.CurrentActIndex + 1} floor={state.TotalFloor} acts={state.MapPointHistory.Count} monsters={counts.MonsterCombats} elites={counts.EliteCombats} bosses={counts.BossCombats} rests={counts.RestSites} treasures={counts.TreasureRooms} ancients={counts.AncientRooms} unknownOrShop={counts.UnknownAndShopRooms}");
            }
            Log.Info($"[MidRunJoinE2E] LATEGAME_P1_PASS peer={me.NetId} act={state.CurrentActIndex + 1} floor={state.TotalFloor}");
            _done = true;
            return;
        }

        // 1. Active combat (Monster / Elite / Boss): host immediately sends 'kill all'
        if (CombatManager.Instance.IsInProgress)
        {
            if (IsLocalPlayPhase(me) && me.NetId == 1 && _lastKillFloor != floorKey)
            {
                _lastKillFloor = floorKey;
                Log.Info($"[MidRunJoinE2E] LateGameP1 host 'kill all' at act={state.CurrentActIndex + 1} floor={state.TotalFloor} room={state.CurrentRoom?.RoomType}");
                RunConsoleCommand("kill all");
                _cooldown = 0.6;
            }
            return;
        }

        // 2. Terminal Rewards Screen (Monster / Elite -> Proceed; Boss -> ActChangeSynchronizer.SetLocalPlayerReady!)
        if (_lastRewardsFloor != floorKey && NOverlayStack.Instance?.Peek() is NRewardsScreen rewardsScreen)
        {
            bool isTerminal = (bool)AccessTools.Field(typeof(NRewardsScreen), "_isTerminal").GetValue(rewardsScreen)!;
            if (isTerminal)
            {
                _lastRewardsFloor = floorKey;
                SaveManager.Instance.MarkFtueAsComplete("combat_reward_ftue");
                if (state.CurrentRoom?.RoomType == RoomType.Boss)
                {
                    Log.Info($"[MidRunJoinE2E] LateGameP1 peer={me.NetId} Act 1 Boss cleared! Ready for Act 2 transition");
                    RunManager.Instance.ActChangeSynchronizer.SetLocalPlayerReady();
                }
                else
                {
                    RunManager.Instance.ProceedFromTerminalRewardsScreen();
                }
                _cooldown = 0.7;
                return;
            }
        }

        // 3. EventRoom (including Neow / Act 2 Ancient / Unknown events)
        if (state.CurrentRoom is EventRoom)
        {
            if (NEventRoom.Instance == null)
                return;
            if (HandleEventIfActive(state, me, preferProceed: true))
                return;
            var ev = TryGetLocalEvent();
            if (ev == null || !ev.IsFinished)
                return;
        }

        // 4. RestSiteRoom -> pick Heal (index 0)
        if (state.CurrentRoom is RestSiteRoom)
        {
            if (_lastRestFloor != floorKey)
            {
                _lastRestFloor = floorKey;
                Log.Info($"[MidRunJoinE2E] LateGameP1 peer={me.NetId} RestSite heal at floor={state.TotalFloor}");
                _ = TaskHelper.RunSafely(RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(0));
                _cooldown = 1.0;
                return;
            }
        }

        // 5. TreasureRoom -> each peer picks a distinct relic slot
        if (state.CurrentRoom is TreasureRoom)
        {
            if (_lastTreasureFloor != floorKey)
            {
                var relics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
                if (relics != null)
                {
                    _lastTreasureFloor = floorKey;
                    if (relics.Count > 0)
                        RunManager.Instance.TreasureRoomRelicSynchronizer.PickRelicLocally((int)(me.NetId - 1) % relics.Count);
                    else
                        RunManager.Instance.TreasureRoomRelicSynchronizer.SkipRelicLocally();
                    Log.Info($"[MidRunJoinE2E] LateGameP1 peer={me.NetId} Treasure picked at floor={state.TotalFloor}");
                    _cooldown = 1.2;
                }
                return;
            }
        }

        // Never vote while inside an active/loading CombatRoom before its rewards have been dismissed!
        if (state.CurrentRoom is CombatRoom && _lastRewardsFloor != floorKey)
            return;

        // Boss room transitions via ActChangeSynchronizer, not map vote
        if (state.CurrentRoom?.RoomType == RoomType.Boss)
            return;

        // 6. Map vote -> advance toward Act 1 Boss (prefer diverse rooms: Boss > Treasure > RestSite > Elite > Shop > Monster > Unknown)
        bool canVote = NMapScreen.Instance?.IsOpen == true
            || state.CurrentRoom is MerchantRoom
            || (state.CurrentRoom is RestSiteRoom && _lastRestFloor == floorKey)
            || (state.CurrentRoom is TreasureRoom && _lastTreasureFloor == floorKey)
            || (state.CurrentRoom is EventRoom)
            || (_lastRewardsFloor == floorKey);
        if (canVote)
            VoteForNextNodeLateGame(state, me, floorKey);
    }

    private static void VoteForNextNodeLateGame(RunState state, Player me, int floorKey)
    {
        var current = state.CurrentMapPoint ?? state.Map.StartingMapPoint;
        if (current == null || current.Children.Count == 0)
            return;
        if (!_mapVoteSentForVisitedCount.Add(floorKey))
            return;

        static int RoomPriority(MapPointType t) => t switch
        {
            MapPointType.Boss => 0,
            MapPointType.Treasure => 1,
            MapPointType.RestSite => 2,
            MapPointType.Elite => 3,
            MapPointType.Shop => 4,
            MapPointType.Monster => 5,
            MapPointType.Ancient => 6,
            MapPointType.Unknown => 7,
            _ => 8
        };

        var chosen = current.Children
            .OrderBy(p => RoomPriority(p.PointType))
            .ThenBy(p => p.coord.col)
            .ThenBy(p => p.coord.row)
            .First();

        var vote = new MapVote
        {
            coord = chosen.coord,
            mapGenerationCount = RunManager.Instance.MapSelectionSynchronizer.MapGenerationCount
        };
        Log.Info($"[MidRunJoinE2E] LateGameP1 peer={me.NetId} voting {state.MapLocation} -> {chosen.coord} ({chosen.PointType})");
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new VoteForMapCoordAction(me, state.MapLocation, vote));
        _cooldown = 0.8;
    }

    private static EventModel? TryGetLocalEvent()
    {
        try
        {
            return RunManager.Instance.EventSynchronizer.GetLocalEvent();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int _eventAttemptFloor = -1;
    private static int _eventAttemptCount;

    private static bool HandleEventIfActive(RunState state, Player me, bool preferProceed = false)
    {
        if (state.CurrentRoom is not EventRoom)
            return false;
        var ev = TryGetLocalEvent();
        if (NMapScreen.Instance?.IsOpen != true && ev != null && !ev.IsFinished && ev.CurrentOptions.Count > 0)
        {
            int idx = -1;
            if (preferProceed)
            {
                for (int i = 0; i < ev.CurrentOptions.Count; i++)
                {
                    if (!ev.CurrentOptions[i].IsLocked && ev.CurrentOptions[i].IsProceed)
                    {
                        idx = i;
                        break;
                    }
                }
            }
            if (idx < 0)
            {
                if (_eventAttemptFloor != state.TotalFloor)
                {
                    _eventAttemptFloor = state.TotalFloor;
                    _eventAttemptCount = 0;
                }
                var unlocked = new List<int>();
                for (int i = 0; i < ev.CurrentOptions.Count; i++)
                {
                    if (!ev.CurrentOptions[i].IsLocked)
                        unlocked.Add(i);
                }
                if (unlocked.Count > 0)
                {
                    // Prefer last option (usually Leave/Decline) first in late-game blitz, cycling if needed
                    idx = preferProceed
                        ? unlocked[(unlocked.Count - 1 - (_eventAttemptCount % unlocked.Count) + unlocked.Count) % unlocked.Count]
                        : unlocked[0];
                    _eventAttemptCount++;
                }
            }
            if (idx >= 0)
            {
                Log.Info($"[MidRunJoinE2E] peer={me.NetId} choosing event option {idx} (isProceed={ev.CurrentOptions[idx].IsProceed})");
                RunManager.Instance.EventSynchronizer.ChooseLocalOption(idx);
                _cooldown = 0.8;
                return true;
            }
        }
        if (NOverlayStack.Instance?.Peek() is NRewardsScreen rs)
        {
            bool isTerminal = (bool)AccessTools.Field(typeof(NRewardsScreen), "_isTerminal").GetValue(rs)!;
            if (isTerminal)
                RunManager.Instance.ProceedFromTerminalRewardsScreen();
            else
            {
                RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
                NOverlayStack.Instance.Remove(rs);
            }
            _cooldown = 0.8;
            return true;
        }
        return false;
    }

    private static void VoteForNextNode(RunState state, Player me, int visitedCount, bool preferMonster, bool requireMonsterChild)
    {
        var current = state.CurrentMapPoint ?? state.Map.StartingMapPoint;
        if (current == null || current.Children.Count == 0)
            return;
        if (!_mapVoteSentForVisitedCount.Add(visitedCount))
            return;

        MapPoint? chosen = null;
        if (preferMonster)
        {
            var monsters = current.Children
                .Where(p => p.PointType == MapPointType.Monster)
                .OrderBy(p => p.coord.col)
                .ThenBy(p => p.coord.row)
                .ToList();
            if (requireMonsterChild)
                chosen = monsters.FirstOrDefault(p => p.Children.Any(c => c.PointType == MapPointType.Monster));
            chosen ??= monsters.FirstOrDefault();
        }
        chosen ??= current.Children.OrderBy(p => p.coord.col).ThenBy(p => p.coord.row).FirstOrDefault();
        Require(chosen != null, $"No child map point found from {current.coord}");

        var vote = new MapVote
        {
            coord = chosen!.coord,
            mapGenerationCount = RunManager.Instance.MapSelectionSynchronizer.MapGenerationCount
        };
        Log.Info($"[MidRunJoinE2E] {_phase} peer={me.NetId} voting map {state.MapLocation} -> {chosen.coord} ({chosen.PointType})");
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new VoteForMapCoordAction(me, state.MapLocation, vote));
        _cooldown = 1.0;
    }

    private static void Verify4PlayerScaling(RunState state, CombatState cs, Player me)
    {
        Require(state.Players.Count == 4, $"Expected 4 RunState players, got {state.Players.Count}");
        Require(state.Players.Select(p => p.NetId).SequenceEqual(new ulong[] { 1, 2, 3, 4 }), "Unexpected 4P NetIds");
        Require(state.Players[2].Character.Id.Entry.Equals(_guest3Char, StringComparison.OrdinalIgnoreCase), "Guest 3 char mismatch");
        Require(state.Players[3].Character.Id.Entry.Equals(_guest4Char, StringComparison.OrdinalIgnoreCase), "Guest 4 char mismatch");
        Require(cs.Players.Count == 4, $"Expected 4 CombatState players, got {cs.Players.Count}");
        decimal? actualBlockMult = TryVerifyMultiplayerBlockScaling(state, cs, expectedPlayers: 4);

        foreach (var enemy in cs.Enemies)
        {
            int baseHp = enemy.MonsterMaxHpBeforeModification ?? 0;
            Require(baseHp > 0, $"Enemy {enemy.Name} invalid baseHp={baseHp}");
            int expected4P = (int)Creature.ScaleHpForMultiplayer(baseHp, cs.Encounter, 4, state.CurrentActIndex);
            int expected2P = (int)Creature.ScaleHpForMultiplayer(baseHp, cs.Encounter, 2, state.CurrentActIndex);
            Require(enemy.MaxHp == expected4P,
                $"Enemy {enemy.Name} MaxHp={enemy.MaxHp} != expected 4P HP {expected4P} (base={baseHp}, 2P={expected2P})");
            Log.Info($"[MidRunJoinE2E] ENEMY_SCALING peer={me.NetId} enemy={enemy.Name} baseHp={baseHp} maxHp={enemy.MaxHp} expected4P={expected4P} blockMult={actualBlockMult?.ToString() ?? "n/a"}");
        }
        Log.Info($"[MidRunJoinE2E] SCALING_OK peer={me.NetId} players=4 guests=[{_guest3Char},{_guest4Char}] blockMult={actualBlockMult?.ToString() ?? "n/a"}");
    }

    private static decimal? TryVerifyMultiplayerBlockScaling(RunState state, CombatState cs, int expectedPlayers)
    {
        var scalingType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.MultiplayerScalingModel");
        var runModelsProp = AccessTools.Property(typeof(RunState), "RunModels");
        if (scalingType == null || runModelsProp == null)
            return null;

        var runModels = runModelsProp.GetValue(state) as System.Collections.IEnumerable;
        object? scalingModel = null;
        if (runModels != null)
        {
            foreach (var item in runModels)
            {
                if (item != null && scalingType.IsInstanceOfType(item))
                {
                    scalingModel = item;
                    break;
                }
            }
        }
        Require(scalingModel != null, $"Missing MultiplayerScalingModel for {expectedPlayers}P");

        var getScale = AccessTools.Method(scalingType, "GetMultiplayerScaling", [typeof(EncounterModel), typeof(int)]);
        var modBlock = AccessTools.Method(scalingType, "ModifyBlockMultiplicative");
        if (getScale == null || modBlock == null)
            return null;

        decimal actScale = (decimal)getScale.Invoke(null, [cs.Encounter, state.CurrentActIndex])!;
        decimal expectedBlockMult = expectedPlayers * actScale;
        decimal actualBlockMult = (decimal)modBlock.Invoke(scalingModel, [cs.Enemies[0], 10m, ValueProp.Move, null, null])!;
        Require(actualBlockMult == expectedBlockMult,
            $"{expectedPlayers}P block mult expected {expectedBlockMult}, got {actualBlockMult}");
        return actualBlockMult;
    }

    private static Creature? PickTarget(CardModel card, CombatState cs, Player me, Random rng)
    {
        if (card.TargetType == TargetType.AnyEnemy)
        {
            var enemies = cs.HittableEnemies;
            return enemies.Count > 0 ? enemies[rng.Next(enemies.Count)] : null;
        }
        if (card.TargetType == TargetType.AnyAlly)
        {
            var allies = cs.Allies.Where(a => a.IsAlive).ToList();
            return allies.Count > 0 ? allies[rng.Next(allies.Count)] : me.Creature;
        }
        return null;
    }

    private static bool IsLocalPlayPhase(Player me)
    {
        var cm = CombatManager.Instance;
        return cm.IsInProgress
               && !cm.IsStarting
               && !cm.IsEnemyTurnStarted
               && !cm.IsOverOrEnding
               && cm.IsPartOfPlayerTurn(me)
               && me.PlayerCombatState != null
               && me.PlayerCombatState.Hand.Cards.Count > 0;
    }

    private static void RunConsoleCommand(string cmd)
    {
        var devConsole = (DevConsole)AccessTools.Field(typeof(NDevConsole), "_devConsole").GetValue(NDevConsole.Instance)!;
        var res = devConsole.ProcessCommand(cmd);
        Require(res.success, $"DevConsole '{cmd}' failed: {res.msg}");
        Log.Info($"[MidRunJoinE2E] DevConsole '{cmd}' -> {res.msg}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Fail(Node node, string error)
    {
        if (_done)
            return;
        _done = true;
        Log.Error($"[MidRunJoinE2E] FAIL: {error}");
        node.GetTree().Quit(1);
    }
}

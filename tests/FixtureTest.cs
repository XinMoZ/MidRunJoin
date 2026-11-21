using System;
using System.Linq;
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
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace MidRunJoinSmoke;

[ModInitializer(nameof(Init)), HarmonyPatch]
public static class FixtureTest
{
    private static bool lobbyChecked;

    public static void Init()
    {
        if (CommandLineHelper.HasArg("midrunjoin-smoke") && CommandLineHelper.HasArg("fastmp"))
            new Harmony("xinmo.MidRunJoin.Smoke").PatchAll(typeof(FixtureTest).Assembly);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(NMainMenu), "CheckCommandLineArgs")]
    private static bool OpenFixture(NMainMenu __instance)
    {
        try
        {
            var menu = (NMultiplayerSubmenu)AccessTools.Method(typeof(NMainMenu), "OpenMultiplayerSubmenu")
                .Invoke(__instance, null)!;
            var checkpoint = CreateCheckpoint();
            CheckCandidate(checkpoint);
            var dialog = ShiftLoad(menu, checkpoint);
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            dialog.Hide();
            dialog = ShiftLoad(menu, checkpoint);
            var input = dialog.GetChildren().OfType<VBoxContainer>().SelectMany(c => c.GetChildren()).OfType<LineEdit>().Single();
            input.Text = "2";
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            Require(dialog.Visible, "Existing player accepted");
            input.Text = "3";
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            Require(!dialog.Visible, "Guest rejected");
        }
        catch (Exception e)
        {
            Log.Error($"[MidRunJoinSmoke] FAIL: {e}");
            __instance.GetTree().Quit(1);
        }
        return false;
    }

    private static ConfirmationDialog ShiftLoad(NMultiplayerSubmenu menu, SerializableRun checkpoint)
    {
        static void Shift(bool down)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Shift, PhysicalKeycode = Key.Shift, Pressed = down });
            Input.FlushBufferedEvents();
        }
        Shift(true);
        try { menu.StartHost(checkpoint); }
        finally { Shift(false); }
        return menu.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Visible);
    }

    private static SerializableRun CreateCheckpoint()
    {
        Player[] players = [Player.CreateForNewRun<Ironclad>(UnlockState.none, 1), Player.CreateForNewRun<Ironclad>(UnlockState.none, 2)];
        var acts = ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList();
        var state = RunState.CreateForNewRun(players, acts, [], GameMode.Standard, false, 0, "MIDJOIN");
        foreach (var act in state.Acts)
            act.GenerateRooms(state);
        foreach (var player in players)
            player.PopulateRelicGrabBagIfNecessary(state.Rng.UpFront);
        var map = SerializableActMap.FromActMap(state.Act.CreateMap(state, false));
        var savedActs = state.Acts.Select(a => a.ToSave()).ToList();
        savedActs[0].SavedMap = map;
        var previous = new MapPointHistoryEntry(MapPointType.Ancient, state);
        previous.Rooms.Add(new MapPointRoomHistoryEntry { RoomType = RoomType.Event });
        return new SerializableRun
        {
            SchemaVersion = SaveManager.Instance.GetLatestSchemaVersion<SerializableRun>(),
            Players = players.Select(p => p.ToSerializable()).ToList(),
            Acts = savedActs, GameMode = GameMode.Standard,
            VisitedMapCoords = [map.StartingPoint.Coord, map.Points.First(p => p.PointType == MapPointType.Monster).Coord],
            MapPointHistory = [[previous]],
            SerializableRng = state.Rng.ToSerializable(), SerializableOdds = state.Odds.ToSerializable(),
            SerializableSharedRelicGrabBag = state.SharedRelicGrabBag.ToSerializable()
        };
    }

    private static void CheckCandidate(SerializableRun checkpoint)
    {
        var prepare = AccessTools.Method(AccessTools.TypeByName("MidRunJoin.Candidate"), "Prepare");
        SerializableRun Prepare() => (SerializableRun)prepare.Invoke(null, [checkpoint, 3UL])!;
        string before = JsonSerializationUtility.ToJson(checkpoint);
        var candidate = Prepare();
        Require(JsonSerializationUtility.ToJson(candidate) == JsonSerializationUtility.ToJson(Prepare()), "Retry diverged");
        Require(before == JsonSerializationUtility.ToJson(checkpoint), "Checkpoint changed");
        var writer = new PacketWriter();
        candidate.Serialize(writer);
        var reader = new PacketReader();
        reader.Reset(writer.Buffer);
        var packet = new SerializableRun();
        packet.Deserialize(reader);
        var restored = RunState.FromSerializable(packet);
        Require(restored.Players.Select(p => p.NetId).SequenceEqual(new ulong[] { 1, 2, 3 }), "Seat order changed");
        var guest = restored.Players[2];
        Require(guest.Deck.Cards.Count > 0 && guest.Relics.Count > 0 && guest.RelicGrabBag.IsPopulated, "Guest inventory missing");
        Require(restored.CurrentMapPointHistoryEntry?.GetEntry(3).PlayerId == 3, "Boundary history missing");
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
            lobby.Players.Clear();
            lobby.Players.Add(new LoadRunLobbyPlayer { id = 1, isReady = true });
            lobby.Players.Add(new LoadRunLobbyPlayer { id = 2, isReady = true });
            Require(!lobby.IsAboutToBeginGame(), "Missing guest accepted");
            lobby.Players.Add(new LoadRunLobbyPlayer { id = 4, isReady = true });
            Require(!lobby.IsAboutToBeginGame(), "Wrong guest accepted");
            lobby.Players[2] = new LoadRunLobbyPlayer { id = 3, isReady = false };
            Require(!lobby.IsAboutToBeginGame(), "Unready guest accepted");
            lobby.Players[2] = new LoadRunLobbyPlayer { id = 3, isReady = true };
            Require(lobby.IsAboutToBeginGame(), "Complete roster rejected");
            Log.Info("[MidRunJoinSmoke] PASS");
            __instance.GetTree().Quit();
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
        if (!condition) throw new InvalidOperationException(error);
    }
}

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;

namespace MidRunJoin;

[HarmonyPatch]
internal static class JoinLobby
{
    private static readonly ConditionalWeakTable<SerializableRun, object> Expanded = new();

    [HarmonyPrefix, HarmonyPatch(typeof(NMultiplayerSubmenu), nameof(NMultiplayerSubmenu.StartHost))]
    private static bool ChooseGuest(NMultiplayerSubmenu __instance, SerializableRun run)
    {
        if (Expanded.TryGetValue(run, out _) || !Input.IsKeyPressed(Key.Shift))
            return true;

        var dialog = new ConfirmationDialog
        {
            Title = "中途加入",
            OkButtonText = "建立扩员大厅",
            CancelButtonText = "取消",
            DialogHideOnOk = false,
            Exclusive = true
        };
        var fields = new VBoxContainer();
        var message = new Label { Text = "输入新人的 SteamID64 或本地 ENet clientId。\n新人以铁甲战士的初始牌组、金币和遗物加入。" };
        var input = new LineEdit { PlaceholderText = "玩家 ID" };
        fields.AddChild(message);
        fields.AddChild(input);
        dialog.AddChild(fields);
        __instance.AddChild(dialog);
        dialog.Canceled += dialog.QueueFree;
        dialog.Confirmed += () =>
        {
            if (!ulong.TryParse(input.Text.Trim(), out ulong id) || id == 0)
            {
                message.Text = "请输入有效的非零数字玩家 ID。";
                return;
            }
            try
            {
                StartExpandedLobby(__instance, run, id);
                dialog.Hide();
                dialog.QueueFree();
            }
            catch (Exception ex)
            {
                Log.Warn($"[MidRunJoin] Candidate preparation failed: {ex}");
                message.Text = ex is InvalidOperationException ? ex.Message : "扩员失败，请查看游戏日志。";
            }
        };
        dialog.PopupCentered(new Vector2I(580, 200));
        input.GrabFocus();
        return false;
    }

    internal static void StartExpandedLobby(NMultiplayerSubmenu menu, SerializableRun checkpoint, ulong id)
    {
        var candidate = Candidate.Prepare(checkpoint, id);
        Expanded.Add(candidate, new object());
        Log.Info($"[MidRunJoin] Prepared guest {id} for {candidate.Players.Count} seats.");
        menu.StartHost(candidate);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.IsAboutToBeginGame))]
    private static void RequireRoster(LoadRunLobby __instance, ref bool __result)
    {
        if (__result && Expanded.TryGetValue(__instance.Run, out _))
            __result = __instance.PlayerCount == __instance.Run.Players.Count &&
                       __instance.Run.Players.All(p => __instance.IsPlayerReady(p.NetId));
    }
}

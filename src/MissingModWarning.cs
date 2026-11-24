using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MidRunJoin.CatchUp;

namespace MidRunJoin;

public enum MissingModPeerRole
{
    NewGuest,
    RestoredGuest,
    ExistingTeammate
}

public sealed record MissingModPeerInfo(
    ulong PeerId,
    string DisplayName,
    MissingModPeerRole Role);

/// <summary>
/// Detects peers who join an expanded <see cref="LoadRunLobby"/> without MidRunJoin installed
/// (possible because <c>affects_gameplay</c> is <c>false</c>), switches catch-up rewards to
/// safe-sync mode so unmodded peers never desync, and displays a host-side warning modal listing
/// all missing-mod players with "Open Workshop" and "Ignore &amp; Enter" actions.
/// </summary>
[HarmonyPatch]
internal static class MissingModWarning
{
    internal const string WorkshopUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=3844124271";
    internal const string ModalName = "MidRunJoinMissingModModal";
    internal const string PlayerListName = "MissingModPlayerList";
    internal const string OpenWorkshopButtonName = "OpenWorkshopButton";
    internal const string IgnoreButtonName = "IgnoreMissingModButton";
    internal const string ToastName = "MidRunJoinToast";

    private static readonly MethodInfo? BeginRunIfAllReadyMethod =
        AccessTools.Method(typeof(LoadRunLobby), "BeginRunForAllPlayersIfAllReady");

    internal static bool HasMidRunJoinMod(PeerVersionInfo versionInfo)
    {
        return ContainsMidRunJoin(versionInfo.otherMods) ||
               ContainsMidRunJoin(versionInfo.gameplayAffectingMods);
    }

    internal static bool ContainsMidRunJoin(IEnumerable<string>? modNames)
    {
        if (modNames == null)
            return false;
        foreach (string name in modNames)
        {
            if (!string.IsNullOrWhiteSpace(name) &&
                name.IndexOf("MidRunJoin", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    internal static string GetIgnoreButtonText(IReadOnlyCollection<MissingModPeerInfo> peers)
    {
        foreach (var peer in peers)
        {
            if (peer.Role == MissingModPeerRole.NewGuest)
                return ModLoc.MissingModIgnoreStarterButton;
        }
        return ModLoc.MissingModIgnoreSavedButton;
    }

    internal static Control ShowOrRefresh(
        Control hostParent,
        IReadOnlyList<MissingModPeerInfo> missingPeers,
        Action onIgnore)
    {
        CloseIfOpen(hostParent);

        var modalRoot = new Control
        {
            Name = ModalName,
            ZIndex = 120,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        modalRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var backdrop = new ColorRect
        {
            Name = "Backdrop",
            Color = StsColors.screenBackdrop,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        modalRoot.AddChild(backdrop);

        var center = new CenterContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        modalRoot.AddChild(center);

        int listHeight = Math.Clamp(missingPeers.Count * 42 + 12, 64, 210);
        int panelHeight = 250 + listHeight;
        var panel = new PanelContainer
        {
            Name = "MissingModPanel",
            CustomMinimumSize = new Vector2(560, panelHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        panel.AddThemeStyleboxOverride("panel", VanillaUi.MakeBox(
            bg: VanillaUi.PanelBg,
            border: VanillaUi.GoldBorder,
            borderWidth: 2,
            radius: 12,
            padH: 26,
            padV: 22));
        center.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        panel.AddChild(vbox);

        var title = new Label
        {
            Name = "TitleLabel",
            Text = ModLoc.MissingModPopupTitle,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        VanillaUi.ApplyFont(title, FontType.Bold, 24, StsColors.gold, outlineSize: 4);
        vbox.AddChild(title);

        var sep = new ColorRect
        {
            CustomMinimumSize = new Vector2(0, 1),
            Color = new Color(0.68f, 0.56f, 0.32f, 0.45f)
        };
        vbox.AddChild(sep);

        var bodyLabel = new Label
        {
            Name = "BodyLabel",
            Text = ModLoc.MissingModPopupBody,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        VanillaUi.ApplyFont(bodyLabel, FontType.Regular, 17, StsColors.cream, outlineSize: 2);
        vbox.AddChild(bodyLabel);

        // Vertical list of missing-mod players
        var listFrame = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, listHeight)
        };
        listFrame.AddThemeStyleboxOverride("panel", VanillaUi.MakeBox(
            bg: VanillaUi.SectionBg,
            border: VanillaUi.SubtleBorder,
            borderWidth: 1,
            radius: 8,
            padH: 10,
            padV: 8));
        vbox.AddChild(listFrame);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        listFrame.AddChild(scroll);

        var listBox = new VBoxContainer
        {
            Name = PlayerListName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        listBox.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(listBox);

        foreach (var peer in missingPeers)
        {
            var rowPanel = new PanelContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            rowPanel.AddThemeStyleboxOverride("panel", VanillaUi.MakeBox(
                bg: VanillaUi.RowBg,
                border: VanillaUi.SubtleBorder,
                borderWidth: 1,
                radius: 6,
                padH: 12,
                padV: 6));

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            rowPanel.AddChild(row);

            var nameLabel = new Label
            {
                Text = peer.DisplayName,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true
            };
            VanillaUi.ApplyFont(nameLabel, FontType.Bold, 16, StsColors.cream, outlineSize: 2);
            row.AddChild(nameLabel);

            (string badgeText, Color badgeBg, Color badgeFg) = peer.Role switch
            {
                MissingModPeerRole.NewGuest => (ModLoc.NewGuestBadge, new Color(0.14f, 0.30f, 0.22f, 0.95f), StsColors.green),
                MissingModPeerRole.RestoredGuest => (ModLoc.SavedProgressBadge, new Color(0.28f, 0.22f, 0.10f, 0.95f), StsColors.gold),
                _ => (ModLoc.MemberBadge, new Color(0.18f, 0.22f, 0.28f, 0.95f), StsColors.cream)
            };
            var badgePanel = new PanelContainer();
            badgePanel.AddThemeStyleboxOverride("panel", VanillaUi.MakeBox(badgeBg, badgeFg, 1, 5, 8, 2));
            var badgeLabel = new Label { Text = badgeText };
            VanillaUi.ApplyFont(badgeLabel, FontType.Bold, 13, badgeFg, outlineSize: 1);
            badgePanel.AddChild(badgeLabel);
            row.AddChild(badgePanel);

            listBox.AddChild(rowPanel);
        }

        var hintLabel = new Label
        {
            Name = "WorkshopHintLabel",
            Text = "",
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        VanillaUi.ApplyFont(hintLabel, FontType.Regular, 14, StsColors.green, outlineSize: 1);
        vbox.AddChild(hintLabel);

        var buttonRow = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.End
        };
        buttonRow.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(buttonRow);

        var workshopBtn = new Button
        {
            Name = OpenWorkshopButtonName,
            Text = ModLoc.MissingModOpenWorkshopButton,
            CustomMinimumSize = new Vector2(165, 42),
            FocusMode = Control.FocusModeEnum.None
        };
        VanillaUi.StylePrimaryButton(workshopBtn, 15);
        workshopBtn.Pressed += () =>
        {
            if (SteamHelper.TryOpenWorkshopPage(WorkshopUrl))
            {
                hintLabel.Text = ModLoc.MissingModWorkshopOpenedHint;
                hintLabel.Visible = true;
            }
            else
            {
                ShowToast(modalRoot, ModLoc.MissingModWorkshopUnavailableToast);
            }
        };

        var ignoreBtn = new Button
        {
            Name = IgnoreButtonName,
            Text = GetIgnoreButtonText(missingPeers),
            CustomMinimumSize = new Vector2(215, 42),
            FocusMode = Control.FocusModeEnum.None
        };
        VanillaUi.StyleCancelButton(ignoreBtn, 15);
        ignoreBtn.Pressed += () =>
        {
            CloseIfOpen(hostParent);
            onIgnore();
        };

        buttonRow.AddChild(workshopBtn);
        buttonRow.AddChild(ignoreBtn);

        hostParent.AddChild(modalRoot);
        return modalRoot;
    }

    internal static Control ShowToast(Control host, string message)
    {
        if (host.GetNodeOrNull<Control>(ToastName) is { } existing)
        {
            host.RemoveChild(existing);
            existing.QueueFree();
        }

        var toast = new PanelContainer
        {
            Name = ToastName,
            ZIndex = 200,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 1f,
            AnchorBottom = 1f,
            OffsetLeft = -220,
            OffsetRight = 220,
            OffsetTop = -112,
            OffsetBottom = -64,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Begin
        };
        toast.AddThemeStyleboxOverride("panel", VanillaUi.MakeBox(
            bg: new Color(0.06f, 0.08f, 0.11f, 0.82f),
            border: new Color(0.784f, 0.635f, 0.290f, 0.72f),
            borderWidth: 1,
            radius: 10,
            padH: 20,
            padV: 10));

        var label = new Label
        {
            Name = "ToastLabel",
            Text = message,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        VanillaUi.ApplyFont(label, FontType.Bold, 16, StsColors.cream, outlineSize: 2);
        toast.AddChild(label);

        host.AddChild(toast);

        toast.Modulate = new Color(1f, 1f, 1f, 0f);
        var tween = toast.CreateTween();
        tween.TweenProperty(toast, "modulate:a", 0.92f, 0.15);
        tween.TweenInterval(2.2);
        tween.TweenProperty(toast, "modulate:a", 0f, 0.35);
        tween.TweenCallback(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(toast))
                toast.QueueFree();
        }));

        return toast;
    }

    internal static void CloseIfOpen(Control? hostParent)
    {
        if (hostParent == null || !GodotObject.IsInstanceValid(hostParent))
            return;
        if (hostParent.GetNodeOrNull<Control>(ModalName) is { } existing)
        {
            existing.Visible = false;
            hostParent.RemoveChild(existing);
            existing.QueueFree();
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(LoadRunLobby), "HandleClientLoadJoinRequestMessage")]
    private static void OnClientJoinedLoadLobby(LoadRunLobby __instance, ulong senderId)
    {
        try
        {
            if (__instance.NetService.Type != NetGameType.Host)
                return;
            if (!JoinLobby.TryGetExpandedSession(__instance.Run, out var session))
                return;
            if (!__instance.Players.Any(p => p.id == senderId))
                return;

            var hostService = (INetHostGameService)__instance.NetService;
            var versionInfo = hostService.GetVersionInfoForPeer(senderId);
            if (!versionInfo.HasValue || HasMidRunJoinMod(versionInfo.Value))
                return;

            session.HasAnyMissingModPeer = true;
            session.AcknowledgedMissingModIds.Remove(senderId);

            // Clear catch-up marker for this unmodded peer (if they were a guest) and downgrade
            // any remaining pending guests in Run to Safe-Sync mode for subsequent joiners.
            DowngradeRunMarkersForUnmoddedPeer(__instance.Run, senderId);

            int seatIndex = __instance.Run.Players.FindIndex(p => p.NetId == senderId);
            string displayName = SteamHelper.FormatPlayerIdentity(
                senderId,
                SteamHelper.TryGetPlayerName(senderId),
                seatIndex >= 0 ? seatIndex + 1 : 0);
            var role = session.GetPeerRole(senderId);
            session.MissingModPeers[senderId] = new MissingModPeerInfo(senderId, displayName, role);

            Log.Warn($"[MidRunJoin] Peer {senderId} ({displayName}, role={role}) joined without MidRunJoin; switching catch-up to safe-sync and alerting host.");

            if (__instance.LobbyListener is Control hostScreen && GodotObject.IsInstanceValid(hostScreen))
            {
                ShowOrRefresh(hostScreen, session.GetSortedMissingModPeers(), () =>
                {
                    foreach (ulong id in session.MissingModPeers.Keys)
                        session.AcknowledgedMissingModIds.Add(id);
                    Log.Info($"[MidRunJoin] Host ignored missing-mod warning for {session.MissingModPeers.Count} peer(s).");
                    BeginRunIfAllReadyMethod?.Invoke(__instance, null);
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[MidRunJoin] Failed during missing-mod lobby check for {senderId}: {ex.Message}");
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(LoadRunLobby), "OnDisconnectedFromClientAsHost")]
    private static void OnClientDisconnectedFromHost(LoadRunLobby __instance, ulong playerId)
    {
        HandleMissingModPeerLeft(__instance, playerId);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(LoadRunLobby), "HandlePlayerLeftMessage")]
    private static void OnPlayerLeftLobby(LoadRunLobby __instance, PlayerLeftMessage message)
    {
        HandleMissingModPeerLeft(__instance, message.playerId);
    }

    private static void HandleMissingModPeerLeft(LoadRunLobby lobby, ulong playerId)
    {
        try
        {
            if (lobby.NetService.Type != NetGameType.Host)
                return;
            if (!JoinLobby.TryGetExpandedSession(lobby.Run, out var session))
                return;
            if (!session.MissingModPeers.Remove(playerId))
                return;

            session.AcknowledgedMissingModIds.Remove(playerId);
            if (lobby.LobbyListener is Control hostScreen && GodotObject.IsInstanceValid(hostScreen))
            {
                if (session.MissingModPeers.Count == 0)
                {
                    CloseIfOpen(hostScreen);
                }
                else if (hostScreen.GetNodeOrNull<Control>(ModalName) != null)
                {
                    ShowOrRefresh(hostScreen, session.GetSortedMissingModPeers(), () =>
                    {
                        foreach (ulong id in session.MissingModPeers.Keys)
                            session.AcknowledgedMissingModIds.Add(id);
                        BeginRunIfAllReadyMethod?.Invoke(lobby, null);
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[MidRunJoin] Failed updating missing-mod modal after {playerId} left: {ex.Message}");
        }
    }

    [HarmonyPrefix, HarmonyPatch(typeof(LoadRunLobby), "BeginRunLocally")]
    private static void RecordLobbyModStateBeforeBegin(LoadRunLobby __instance)
    {
        bool allModded = __instance.Players.All(p => p.isModded);
        if (JoinLobby.TryGetExpandedSession(__instance.Run, out var session) && session.HasAnyMissingModPeer)
            allModded = false;
        CatchUpCoordinator.LastLobbyAllPeersModded = allModded;
        Log.Info($"[MidRunJoin] LoadRunLobby.BeginRunLocally: allPeersModded={allModded} (players={__instance.Players.Count}).");
    }

    internal static void DowngradeRunMarkersForUnmoddedPeer(SerializableRun run, ulong unmoddedPeerId)
    {
        foreach (var player in run.Players)
        {
            if (player.Relics.Count == 0)
                continue;
            int? rawOpt = player.Relics[0].FloorAddedToDeck;
            if (!rawOpt.HasValue || rawOpt.Value < 240)
                continue;
            int raw = rawOpt.Value;

            if (player.NetId == unmoddedPeerId)
            {
                player.Relics[0].FloorAddedToDeck = 1;
            }
            else
            {
                CatchUpCoordinator.DecodeMarker(raw, out int idx, out bool isRestored, out _);
                string flowId = CatchUpRegistry.GetByIndex(idx).Id;
                player.Relics[0].FloorAddedToDeck = CatchUpCoordinator.EncodeFlowMarker(flowId, isRestored, safeSync: true);
            }
        }
    }
}

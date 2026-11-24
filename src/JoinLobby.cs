using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MidRunJoin.CatchUp;

namespace MidRunJoin;

[HarmonyPatch]
internal static class JoinLobby
{
    internal sealed class ExpandedSession
    {
        public ulong[] GuestIds { get; }
        public HashSet<ulong> GuestIdSet { get; }
        public HashSet<ulong> RestoredGuestIds { get; }
        public string FlowId { get; }
        public Dictionary<ulong, MissingModPeerInfo> MissingModPeers { get; } = new();
        public HashSet<ulong> AcknowledgedMissingModIds { get; } = new();
        public bool HasAnyMissingModPeer { get; set; }

        public ExpandedSession(ulong[] guestIds, HashSet<ulong> restoredGuestIds, string flowId)
        {
            GuestIds = guestIds;
            GuestIdSet = new HashSet<ulong>(guestIds);
            RestoredGuestIds = restoredGuestIds;
            FlowId = flowId;
        }

        public MissingModPeerRole GetPeerRole(ulong peerId)
        {
            if (RestoredGuestIds.Contains(peerId))
                return MissingModPeerRole.RestoredGuest;
            if (GuestIdSet.Contains(peerId))
                return MissingModPeerRole.NewGuest;
            return MissingModPeerRole.ExistingTeammate;
        }

        public bool HasUnacknowledgedMissingModPeers =>
            MissingModPeers.Keys.Any(id => !AcknowledgedMissingModIds.Contains(id));

        public List<MissingModPeerInfo> GetSortedMissingModPeers() =>
            MissingModPeers.Values
                .OrderBy(p => p.Role)
                .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    internal static bool TryGetExpandedSession(SerializableRun run, out ExpandedSession session) =>
        Expanded.TryGetValue(run, out session!);
    private readonly record struct QueuedGuest(ulong GuestId, CharacterModel? Character, SerializablePlayer? RestoredPlayer = null);

    internal const string FloatingButtonName = "MidRunJoinOverlayButton";
    internal const string RosterModalName = "MidRunJoinRosterModal";

    private static readonly ConditionalWeakTable<SerializableRun, ExpandedSession> Expanded = new();

    [HarmonyPostfix, HarmonyPatch(typeof(NMultiplayerSubmenu), nameof(NMultiplayerSubmenu._Ready))]
    private static void AttachFloatingJoinButton(NMultiplayerSubmenu __instance, NSubmenuButton? ____loadButton)
    {
        if (____loadButton == null || ____loadButton.GetNodeOrNull<Button>(FloatingButtonName) != null)
            return;

        var btn = new Button
        {
            Name = FloatingButtonName,
            Text = ModLoc.FloatingButtonText,
            TooltipText = ModLoc.FloatingButtonTooltip,
            AnchorLeft = 1f,
            AnchorRight = 1f,
            AnchorTop = 0f,
            AnchorBottom = 0f,
            OffsetLeft = -186,
            OffsetRight = -16,
            OffsetTop = 16,
            OffsetBottom = 58,
            ZIndex = 10,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None
        };
        StyleFloatingButton(btn);
        btn.Pressed += () => OnFloatingJoinPressed(__instance);
        ____loadButton.AddChild(btn);

        // Keep the floating button text & font synced if the player switches language in Settings.
        void OnLocaleChanged()
        {
            if (GodotObject.IsInstanceValid(btn))
            {
                btn.Text = ModLoc.FloatingButtonText;
                btn.TooltipText = ModLoc.FloatingButtonTooltip;
                StyleFloatingButton(btn);
            }
        }
        LocManager.Instance?.SubscribeToLocaleChange(OnLocaleChanged);
        btn.TreeExiting += () => LocManager.Instance?.UnsubscribeToLocaleChange(OnLocaleChanged);
    }

    private static void StyleFloatingButton(Button btn)
    {
        VanillaUi.StyleButton(
            btn,
            normalBg: new Color(0.106f, 0.145f, 0.200f, 0.94f),
            hoverBg: new Color(0.165f, 0.235f, 0.325f, 0.98f),
            pressedBg: new Color(0.075f, 0.102f, 0.145f, 0.98f),
            normalBorder: VanillaUi.GoldBorder,
            hoverBorder: StsColors.gold,
            fontSize: 18,
            fontType: FontType.Bold,
            radius: 8,
            padH: 12,
            padV: 4,
            outlineSize: 3);
    }

    private static void OnFloatingJoinPressed(NMultiplayerSubmenu menu)
    {
        var platformType = SteamInitializer.Initialized && !CommandLineHelper.HasArg("fastmp")
            ? PlatformType.Steam
            : PlatformType.None;
        var readResult = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(PlatformUtil.GetLocalPlayerId(platformType));
        if (!readResult.Success || readResult.SaveData == null)
        {
            Log.Warn($"[MidRunJoin] Failed to load multiplayer save from floating button (status={readResult.Status}).");
            var popup = Compat.CreateSaveErrorPopup(readResult.Status);
            if (popup != null && NModalContainer.Instance != null)
            {
                NModalContainer.Instance.Add(popup);
                NModalContainer.Instance.ShowBackstop();
            }
            return;
        }
        OpenGuestDialog(menu, readResult.SaveData);
    }

    internal static void OpenGuestDialog(NMultiplayerSubmenu __instance, SerializableRun run)
    {
        // Prevent duplicate modals if triggered twice
        if (__instance.GetNodeOrNull<Control>(RosterModalName) is { } existingModal)
        {
            __instance.RemoveChild(existingModal);
            existingModal.QueueFree();
        }

        var queued = new List<QueuedGuest>();
        var queuedNames = new Dictionary<ulong, string>();
        var removedIds = new HashSet<ulong>();
        var persistedStashes = PlayerStash.LoadForRun(run);
        var stagedStashes = new Dictionary<ulong, StashedPlayerEntry>();
        ulong? pendingRemoveConfirmId = null;
        var characters = ModelDb.AllCharacters.ToArray();
        int randomCharIndex = characters.Length;
        var flows = CatchUpRegistry.All;
        ulong hostId = run.Players.Count > 0 ? run.Players[0].NetId : 0;
        bool isSteamMode = SteamHelper.IsSteamReady && !CommandLineHelper.HasArg("fastmp");

        StashedPlayerEntry? TryGetValidStash(ulong playerId)
        {
            if (stagedStashes.TryGetValue(playerId, out var staged))
                return staged;
            if (persistedStashes.TryGetValue(playerId, out var persisted))
                return persisted;
            return null;
        }

        CharacterModel? ResolveStashedCharacter(StashedPlayerEntry stash)
        {
            if (string.IsNullOrWhiteSpace(stash.CharacterId))
                return null;
            try
            {
                return ResolveCharacter(ModelId.Deserialize(stash.CharacterId), characters);
            }
            catch
            {
                return characters.FirstOrDefault(c =>
                    stash.CharacterId.EndsWith("." + c.Id.Entry, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stash.CharacterId, c.Id.Entry, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Full-screen vanilla modal overlay with dark backstop
        var modalRoot = new Control
        {
            Name = RosterModalName,
            ZIndex = 100,
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

        int existingHeight = Math.Clamp(run.Players.Count * 46 + 8, 92, 196);
        int extraExisting = Math.Max(0, existingHeight - 92);
        int targetHeight = Math.Min((isSteamMode ? 720 : 530) + extraExisting, 810);
        int targetWidth = 720;

        var panel = new PanelContainer
        {
            Name = "RosterPanel",
            CustomMinimumSize = new Vector2(targetWidth, targetHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        panel.AddThemeStyleboxOverride("panel", VanillaUi.MakeBox(
            VanillaUi.PanelBg,
            VanillaUi.GoldBorder,
            borderWidth: 2,
            radius: 12,
            padH: 22,
            padV: 18,
            shadowSize: 22));
        center.AddChild(panel);

        var fields = new VBoxContainer
        {
            Name = "ContentVBox",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        fields.AddThemeConstantOverride("separation", 10);
        panel.AddChild(fields);

        // --- Header row: Gold title + live party count badge ---
        var headerRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        headerRow.AddThemeConstantOverride("separation", 10);
        var titleLabel = new Label
        {
            Text = ModLoc.DialogTitle,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center
        };
        VanillaUi.ApplyFont(titleLabel, FontType.Bold, 26, StsColors.gold, outlineSize: 4);
        headerRow.AddChild(titleLabel);

        if (!isSteamMode)
        {
            headerRow.AddChild(VanillaUi.CreateBadgePill(
                ModLoc.LocalModeBadge,
                new Color(0.12f, 0.22f, 0.28f, 0.95f),
                StsColors.blue,
                fontSize: 13));
        }

        var partyCountBadge = VanillaUi.CreateBadgePill(
            ModLoc.PartyCountBadge(run.Players.Count),
            new Color(0.18f, 0.24f, 0.30f, 0.95f),
            StsColors.gold,
            fontSize: 14);
        var partyCountLabel = partyCountBadge.GetChild<Label>(0);
        headerRow.AddChild(partyCountBadge);
        fields.AddChild(headerRow);
        fields.AddChild(VanillaUi.CreateGoldDivider());

        // --- Status / summary banner ---
        var message = new Label
        {
            Name = "StatusLabel",
            Text = ModLoc.DialogIntro(isSteamMode),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        VanillaUi.ApplyFont(message, FontType.Regular, 14, VanillaUi.MutedText);
        fields.AddChild(message);

        void SetStatusMessage(string text, bool isError = false)
        {
            message.Text = text;
            message.AddThemeColorOverride("font_color", isError ? StsColors.red : StsColors.cream);
        }

        // --- Section 1: Existing party members ---
        var existingCard = VanillaUi.CreateSectionCard(out var existingSection);
        var existingHeader = new Label { Text = ModLoc.SectionCurrentParty };
        VanillaUi.ApplyFont(existingHeader, FontType.Bold, 15, StsColors.gold, outlineSize: 2);
        existingSection.AddChild(existingHeader);

        var existingScroll = new ScrollContainer
        {
            Name = "ExistingMembersScroll",
            CustomMinimumSize = new Vector2(0, existingHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var existingBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        existingBox.AddThemeConstantOverride("separation", 6);
        existingScroll.AddChild(existingBox);
        existingSection.AddChild(existingScroll);
        fields.AddChild(existingCard);

        // Friend selection state (Steam mode)
        LineEdit? friendSearch = null;
        LineEdit? manualSteamIdInput = null;
        CheckBox? onlineOnlyCheck = null;
        Label? friendCountLabel = null;
        VBoxContainer? friendListBox = null;
        Label? selectedFriendLabel = null;
        List<SteamFriendEntry> allFriends = new();
        List<SteamFriendEntry> visibleFriends = new();
        SteamFriendEntry? selectedFriend = null;
        ulong lastFriendClickId = 0;
        ulong lastFriendClickMs = 0;

        Action? tryAddSelectedOrInput = null;
        Action<ulong?>? syncPickerForCandidateId = null;

        void UpdateSelectedFriendLabel()
        {
            if (selectedFriendLabel == null)
                return;
            if (selectedFriend.HasValue)
            {
                var stash = TryGetValidStash(selectedFriend.Value.SteamId);
                var stashedChar = stash != null ? ResolveStashedCharacter(stash) : null;
                if (stashedChar != null)
                {
                    selectedFriendLabel.Text = ModLoc.RestoredCharacterLockedHint(selectedFriend.Value.Name, CharacterName(stashedChar));
                    selectedFriendLabel.AddThemeColorOverride("font_color", StsColors.gold);
                }
                else
                {
                    selectedFriendLabel.Text = ModLoc.SelectedFriendLabel(selectedFriend.Value.Name);
                    selectedFriendLabel.AddThemeColorOverride("font_color", StsColors.aqua);
                }
            }
            else
            {
                selectedFriendLabel.Text = ModLoc.SelectFriendHint;
                selectedFriendLabel.AddThemeColorOverride("font_color", VanillaUi.MutedText);
            }
        }

        void UpdateSummaryMessage()
        {
            int kept = run.Players.Count - removedIds.Count;
            int finalCount = kept + queued.Count;
            partyCountLabel.Text = ModLoc.PartyCountBadge(finalCount);
            SetStatusMessage(ModLoc.RosterSummary(kept, removedIds.Count, queued.Count, finalCount), isError: false);
        }

        void PopulateFriendList()
        {
            if (friendListBox == null)
                return;

            foreach (Node child in friendListBox.GetChildren())
            {
                friendListBox.RemoveChild(child);
                child.QueueFree();
            }

            var stashedIds = new HashSet<ulong>(persistedStashes.Keys);
            foreach (ulong sid in stagedStashes.Keys)
                stashedIds.Add(sid);

            visibleFriends = SteamHelper.FilterFriends(
                allFriends,
                friendSearch?.Text,
                onlineOnlyCheck?.ButtonPressed ?? false,
                stashedIds);

            if (selectedFriend.HasValue && !visibleFriends.Any(f => f.SteamId == selectedFriend.Value.SteamId))
            {
                selectedFriend = null;
                UpdateSelectedFriendLabel();
            }

            if (friendCountLabel != null)
                friendCountLabel.Text = $"{visibleFriends.Count}/{allFriends.Count}";

            if (visibleFriends.Count == 0)
            {
                var emptyLbl = new Label
                {
                    Text = ModLoc.NoFriendsMatch,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    CustomMinimumSize = new Vector2(0, 44),
                    VerticalAlignment = VerticalAlignment.Center
                };
                VanillaUi.ApplyFont(emptyLbl, FontType.Regular, 14, VanillaUi.MutedText);
                friendListBox.AddChild(emptyLbl);
                return;
            }

            foreach (var friend in visibleFriends)
            {
                var entry = friend;
                bool isSelected = selectedFriend.HasValue && selectedFriend.Value.SteamId == entry.SteamId;

                var rowBtn = new Button
                {
                    CustomMinimumSize = new Vector2(0, 36),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    FocusMode = Control.FocusModeEnum.None
                };
                VanillaUi.StyleFriendRowButton(rowBtn, isSelected);

                var rowContent = new HBoxContainer
                {
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                rowContent.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                rowContent.AddThemeConstantOverride("separation", 8);
                rowContent.OffsetLeft = 10;
                rowContent.OffsetRight = -10;
                rowBtn.AddChild(rowContent);

                bool hasStash = TryGetValidStash(entry.SteamId) != null;
                if (hasStash)
                {
                    rowContent.AddChild(VanillaUi.CreateSaveIcon(18));
                }

                var nameLbl = new Label
                {
                    Text = entry.Name,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    VerticalAlignment = VerticalAlignment.Center,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
                };
                VanillaUi.ApplyFont(nameLbl, FontType.Regular, 15, isSelected || hasStash ? StsColors.gold : StsColors.cream);
                rowContent.AddChild(nameLbl);

                Color statusColor = entry.IsPlayingSts2
                    ? StsColors.aqua
                    : (entry.IsOnline ? StsColors.blue : VanillaUi.MutedText);
                Color statusBg = entry.IsPlayingSts2
                    ? new Color(0.08f, 0.25f, 0.22f, 0.9f)
                    : (entry.IsOnline ? new Color(0.10f, 0.20f, 0.28f, 0.9f) : new Color(0.16f, 0.18f, 0.21f, 0.85f));
                if (hasStash)
                {
                    rowContent.AddChild(VanillaUi.CreateBadgePill(
                        ModLoc.SavedProgressBadge,
                        new Color(0.28f, 0.22f, 0.08f, 0.95f),
                        StsColors.gold,
                        12));
                }
                if (!string.Equals(entry.StatusTag, ModLoc.SavedProgressBadge, StringComparison.Ordinal))
                    rowContent.AddChild(VanillaUi.CreateBadgePill(entry.StatusTag, statusBg, statusColor, 12));

                rowBtn.Pressed += () =>
                {
                    ulong now = Time.GetTicksMsec();
                    bool isDoubleClick = lastFriendClickId == entry.SteamId && now - lastFriendClickMs <= 420;
                    lastFriendClickId = entry.SteamId;
                    lastFriendClickMs = now;

                    selectedFriend = entry;
                    if (manualSteamIdInput != null && !string.IsNullOrEmpty(manualSteamIdInput.Text))
                        manualSteamIdInput.Text = "";
                    syncPickerForCandidateId?.Invoke(entry.SteamId);
                    UpdateSelectedFriendLabel();
                    if (isDoubleClick)
                    {
                        lastFriendClickId = 0;
                        tryAddSelectedOrInput?.Invoke();
                    }
                    else
                    {
                        PopulateFriendList();
                    }
                };

                friendListBox.AddChild(rowBtn);
            }
        }

        void RefreshFriendPicker()
        {
            if (friendListBox == null)
                return;
            var activeIds = new HashSet<ulong>(
                run.Players.Where(p => !removedIds.Contains(p.NetId)).Select(p => p.NetId)
                    .Concat(queued.Select(q => q.GuestId)));
            allFriends = SteamHelper.GetCandidateFriends(activeIds);

            // Ensure any stashed player (even if not in the host's Steam friends list) is immediately selectable.
            var existingFriendIds = new HashSet<ulong>(allFriends.Select(f => f.SteamId));
            foreach (ulong stashedId in stagedStashes.Keys.Concat(persistedStashes.Keys).Distinct())
            {
                if (stashedId == 0 || activeIds.Contains(stashedId) || !existingFriendIds.Add(stashedId))
                    continue;
                string displayName = SteamHelper.FormatPlayerIdentity(stashedId, SteamHelper.TryGetPlayerName(stashedId));
                allFriends.Add(new SteamFriendEntry(
                    stashedId,
                    displayName,
                    $"{displayName} · {ModLoc.SavedProgressBadge}",
                    ModLoc.SavedProgressBadge,
                    IsPlayingSts2: false,
                    IsOnline: true));
            }

            PopulateFriendList();
        }

        void RebuildExistingUi()
        {
            int savedScroll = existingScroll.ScrollVertical;
            foreach (Node child in existingBox.GetChildren())
            {
                existingBox.RemoveChild(child);
                child.QueueFree();
            }
            for (int i = 0; i < run.Players.Count; i++)
            {
                var p = run.Players[i];
                ulong pid = p.NetId;
                bool isHost = pid == hostId;
                if (removedIds.Contains(pid))
                    continue;

                bool isConfirmingRemove = pendingRemoveConfirmId == pid;
                var charModel = ResolveCharacter(p.CharacterId, characters);
                string charName = charModel != null ? CharacterName(charModel) : (p.CharacterId?.Entry ?? "?");
                string displayName = SteamHelper.FormatPlayerIdentity(pid, SteamHelper.TryGetPlayerName(pid), i + 1);

                var rowCard = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                rowCard.AddThemeStyleboxOverride("panel", VanillaUi.MakeBox(
                    isConfirmingRemove ? new Color(0.20f, 0.14f, 0.15f, 0.92f) : VanillaUi.RowBg,
                    isConfirmingRemove ? StsColors.gold : (isHost ? VanillaUi.GoldBorder : VanillaUi.SubtleBorder),
                    borderWidth: 1,
                    radius: 6,
                    padH: 10,
                    padV: 5));

                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 8);
                rowCard.AddChild(row);

                row.AddChild(VanillaUi.CreateCharacterIcon(charModel, 28));

                var infoBox = new HBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
                };
                infoBox.AddThemeConstantOverride("separation", 8);

                var nameLbl = new Label
                {
                    Text = displayName,
                    VerticalAlignment = VerticalAlignment.Center
                };
                VanillaUi.ApplyFont(nameLbl, FontType.Bold, 15, StsColors.cream, outlineSize: 1);
                infoBox.AddChild(nameLbl);

                var charLbl = new Label
                {
                    Text = $"·  {charName}",
                    VerticalAlignment = VerticalAlignment.Center
                };
                VanillaUi.ApplyFont(charLbl, FontType.Regular, 14, charModel?.NameColor ?? StsColors.cream);
                infoBox.AddChild(charLbl);
                row.AddChild(infoBox);

                if (isHost)
                {
                    row.AddChild(VanillaUi.CreateBadgePill(
                        ModLoc.HostBadge,
                        new Color(0.28f, 0.22f, 0.08f, 0.95f),
                        StsColors.gold,
                        fontSize: 12));
                }
                else if (isConfirmingRemove)
                {
                    // Secondary 3-button confirmation in the exact position of the Remove button:
                    // Delete / Save Progress / Cancel
                    var deleteBtn = new Button
                    {
                        Name = $"ConfirmDelete_{pid}",
                        Text = ModLoc.ConfirmDeleteButton,
                        FocusMode = Control.FocusModeEnum.None
                    };
                    VanillaUi.StyleDangerSmallButton(deleteBtn, 13);
                    deleteBtn.Pressed += () =>
                    {
                        pendingRemoveConfirmId = null;
                        removedIds.Add(pid);
                        RebuildExistingUi();
                        RefreshFriendPicker();
                        UpdateSummaryMessage();
                    };

                    var stashBtn = new Button
                    {
                        Name = $"ConfirmStash_{pid}",
                        Text = ModLoc.ConfirmStashButton,
                        FocusMode = Control.FocusModeEnum.None
                    };
                    VanillaUi.StyleSecondaryButton(stashBtn, 13);
                    stashBtn.Pressed += () =>
                    {
                        pendingRemoveConfirmId = null;
                        stagedStashes[pid] = PlayerStash.CreateEntry(run, p);
                        removedIds.Add(pid);
                        RebuildExistingUi();
                        RefreshFriendPicker();
                        UpdateSummaryMessage();
                    };

                    var cancelRemoveBtn = new Button
                    {
                        Name = $"CancelRemove_{pid}",
                        Text = ModLoc.CancelRemoveButton,
                        FocusMode = Control.FocusModeEnum.None
                    };
                    VanillaUi.StyleButton(
                        cancelRemoveBtn,
                        VanillaUi.RowBg,
                        VanillaUi.RowHoverBg,
                        VanillaUi.InputBg,
                        VanillaUi.SubtleBorder,
                        StsColors.cream,
                        fontSize: 13,
                        fontType: FontType.Regular,
                        radius: 6,
                        padH: 10,
                        padV: 4,
                        outlineSize: 1);
                    cancelRemoveBtn.Pressed += () =>
                    {
                        pendingRemoveConfirmId = null;
                        RebuildExistingUi();
                    };

                    row.AddChild(deleteBtn);
                    row.AddChild(stashBtn);
                    row.AddChild(cancelRemoveBtn);
                }
                else
                {
                    var removeBtn = new Button
                    {
                        Name = $"RemoveExisting_{pid}",
                        Text = ModLoc.RemoveButton,
                        FocusMode = Control.FocusModeEnum.None
                    };
                    VanillaUi.StyleDangerSmallButton(removeBtn, 13);
                    removeBtn.Pressed += () =>
                    {
                        pendingRemoveConfirmId = pid;
                        RebuildExistingUi();
                    };
                    row.AddChild(removeBtn);
                }

                existingBox.AddChild(rowCard);
            }
            existingScroll.ScrollVertical = savedScroll;
        }
        RebuildExistingUi();

        // --- Section 2: Add New Members ---
        var addCard = VanillaUi.CreateSectionCard(out var addSection);
        var addHeader = new Label { Text = ModLoc.SectionAddGuests };
        VanillaUi.ApplyFont(addHeader, FontType.Bold, 15, StsColors.gold, outlineSize: 2);
        addSection.AddChild(addHeader);

        if (isSteamMode)
        {
            var searchRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            searchRow.AddThemeConstantOverride("separation", 8);

            friendSearch = new LineEdit
            {
                Name = "FriendSearchInput",
                PlaceholderText = ModLoc.FriendSearchPlaceholder,
                ClearButtonEnabled = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            VanillaUi.StyleLineEdit(friendSearch, 14);

            onlineOnlyCheck = new CheckBox
            {
                Text = ModLoc.OnlineOnlyCheckbox,
                ButtonPressed = true,
                FocusMode = Control.FocusModeEnum.None
            };
            VanillaUi.StyleCheckBox(onlineOnlyCheck, 14);

            friendCountLabel = new Label
            {
                Text = "0/0",
                VerticalAlignment = VerticalAlignment.Center
            };
            VanillaUi.ApplyFont(friendCountLabel, FontType.Regular, 13, VanillaUi.MutedText);

            searchRow.AddChild(friendSearch);
            searchRow.AddChild(onlineOnlyCheck);
            searchRow.AddChild(friendCountLabel);
            addSection.AddChild(searchRow);

            var friendScroll = new ScrollContainer
            {
                Name = "SteamFriendsScroll",
                CustomMinimumSize = new Vector2(0, 126),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            friendListBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            friendListBox.AddThemeConstantOverride("separation", 4);
            friendScroll.AddChild(friendListBox);
            addSection.AddChild(friendScroll);

            friendSearch.TextChanged += _ => PopulateFriendList();
            onlineOnlyCheck.Toggled += _ => PopulateFriendList();

            // Optional manual SteamID input for non-friends
            manualSteamIdInput = new LineEdit
            {
                Name = "ManualSteamIdInput",
                PlaceholderText = ModLoc.ManualSteamIdPlaceholder,
                ClearButtonEnabled = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            VanillaUi.StyleLineEdit(manualSteamIdInput, 14);
            manualSteamIdInput.TextChanged += text =>
            {
                if (!string.IsNullOrWhiteSpace(text) && selectedFriend.HasValue)
                {
                    selectedFriend = null;
                    UpdateSelectedFriendLabel();
                    PopulateFriendList();
                }
                syncPickerForCandidateId?.Invoke(TryParseGuestId(text, out ulong parsed) ? parsed : null);
            };
            addSection.AddChild(manualSteamIdInput);
        }

        // Character selector + Add action row
        var inputRow = new HBoxContainer
        {
            Name = "GuestInputRow",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        inputRow.AddThemeConstantOverride("separation", 8);

        LineEdit? input = null;
        if (isSteamMode)
        {
            selectedFriendLabel = new Label
            {
                Name = "SelectedFriendLabel",
                Text = ModLoc.SelectFriendHint,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            VanillaUi.ApplyFont(selectedFriendLabel, FontType.Regular, 14, VanillaUi.MutedText);
            inputRow.AddChild(selectedFriendLabel);
        }
        else
        {
            input = new LineEdit
            {
                Name = "GuestIdInput",
                PlaceholderText = ModLoc.GuestIdPlaceholder,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            VanillaUi.StyleLineEdit(input, 14);
            input.TextChanged += text =>
            {
                syncPickerForCandidateId?.Invoke(TryParseGuestId(text, out ulong parsed) ? parsed : null);
            };
            inputRow.AddChild(input);
        }

        var pickerCharIcon = VanillaUi.CreateCharacterIcon(characters.Length > 0 ? characters[0] : null, 28);
        inputRow.AddChild(pickerCharIcon);

        var picker = new OptionButton
        {
            Name = "CharacterPicker",
            CustomMinimumSize = new Vector2(150, 34),
            FocusMode = Control.FocusModeEnum.None
        };
        foreach (var c in characters)
            picker.AddItem(CharacterName(c));
        picker.AddItem(ModLoc.RandomCharacterOption);
        picker.Selected = 0;
        VanillaUi.StyleOptionButton(picker, 14);
        picker.ItemSelected += idx =>
        {
            if (idx == randomCharIndex)
                VanillaUi.SetRandomCharacterIconTexture(pickerCharIcon);
            else if (idx >= 0 && idx < characters.Length)
                VanillaUi.SetCharacterIconTexture(pickerCharIcon, characters[(int)idx]);
        };
        syncPickerForCandidateId = candidateId =>
        {
            var stash = candidateId.HasValue ? TryGetValidStash(candidateId.Value) : null;
            var stashedChar = stash != null ? ResolveStashedCharacter(stash) : null;
            if (stashedChar != null)
            {
                int idx = Array.FindIndex(characters, c => c.Id == stashedChar.Id);
                if (idx >= 0)
                    picker.Selected = idx;
                VanillaUi.SetCharacterIconTexture(pickerCharIcon, stashedChar);
                picker.Disabled = true;
            }
            else
            {
                picker.Disabled = false;
            }
        };
        inputRow.AddChild(picker);

        var addButton = new Button
        {
            Name = "AddGuestButton",
            Text = ModLoc.AddGuestButton,
            CustomMinimumSize = new Vector2(92, 34),
            FocusMode = Control.FocusModeEnum.None
        };
        VanillaUi.StyleSecondaryButton(addButton, 14);
        inputRow.AddChild(addButton);
        addSection.AddChild(inputRow);

        // Queued guests list (inside Add New Members section, collapses neatly when empty)
        var queueHeader = new Label
        {
            Text = ModLoc.SectionQueuedGuests,
            Visible = false
        };
        VanillaUi.ApplyFont(queueHeader, FontType.Bold, 14, StsColors.aqua);
        addSection.AddChild(queueHeader);

        var queueScroll = new ScrollContainer
        {
            Name = "QueuedGuestsScroll",
            CustomMinimumSize = new Vector2(0, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Visible = false
        };
        var queueBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        queueBox.AddThemeConstantOverride("separation", 5);
        queueScroll.AddChild(queueBox);
        addSection.AddChild(queueScroll);
        fields.AddChild(addCard);

        void RebuildQueueUi()
        {
            foreach (Node child in queueBox.GetChildren())
            {
                queueBox.RemoveChild(child);
                child.QueueFree();
            }

            bool hasQueued = queued.Count > 0;
            queueHeader.Visible = hasQueued;
            queueScroll.Visible = hasQueued;
            queueScroll.CustomMinimumSize = hasQueued ? new Vector2(0, Math.Clamp(queued.Count * 42 + 4, 46, 110)) : Vector2.Zero;

            for (int i = 0; i < queued.Count; i++)
            {
                int idx = i;
                var g = queued[i];
                string displayName = queuedNames.TryGetValue(g.GuestId, out var knownName)
                    ? knownName
                    : SteamHelper.FormatPlayerIdentity(g.GuestId, SteamHelper.TryGetPlayerName(g.GuestId));

                var rowCard = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                rowCard.AddThemeStyleboxOverride("panel", VanillaUi.MakeBox(
                    new Color(0.11f, 0.21f, 0.20f, 0.90f),
                    new Color(0.17f, 0.65f, 0.55f, 0.75f),
                    borderWidth: 1,
                    radius: 6,
                    padH: 10,
                    padV: 4));

                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 8);
                rowCard.AddChild(row);

                var rowIcon = VanillaUi.CreateCharacterIcon(g.Character, 24);
                if (g.Character == null)
                    VanillaUi.SetRandomCharacterIconTexture(rowIcon);
                row.AddChild(rowIcon);

                string charLabel = g.Character != null ? CharacterName(g.Character) : ModLoc.RandomCharacterOption;
                var nameLbl = new Label
                {
                    Text = $"{displayName}  ·  {charLabel}",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    VerticalAlignment = VerticalAlignment.Center
                };
                VanillaUi.ApplyFont(nameLbl, FontType.Regular, 14, StsColors.cream);
                row.AddChild(nameLbl);

                bool isRestored = g.RestoredPlayer != null;
                row.AddChild(VanillaUi.CreateBadgePill(
                    isRestored ? ModLoc.RestoredGuestBadge : ModLoc.NewGuestBadge,
                    isRestored ? new Color(0.28f, 0.22f, 0.08f, 0.95f) : new Color(0.08f, 0.26f, 0.22f, 0.95f),
                    isRestored ? StsColors.gold : StsColors.aqua,
                    fontSize: 12));

                var rm = new Button
                {
                    Text = ModLoc.RemoveButton,
                    FocusMode = Control.FocusModeEnum.None
                };
                VanillaUi.StyleDangerSmallButton(rm, 13);
                rm.Pressed += () =>
                {
                    queued.RemoveAt(idx);
                    RebuildQueueUi();
                    RefreshFriendPicker();
                    UpdateSummaryMessage();
                };
                row.AddChild(rm);
                queueBox.AddChild(rowCard);
            }
        }

        bool TryAddCurrentSelection()
        {
            ulong id;
            string displayName;
            if (isSteamMode)
            {
                if (manualSteamIdInput != null && !string.IsNullOrWhiteSpace(manualSteamIdInput.Text))
                {
                    if (!TryParseGuestId(manualSteamIdInput.Text, out id))
                    {
                        SetStatusMessage(ModLoc.ErrInvalidPlayerId, isError: true);
                        return false;
                    }
                    displayName = SteamHelper.FormatPlayerIdentity(id, SteamHelper.TryGetPlayerName(id));
                }
                else if (selectedFriend.HasValue)
                {
                    id = selectedFriend.Value.SteamId;
                    displayName = selectedFriend.Value.Name;
                }
                else
                {
                    SetStatusMessage(ModLoc.ErrSelectFriendFirst, isError: true);
                    return false;
                }
            }
            else
            {
                if (input == null || !TryParseGuestId(input.Text, out id))
                {
                    SetStatusMessage(ModLoc.ErrInvalidPlayerId, isError: true);
                    return false;
                }
                displayName = SteamHelper.FormatPlayerIdentity(id, SteamHelper.TryGetPlayerName(id));
            }

            bool stillInParty = run.Players.Any(p => p.NetId == id && !removedIds.Contains(id));
            if (stillInParty || queued.Any(q => q.GuestId == id))
            {
                SetStatusMessage(ModLoc.ErrDuplicatePlayer(displayName), isError: true);
                return false;
            }

            var stash = TryGetValidStash(id);
            SerializablePlayer? restoredPlayer = stash != null ? PlayerStash.DeserializePlayer(stash) : null;
            CharacterModel? chosenChar;
            if (restoredPlayer != null)
            {
                chosenChar = ResolveCharacter(restoredPlayer.CharacterId, characters)
                    ?? characters[Math.Clamp(picker.Selected, 0, characters.Length - 1)];
            }
            else
            {
                chosenChar = picker.Selected == randomCharIndex
                    ? null
                    : characters[Math.Clamp(picker.Selected, 0, characters.Length - 1)];
            }

            queued.Add(new QueuedGuest(id, chosenChar, restoredPlayer));
            queuedNames[id] = displayName;
            if (isSteamMode)
            {
                selectedFriend = null;
                if (manualSteamIdInput != null)
                    manualSteamIdInput.Text = "";
                UpdateSelectedFriendLabel();
            }
            else if (input != null)
            {
                input.Text = "";
            }
            picker.Disabled = false;
            RebuildQueueUi();
            RefreshFriendPicker();
            UpdateSummaryMessage();
            return true;
        }

        tryAddSelectedOrInput = () => TryAddCurrentSelection();
        addButton.Pressed += () => TryAddCurrentSelection();

        if (isSteamMode)
        {
            RefreshFriendPicker();
            if (visibleFriends.Count == 0 && allFriends.Count > 0 && onlineOnlyCheck != null)
            {
                onlineOnlyCheck.ButtonPressed = false;
                PopulateFriendList();
            }
        }

        // --- Section 3: Catch-up rewards ---
        var flowCard = VanillaUi.CreateSectionCard(out var flowSection, padH: 12, padV: 8);
        var flowRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        flowRow.AddThemeConstantOverride("separation", 10);

        var flowLbl = new Label
        {
            Text = ModLoc.SectionCatchUp,
            VerticalAlignment = VerticalAlignment.Center
        };
        VanillaUi.ApplyFont(flowLbl, FontType.Bold, 15, StsColors.gold, outlineSize: 2);
        flowRow.AddChild(flowLbl);

        var flowPicker = new OptionButton
        {
            Name = "CatchUpFlowPicker",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.None
        };
        foreach (var f in flows)
            flowPicker.AddItem(f.DisplayName);
        flowPicker.Selected = 0;
        VanillaUi.StyleOptionButton(flowPicker, 14);
        flowRow.AddChild(flowPicker);
        flowSection.AddChild(flowRow);

        var flowDesc = new Label
        {
            Text = flows[0].Description,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        VanillaUi.ApplyFont(flowDesc, FontType.Regular, 13, VanillaUi.MutedText);
        flowPicker.ItemSelected += idx =>
        {
            if (idx >= 0 && idx < flows.Count)
                flowDesc.Text = flows[(int)idx].Description;
        };
        flowSection.AddChild(flowDesc);
        fields.AddChild(flowCard);

        // --- Footer buttons ---
        var footerRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        footerRow.AddThemeConstantOverride("separation", 14);
        footerRow.Alignment = BoxContainer.AlignmentMode.End;

        var cancelBtn = new Button
        {
            Name = "CancelButton",
            Text = ModLoc.DialogCancelButton,
            CustomMinimumSize = new Vector2(110, 42),
            FocusMode = Control.FocusModeEnum.None
        };
        VanillaUi.StyleCancelButton(cancelBtn, 16);
        var escShortcut = new Shortcut();
        escShortcut.Events.Add(new InputEventKey { Keycode = Key.Escape });
        cancelBtn.Shortcut = escShortcut;
        cancelBtn.ShortcutInTooltip = false;

        var okBtn = new Button
        {
            Name = "ConfirmButton",
            Text = ModLoc.DialogOkButton,
            CustomMinimumSize = new Vector2(180, 42),
            FocusMode = Control.FocusModeEnum.None
        };
        VanillaUi.StylePrimaryButton(okBtn, 16);

        footerRow.AddChild(cancelBtn);
        footerRow.AddChild(okBtn);
        fields.AddChild(footerRow);

        void CloseModal()
        {
            if (GodotObject.IsInstanceValid(modalRoot))
            {
                modalRoot.Visible = false;
                if (modalRoot.GetParent() is { } parent)
                    parent.RemoveChild(modalRoot);
                modalRoot.QueueFree();
            }
        }

        cancelBtn.Pressed += CloseModal;

        okBtn.Pressed += () =>
        {
            if (isSteamMode)
            {
                bool hasManualId = manualSteamIdInput != null && !string.IsNullOrWhiteSpace(manualSteamIdInput.Text);
                if ((selectedFriend.HasValue || hasManualId) && !TryAddCurrentSelection())
                    return;
            }
            else if (input != null && !string.IsNullOrWhiteSpace(input.Text))
            {
                if (!TryAddCurrentSelection())
                    return;
            }

            if (queued.Count == 0 && removedIds.Count == 0)
            {
                SetStatusMessage(ModLoc.ErrNoChangesSelected, isError: true);
                return;
            }
            try
            {
                if (stagedStashes.Count > 0)
                    PlayerStash.SaveForRun(run, stagedStashes.Values);

                string flowId = queued.Count > 0 ? flows[Math.Max(0, flowPicker.Selected)].Id : NoneCatchUpFlow.FlowId;
                var resolvedGuests = queued
                    .Select(g => new GuestSpec(
                        g.GuestId,
                        g.Character ?? characters[Random.Shared.Next(characters.Length)],
                        g.RestoredPlayer))
                    .ToArray();
                StartExpandedLobby(__instance, run, resolvedGuests, flowId, removedIds.ToArray());
                CloseModal();
            }
            catch (Exception ex)
            {
                Log.Warn($"[MidRunJoin] Candidate preparation failed: {ex}");
                SetStatusMessage(ex is InvalidOperationException ? ex.Message : ModLoc.ErrGenericPrepareFailed, isError: true);
            }
        };

        __instance.AddChild(modalRoot);

        // Smooth vanilla-like fade-in animation
        modalRoot.Modulate = new Color(1f, 1f, 1f, 0f);
        var fade = modalRoot.CreateTween();
        fade.TweenProperty(modalRoot, "modulate:a", 1f, 0.16);

        if (friendSearch != null)
            friendSearch.GrabFocus();
        else
            input?.GrabFocus();
    }

    internal static bool TryParseGuestId(string? raw, out ulong id)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            id = 0;
            return false;
        }
        return ulong.TryParse(raw.Trim(), out id) && id != 0;
    }

    private static CharacterModel? ResolveCharacter(ModelId? characterId, CharacterModel[] characters)
    {
        if (characterId == null)
            return null;
        try
        {
            var direct = ModelDb.GetByIdOrNull<CharacterModel>(characterId);
            if (direct != null)
                return direct;
        }
        catch
        {
            // Fall through to linear match.
        }
        return characters.FirstOrDefault(c =>
            c.Id == characterId ||
            string.Equals(c.Id.Entry, characterId.Entry, StringComparison.OrdinalIgnoreCase));
    }

    private static string CharacterName(CharacterModel c)
    {
        try { return c.Title.GetFormattedText(); }
        catch { return c.Id.Entry; }
    }

    internal static void StartExpandedLobby(NMultiplayerSubmenu menu, SerializableRun checkpoint, ulong id) =>
        StartExpandedLobby(menu, checkpoint, [new GuestSpec(id, ModelDb.Character<MegaCrit.Sts2.Core.Models.Characters.Ironclad>())], CatchUpRegistry.Default.Id, null);

    internal static void StartExpandedLobby(NMultiplayerSubmenu menu, SerializableRun checkpoint, ulong id, CharacterModel character) =>
        StartExpandedLobby(menu, checkpoint, [new GuestSpec(id, character)], CatchUpRegistry.Default.Id, null);

    internal static void StartExpandedLobby(
        NMultiplayerSubmenu menu,
        SerializableRun checkpoint,
        IReadOnlyList<GuestSpec> guests,
        string? flowId = null,
        IReadOnlyCollection<ulong>? removedPlayerIds = null)
    {
        var resolvedFlow = guests.Count > 0 ? CatchUpRegistry.GetById(flowId) : CatchUpRegistry.GetById(NoneCatchUpFlow.FlowId);
        var candidate = Candidate.Prepare(checkpoint, guests, removedPlayerIds, resolvedFlow.Id);
        var guestIds = guests.Select(g => g.GuestId).ToArray();
        var restoredIds = new HashSet<ulong>(guests.Where(g => g.RestoredPlayer != null).Select(g => g.GuestId));
        Expanded.Add(candidate, new ExpandedSession(guestIds, restoredIds, resolvedFlow.Id));
        Log.Info($"[MidRunJoin] Prepared roster change: +{guests.Count} guest(s), -{removedPlayerIds?.Count ?? 0} removed -> {candidate.Players.Count} seats (flow={resolvedFlow.Id}).");
        menu.StartHost(candidate);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen.InitializeAsHost))]
    private static void OnExpandedHostLobbyCreated(INetGameService gameService, SerializableRun run)
    {
        SendExpandedSteamInvites(gameService, run);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(NCustomRunLoadScreen), nameof(NCustomRunLoadScreen.InitializeAsHost))]
    private static void OnExpandedCustomHostLobbyCreated(INetGameService gameService, SerializableRun run)
    {
        SendExpandedSteamInvites(gameService, run);
    }

    private static void SendExpandedSteamInvites(INetGameService gameService, SerializableRun run)
    {
        if (Expanded.TryGetValue(run, out var session))
        {
            foreach (ulong guestId in session.GuestIds)
                SteamHelper.TryInviteGuest(gameService, guestId);
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.IsAboutToBeginGame))]
    private static void RequireRoster(LoadRunLobby __instance, ref bool __result)
    {
        if (__result && Expanded.TryGetValue(__instance.Run, out var session))
        {
            __result = Compat.GetLobbyPlayerCount(__instance) == __instance.Run.Players.Count &&
                       __instance.Run.Players.All(p => __instance.IsPlayerReady(p.NetId));
            if (__result && session.HasUnacknowledgedMissingModPeers && __instance.LobbyListener is Control)
                __result = false;
        }
    }
}

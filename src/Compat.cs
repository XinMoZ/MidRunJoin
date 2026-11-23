using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace MidRunJoin;

/// <summary>
/// Cross-version compatibility shims so a single compiled <c>MidRunJoin.dll</c> runs cleanly on both
/// the STS2 mainline release branch (正式版, e.g. v0.111.x / earlier) and <c>public-beta</c> (v0.113.x+).
/// </summary>
internal static class Compat
{
    private static readonly ConstructorInfo? AscensionCtor2 =
        AccessTools.Constructor(typeof(AscensionManager), [typeof(GameMode), typeof(int)]);
    private static readonly ConstructorInfo? AscensionCtor1 =
        AccessTools.Constructor(typeof(AscensionManager), [typeof(int)]);

    private static readonly ConstructorInfo? RngCtorUlongStr =
        AccessTools.Constructor(typeof(Rng), [typeof(ulong), typeof(string)]);
    private static readonly ConstructorInfo? RngCtorUintStr =
        AccessTools.Constructor(typeof(Rng), [typeof(uint), typeof(string)]);
    private static readonly ConstructorInfo? RngCtorUintInt =
        AccessTools.Constructor(typeof(Rng), [typeof(uint), typeof(int)]);
    private static readonly ConstructorInfo? RngCtorUint =
        AccessTools.Constructor(typeof(Rng), [typeof(uint)]);

    private static readonly MethodInfo? ErrorPopupForRunSave =
        AccessTools.Method(typeof(NErrorPopup), "CreateForRunSave", [typeof(ReadSaveStatus)]);

    private static readonly ConstructorInfo? NativeCardUpgradeRewardCtor =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Rewards.CardUpgradeReward") is { } upgradeType
            ? AccessTools.Constructor(upgradeType, [typeof(Player)])
            : null;

    private static readonly PropertyInfo? LobbyPlayerCountProp =
        AccessTools.Property(typeof(LoadRunLobby), "PlayerCount");
    private static readonly PropertyInfo? LobbyPlayersProp =
        AccessTools.Property(typeof(LoadRunLobby), "Players");
    private static readonly PropertyInfo? LobbyConnectedIdsProp =
        AccessTools.Property(typeof(LoadRunLobby), "ConnectedPlayerIds");

    internal static int GetLobbyPlayerCount(LoadRunLobby lobby)
    {
        if (LobbyPlayerCountProp?.GetValue(lobby) is int count)
            return count;
        if (LobbyPlayersProp?.GetValue(lobby) is System.Collections.ICollection players)
            return players.Count;
        if (LobbyConnectedIdsProp?.GetValue(lobby) is System.Collections.ICollection connected)
            return connected.Count;
        return 0;
    }

    internal static AscensionManager CreateAscensionManager(GameMode gameMode, int ascensionLevel)
    {
        if (AscensionCtor2 != null)
            return (AscensionManager)AscensionCtor2.Invoke([gameMode, ascensionLevel]);
        if (AscensionCtor1 != null)
            return (AscensionManager)AscensionCtor1.Invoke([ascensionLevel]);
        throw new MissingMethodException("No compatible AscensionManager constructor found.");
    }

    internal static Rng CreateDeterministicRng(string seedString, int seatOffset, string streamName)
    {
        ulong hash = ComputeFnv1a64(seedString ?? "") + (ulong)(uint)seatOffset;
        if (RngCtorUlongStr != null)
            return (Rng)RngCtorUlongStr.Invoke([hash, streamName]);
        if (RngCtorUintStr != null)
            return (Rng)RngCtorUintStr.Invoke([(uint)hash, streamName]);
        if (RngCtorUintInt != null)
            return (Rng)RngCtorUintInt.Invoke([(uint)hash, 0]);
        if (RngCtorUint != null)
            return (Rng)RngCtorUint.Invoke([(uint)hash]);
        throw new MissingMethodException("No compatible Rng constructor found.");
    }

    internal static NErrorPopup? CreateSaveErrorPopup(ReadSaveStatus status)
    {
        if (ErrorPopupForRunSave != null)
            return ErrorPopupForRunSave.Invoke(null, [status]) as NErrorPopup;

        return NErrorPopup.Create(
            new LocString("main_menu_ui", "INVALID_SAVE_POPUP.title"),
            new LocString("main_menu_ui", "INVALID_SAVE_POPUP.description_run"),
            new LocString("main_menu_ui", "INVALID_SAVE_POPUP.dismiss"),
            showReportBugButton: false);
    }

    internal static Reward CreateCardUpgradeReward(Player guest)
    {
        if (NativeCardUpgradeRewardCtor != null)
            return (Reward)NativeCardUpgradeRewardCtor.Invoke([guest]);
        return new FallbackCardUpgradeReward(guest);
    }

    private static readonly ConstructorInfo? CardRewardCtor =
        typeof(CardReward)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length >= 3
                    && p[0].ParameterType == typeof(CardCreationOptions)
                    && p[1].ParameterType == typeof(int)
                    && p[2].ParameterType == typeof(Player);
            });

    internal static CardReward CreateCardReward(CardCreationOptions options, int cardCount, Player guest)
    {
        if (CardRewardCtor == null)
            throw new MissingMethodException("No compatible CardReward constructor found.");
        var paramCount = CardRewardCtor.GetParameters().Length;
        var args = new object?[paramCount];
        args[0] = options;
        args[1] = cardCount;
        args[2] = guest;
        return (CardReward)CardRewardCtor.Invoke(args);
    }

    internal static CardCreationOptions WithIsFromCombatIfSupported(CardCreationOptions options)
    {
        if (Enum.TryParse<CardCreationFlags>("IsFromCombat", out var flag))
            return options.WithFlags(flag);
        return options;
    }

    private static ulong ComputeFnv1a64(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    /// <summary>
    /// Fallback card-upgrade reward for STS2 mainline versions that do not yet ship
    /// <c>MegaCrit.Sts2.Core.Rewards.CardUpgradeReward</c>.
    /// </summary>
    private sealed class FallbackCardUpgradeReward : Reward
    {
        protected override RewardType RewardType => RewardType.None;
        public override int RewardsSetIndex => 4;
        public override bool IsPopulated => true;
        protected override string IconPath => ImageHelper.GetImagePath("ui/reward_screen/reward_icon_card.png");
        public override LocString Description => new("rest_site_ui", "OPTION_SMITH.name");

        public FallbackCardUpgradeReward(Player player) : base(player) { }

        public override void Populate() { }

        protected override async Task<bool> OnSelect()
        {
            var prefs = new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1)
            {
                Cancelable = true
            };
            var selected = (await CardSelectCmd.FromDeckForUpgrade(Player, prefs)).ToList();
            if (selected.Count == 0)
                return false;
            CardCmd.Upgrade(selected, CardPreviewStyle.HorizontalLayout);
            return true;
        }

        public override void MarkContentAsSeen() { }
    }
}

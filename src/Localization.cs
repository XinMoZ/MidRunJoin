using System;
using MegaCrit.Sts2.Core.Localization;

namespace MidRunJoin;

/// <summary>
/// Bilingual (Chinese / English) localization helper that follows STS2's active <see cref="LocManager.Language"/>.
/// Chinese is used for <c>zhs</c> / <c>zht</c>; English is used for <c>eng</c> and all other locales.
/// </summary>
internal static class ModLoc
{
    internal static string? LanguageOverride { get; set; }

    internal static bool IsChinese
    {
        get
        {
            string? lang = LanguageOverride ?? LocManager.Instance?.Language;
            if (string.IsNullOrWhiteSpace(lang))
                return false;
            return lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static string Pick(string zh, string en) => IsChinese ? zh : en;

    // Floating button on Load Multiplayer button
    internal static string FloatingButtonText => Pick("＋ 中途加入", "+ Mid-Run Join");
    internal static string FloatingButtonTooltip => Pick(
        "调整联机成员：添加新玩家（带补给流程）或移除缺席玩家",
        "Adjust party roster: add mid-run guests (with catch-up rewards) or remove absent players");

    // Modal header & buttons
    internal static string DialogTitle => Pick("调整联机成员", "Adjust Party Roster");
    internal static string PartyCountBadge(int count) => Pick($"{count} 人", $"{count} Players");
    internal static string LocalModeBadge => Pick("本地联机", "Local LAN");
    internal static string DialogOkButton => Pick("建立调整后大厅", "Host Updated Lobby");
    internal static string DialogCancelButton => Pick("取消", "Cancel");

    internal static string DialogIntro(bool steamReady) => steamReady
        ? Pick(
            "可移除缺席的原成员，或从 Steam 好友列表添加新成员并选择职业；建房后将自动发送邀请。",
            "Remove absent players or pick Steam friends to join with a character; invites are sent automatically.")
        : Pick(
            "可移除缺席的原成员，或添加新玩家并选择职业。",
            "Remove absent players or add new players with a character.");

    internal static string RosterSummary(int kept, int removed, int added, int total)
    {
        if (IsChinese)
        {
            string suffix = total > 4 ? "（超过 4 人需配合联机破限 mod）" : "";
            return $"保留 {kept} 人 · 移除 {removed} 人 · 新增 {added} 人（共 {total} 人）{suffix}";
        }
        else
        {
            string suffix = total > 4 ? " (4+ players requires a limit-break mod)" : "";
            return $"Keeping {kept} · Removing {removed} · Adding {added} ({total} total){suffix}";
        }
    }

    // Section headers
    internal static string SectionCurrentParty => Pick("当前队伍", "Current Party");
    internal static string SectionAddGuests => Pick("添加新成员", "Add New Members");
    internal static string SectionQueuedGuests => Pick("待加入成员", "Queued Members");
    internal static string SectionCatchUp => Pick("新人补给", "Catch-Up Rewards");

    // Role badges & row actions
    internal static string HostBadge => Pick("房主", "Host");
    internal static string MemberBadge => Pick("原成员", "Member");
    internal static string RemovingBadge => Pick("将移除", "Removing");
    internal static string NewGuestBadge => Pick("新增", "New");
    internal static string RestoredGuestBadge => Pick("恢复进度", "Restore");
    internal static string SavedProgressBadge => Pick("已存进度", "Saved");
    internal static string RemoveButton => Pick("移除", "Remove");
    internal static string ConfirmDeleteButton => Pick("删除", "Delete");
    internal static string ConfirmStashButton => Pick("保存进度", "Save Progress");
    internal static string CancelRemoveButton => Pick("取消", "Cancel");
    internal static string UndoRemoveButton => Pick("撤销移除", "Undo");
    internal static string RestoredCharacterLockedHint(string name, string charName) => Pick(
        $"已选择：{name}（已保留进度，锁定为 {charName}）",
        $"Selected: {name} (saved progress, locked to {charName})");

    internal static string UnnamedPlayer(int seatNumber) => seatNumber > 0
        ? Pick($"玩家 #{seatNumber}", $"Player #{seatNumber}")
        : Pick("未知玩家", "Unknown Player");

    // Friend picker & input controls
    internal static string FriendSearchPlaceholder => Pick("搜索 Steam 好友...", "Search Steam friends...");
    internal static string OnlineOnlyCheckbox => Pick("仅看在线", "Online only");
    internal static string SelectFriendHint => Pick("点击好友选择（双击直接添加）", "Click a friend to select (double-click to add)");
    internal static string SelectedFriendLabel(string name) => Pick($"已选择：{name}", $"Selected: {name}");
    internal static string NoFriendsMatch => Pick("没有匹配的 Steam 好友", "No matching Steam friends");
    internal static string ManualSteamIdPlaceholder => Pick("或直接输入非好友 SteamID...", "Or enter a non-friend SteamID...");
    internal static string GuestIdPlaceholder => Pick("输入玩家编号（如 3）", "Enter player ID (e.g. 3)");
    internal static string RandomCharacterOption => Pick("随机", "Random");
    internal static string AddGuestButton => Pick("＋ 添加", "+ Add");
    internal static string CatchUpLabel => Pick("补给方式：", "Mode:");

    // Steam friend status tags
    internal static string StatusPlayingSts2 => Pick("正在游玩 STS2", "Playing STS2");
    internal static string StatusOnline => Pick("在线", "Online");
    internal static string StatusOffline => Pick("离线", "Offline");

    // Dialog validation messages
    internal static string ErrSelectFriendFirst => Pick(
        "请先从上方列表选择一名 Steam 好友。",
        "Please select a Steam friend from the list above.");
    internal static string ErrInvalidPlayerId => Pick(
        "请输入有效的玩家编号。",
        "Please enter a valid player number.");
    internal static string ErrDuplicatePlayer(string displayName) => Pick(
        $"「{displayName}」已在队伍中或已添加到列表。",
        $"\"{displayName}\" is already in the party or queued.");
    internal static string ErrNoChangesSelected => Pick(
        "请先移除缺席玩家，或至少添加一名新成员。",
        "Please remove an absent player or add at least one new member.");
    internal static string ErrGenericPrepareFailed => Pick(
        "调整成员失败，请查看游戏日志。",
        "Failed to update party roster. Please check the game log.");

    // Missing-mod warning popup (shown on Host when a peer connects without MidRunJoin)
    internal static string MissingModPopupTitle => Pick(
        "未检测到 MidRunJoin",
        "MidRunJoin Not Detected");
    internal static string MissingModPopupBody => Pick(
        "检测到加入玩家没有安装 mod，因此不会补发奖励：",
        "Detected joining player(s) without MidRunJoin installed. Catch-up rewards will not be granted:");
    internal static string MissingModOpenWorkshopButton => Pick(
        "打开创意工坊",
        "Open Workshop");
    internal static string MissingModIgnoreStarterButton => Pick(
        "忽视并以初始状态进入",
        "Ignore & Enter with Starter State");
    internal static string MissingModIgnoreSavedButton => Pick(
        "忽视并以保存状态进入",
        "Ignore & Enter with Saved State");
    internal static string MissingModWorkshopOpenedHint => Pick(
        "已在 Steam 界面中打开创意工坊页面。",
        "Opened Workshop page in Steam Overlay.");
    internal static string MissingModWorkshopUnavailableToast => Pick(
        "无法跳转，关注XinMoZinc谢谢喵",
        "Unable to open Steam Workshop — follow XinMoZinc, thanks nya!");

    // Candidate validation errors
    internal static string ErrExitRunFirst => Pick(
        "请先退出当前冒险后再调整联机成员。",
        "Please exit the current run before adjusting the party roster.");
    internal static string ErrDailyNotSupported => Pick(
        "每日挑战存档不支持中途调整成员。",
        "Mid-run roster changes are not supported in Daily Runs.");
    internal static string ErrEmptySave => Pick(
        "存档中没有任何玩家。",
        "The save file contains no players.");
    internal static string ErrNoRosterDelta => Pick(
        "请至少添加一名新玩家或移除一名缺席玩家。",
        "Please add at least one new player or remove an absent player.");
    internal static string ErrCannotRemoveHost => Pick(
        "不能移除房主（1 号位玩家）。",
        "Cannot remove the host (seat 1).");
    internal static string ErrPlayerNotInSave(ulong id) => Pick(
        "存档中不存在该玩家，无法移除。",
        "That player does not exist in this save and cannot be removed.");
    internal static string ErrNeedOneRemaining => Pick(
        "至少需要保留一名玩家。",
        "At least one player must remain in the party.");
    internal static string ErrZeroPlayerId => Pick(
        "玩家信息无效。",
        "Invalid player selection.");
    internal static string ErrGuestAlreadyInParty(ulong id) => Pick(
        "该玩家已在队伍中或被重复添加。",
        "That player is already in the party or added twice.");

    // Catch-up flow names & descriptions
    internal static string FlowHistoryBellName => Pick(
        "按经过的房间补给奖励",
        "Catch-Up Rewards (By Visited Rooms)");
    internal static string FlowHistoryBellDesc => Pick(
        "根据队伍已历经的房间发放对应奖励：战斗提供卡牌/金币/遗物、问号与商店各折算10金、宝箱提供金币与随机遗物、每2个火堆提供1次强化、每名先古之民提供1次对应遗物三选一、每名Boss提供1次删牌。",
        "Grants rewards based on rooms the party has visited: combat cards/gold/relics, +10 gold per ?/Shop, treasure gold + relic, 1 card upgrade per 2 Rest Sites, 1 choose-1-of-3 Ancient relic per visited Ancient, and 1 card removal per Boss.");
    internal static string FlowNoneName => Pick(
        "无补给（仅初始牌组与遗物）",
        "No Catch-Up (Starter Kit Only)");
    internal static string FlowNoneDesc => Pick(
        "不发放额外补给，新人直接以职业初始配置加入。",
        "No extra rewards; new guests join with their character's starter deck and relic.");
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Localization;

namespace MidRunJoin;

/// <summary>
/// Data-driven localization loader backed by standalone JSON tables under <c>localization/*.json</c>
/// (embedded into the mod assembly, with optional external overrides in <c>&lt;modDir&gt;/localization/&lt;lang&gt;.json</c>).
/// Follows STS2's active <see cref="LocManager.Language"/>, falling back from <c>zh*</c> to <c>zhs</c>
/// and from any other language to <c>eng</c>.
/// </summary>
internal static class ModLoc
{
    private const string ResourcePrefix = "MidRunJoin.localization.";
    private const string ResourceSuffix = ".json";
    private const string DefaultLocale = "eng";

    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Tables = new(LoadAllTables);

    internal static string? LanguageOverride { get; set; }

    internal static string ActiveLanguage
    {
        get
        {
            string? lang = LanguageOverride ?? LocManager.Instance?.Language;
            return string.IsNullOrWhiteSpace(lang) ? DefaultLocale : lang.Trim().ToLowerInvariant();
        }
    }

    internal static bool IsChinese => ActiveLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyDictionary<string, string> GetTable(string locale)
    {
        var all = Tables.Value;
        string normalized = string.IsNullOrWhiteSpace(locale) ? DefaultLocale : locale.Trim().ToLowerInvariant();
        if (all.TryGetValue(normalized, out var exact))
            return exact;
        if (normalized.StartsWith("zh", StringComparison.Ordinal) && all.TryGetValue("zhs", out var zhs))
            return zhs;
        if (all.TryGetValue(DefaultLocale, out var eng))
            return eng;
        return new Dictionary<string, string>();
    }

    internal static string Get(string key)
    {
        var all = Tables.Value;
        string lang = ActiveLanguage;

        if (all.TryGetValue(lang, out var exact) && exact.TryGetValue(key, out var val))
            return val;
        if (lang.StartsWith("zh", StringComparison.Ordinal) &&
            all.TryGetValue("zhs", out var zhs) &&
            zhs.TryGetValue(key, out val))
            return val;
        if (all.TryGetValue(DefaultLocale, out var eng) && eng.TryGetValue(key, out val))
            return val;
        return key;
    }

    private static string Format(string key, params (string Name, object? Value)[] args)
    {
        string text = Get(key);
        foreach (var (name, value) in args)
            text = text.Replace("{" + name + "}", value?.ToString() ?? "", StringComparison.Ordinal);
        return text;
    }

    private static Dictionary<string, Dictionary<string, string>> LoadAllTables()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var asm = typeof(ModLoc).Assembly;

        foreach (string resName in asm.GetManifestResourceNames())
        {
            if (!resName.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase) ||
                !resName.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string locale = resName.Substring(
                ResourcePrefix.Length,
                resName.Length - ResourcePrefix.Length - ResourceSuffix.Length).ToLowerInvariant();

            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null)
                continue;
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (dict != null)
                result[locale] = dict;
        }

        // Optional external overrides: <modDir>/localization/<locale>.json
        try
        {
            string? asmDir = Path.GetDirectoryName(asm.Location);
            if (!string.IsNullOrWhiteSpace(asmDir))
            {
                string extDir = Path.Combine(asmDir, "localization");
                if (Directory.Exists(extDir))
                {
                    foreach (string file in Directory.GetFiles(extDir, "*.json"))
                    {
                        string locale = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                        var extDict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                        if (extDict == null)
                            continue;
                        if (!result.TryGetValue(locale, out var target))
                        {
                            result[locale] = extDict;
                        }
                        else
                        {
                            foreach (var kv in extDict)
                                target[kv.Key] = kv.Value;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore external override I/O errors; embedded tables remain intact.
        }

        return result;
    }

    // Floating button on Host from Save button
    internal static string FloatingButtonText => Get("floating.button");
    internal static string FloatingButtonTooltip => Get("floating.tooltip");

    // Modal header & buttons
    internal static string DialogTitle => Get("dialog.title");
    internal static string PartyCountBadge(int count) => Format("dialog.party_count_badge", ("count", count));
    internal static string LocalModeBadge => Get("dialog.local_mode_badge");
    internal static string DialogOkButton => Get("dialog.ok_button");
    internal static string DialogCancelButton => Get("dialog.cancel_button");

    internal static string DialogIntro(bool steamReady) =>
        Get(steamReady ? "dialog.intro_steam" : "dialog.intro_local");

    internal static string RosterSummary(int kept, int removed, int added, int total)
    {
        string suffix = total > 4 ? Get("dialog.roster_summary_limit_suffix") : "";
        return Format(
            "dialog.roster_summary",
            ("kept", kept),
            ("removed", removed),
            ("added", added),
            ("total", total),
            ("suffix", suffix));
    }

    // Section headers
    internal static string SectionCurrentParty => Get("section.current_party");
    internal static string SectionAddGuests => Get("section.add_guests");
    internal static string SectionQueuedGuests => Get("section.queued_guests");
    internal static string SectionCatchUp => Get("section.catch_up");

    // Role badges & row actions
    internal static string HostBadge => Get("role.host");
    internal static string MemberBadge => Get("role.member");
    internal static string RemovingBadge => Get("role.removing");
    internal static string NewGuestBadge => Get("role.new_guest");
    internal static string RestoredGuestBadge => Get("role.restored_guest");
    internal static string SavedProgressBadge => Get("role.saved_progress");
    internal static string RemoveButton => Get("action.remove");
    internal static string ConfirmDeleteButton => Get("action.confirm_delete");
    internal static string ConfirmStashButton => Get("action.confirm_stash");
    internal static string CancelRemoveButton => Get("action.cancel_remove");
    internal static string UndoRemoveButton => Get("action.undo_remove");

    internal static string RestoredCharacterLockedHint(string name, string charName) =>
        Format("hint.restored_character_locked", ("name", name), ("char", charName));

    internal static string UnnamedPlayer(int seatNumber) => seatNumber > 0
        ? Format("player.unnamed_seat", ("seat", seatNumber))
        : Get("player.unknown");

    // Friend picker & input controls
    internal static string FriendSearchPlaceholder => Get("picker.friend_search_placeholder");
    internal static string OnlineOnlyCheckbox => Get("picker.online_only");
    internal static string SelectFriendHint => Get("picker.select_friend_hint");
    internal static string SelectedFriendLabel(string name) => Format("picker.selected_friend", ("name", name));
    internal static string NoFriendsMatch => Get("picker.no_friends_match");
    internal static string ManualSteamIdPlaceholder => Get("picker.manual_steam_id_placeholder");
    internal static string GuestIdPlaceholder => Get("picker.guest_id_placeholder");
    internal static string RandomCharacterOption => Get("picker.random_character");
    internal static string AddGuestButton => Get("action.add_guest");
    internal static string CatchUpLabel => Get("picker.catch_up_label");

    // Steam friend status tags
    internal static string StatusPlayingSts2 => Get("status.playing_sts2");
    internal static string StatusOnline => Get("status.online");
    internal static string StatusOffline => Get("status.offline");

    // Dialog validation messages
    internal static string ErrSelectFriendFirst => Get("err.select_friend_first");
    internal static string ErrInvalidPlayerId => Get("err.invalid_player_id");
    internal static string ErrDuplicatePlayer(string displayName) => Format("err.duplicate_player", ("name", displayName));
    internal static string ErrNoChangesSelected => Get("err.no_changes_selected");
    internal static string ErrGenericPrepareFailed => Get("err.generic_prepare_failed");

    // Missing-mod warning popup (shown on Host when a peer connects without MidRunJoin)
    internal static string MissingModPopupTitle => Get("missing_mod.title");
    internal static string MissingModPopupBody => Get("missing_mod.body");
    internal static string MissingModOpenWorkshopButton => Get("missing_mod.open_workshop");
    internal static string MissingModIgnoreStarterButton => Get("missing_mod.ignore_starter");
    internal static string MissingModIgnoreSavedButton => Get("missing_mod.ignore_saved");
    internal static string MissingModWorkshopOpenedHint => Get("missing_mod.workshop_opened_hint");
    internal static string MissingModWorkshopUnavailableToast => Get("missing_mod.workshop_unavailable_toast");

    // Candidate validation errors
    internal static string ErrExitRunFirst => Get("err.exit_run_first");
    internal static string ErrDailyNotSupported => Get("err.daily_not_supported");
    internal static string ErrEmptySave => Get("err.empty_save");
    internal static string ErrNoRosterDelta => Get("err.no_roster_delta");
    internal static string ErrCannotRemoveHost => Get("err.cannot_remove_host");
    internal static string ErrPlayerNotInSave(ulong id) => Get("err.player_not_in_save");
    internal static string ErrNeedOneRemaining => Get("err.need_one_remaining");
    internal static string ErrZeroPlayerId => Get("err.zero_player_id");
    internal static string ErrGuestAlreadyInParty(ulong id) => Get("err.guest_already_in_party");

    // Catch-up flow names & descriptions
    internal static string FlowHistoryBellName => Get("flow.history_bell.name");
    internal static string FlowHistoryBellDesc => Get("flow.history_bell.desc");
    internal static string FlowNoneName => Get("flow.none.name");
    internal static string FlowNoneDesc => Get("flow.none.desc");
}

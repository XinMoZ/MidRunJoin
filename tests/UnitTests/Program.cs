using System.Diagnostics;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Platform.Steam;
using MidRunJoin;
using MidRunJoin.CatchUp;
using Steamworks;

int checks = 0;
void Check(bool condition, string name)
{
    checks++;
    if (!condition)
        throw new Exception($"UNIT TEST FAIL: {name}");
}

// 1. Pure SteamHelper.RankAndFormatFriends tests (Chinese + English localization)
{
    MegaCrit.Sts2.Core.Localization.LocManager.Instance = new() { Language = "zhs" };
    ModLoc.LanguageOverride = null;
    var sample = new RawFriendInfo[]
    {
        new(100, "Zoe", false, false),          // offline
        new(101, "Alice", false, true),         // online
        new(102, "bob", true, true),            // playing STS2
        new(103, "Aaron", true, true),          // playing STS2 (before bob)
        new(104, "Existing", true, true),       // excluded
        new(0,   "ZeroId", true, true),         // invalid ID 0
        new(105, "   ", false, true),           // blank name -> fallback to "105"
        new(106, "alice", false, true),         // same name case-insensitive tie-break by id (101 < 106)
        new(107, "Charlie", false, false),      // offline (before Zoe)
    };

    var ranked = SteamHelper.RankAndFormatFriends(sample, new ulong[] { 104, 999 });
    Check(ranked.Count == 7, $"expected 7 ranked friends, got {ranked.Count}");
    Check(ranked.Select(r => r.SteamId).SequenceEqual(new ulong[] { 103, 102, 101, 106, 105, 107, 100 }),
        $"unexpected rank order: {string.Join(",", ranked.Select(r => r.SteamId))}");
    Check(ranked[0].Label == "Aaron · 正在游玩 STS2" && !ranked[0].Label.Contains("103"), $"bad sts2 label: {ranked[0].Label}");
    Check(ranked[1].Label == "bob · 正在游玩 STS2" && !ranked[1].Label.Contains("102"), $"bad sts2 label 2: {ranked[1].Label}");
    Check(ranked[2].Label == "Alice · 在线" && !ranked[2].Label.Contains("101"), $"bad online label: {ranked[2].Label}");
    Check(ranked[4].Label == "未知玩家 · 在线" && !ranked[4].Label.Contains("105"), $"blank name fallback failed: {ranked[4].Label}");
    Check(ranked[5].Label == "Charlie · 离线" && !ranked[5].Label.Contains("107"), $"bad offline label: {ranked[5].Label}");

    // Empty and all-excluded inputs
    Check(SteamHelper.RankAndFormatFriends([], []).Count == 0, "empty friends returns empty");
    Check(SteamHelper.RankAndFormatFriends([new(1, "A", true, true)], [1]).Count == 0, "all excluded returns empty");

    // Search & onlineOnly filtering
    var onlineOnly = SteamHelper.FilterFriends(ranked, "", onlineOnly: true);
    Check(onlineOnly.Count == 5 && onlineOnly.All(f => f.IsOnline || f.IsPlayingSts2), "onlineOnly hides offline friends");
    var byName = SteamHelper.FilterFriends(ranked, "ali", onlineOnly: false);
    Check(byName.Select(f => f.SteamId).SequenceEqual(new ulong[] { 101, 106 }), "case-insensitive nickname search");
    var onlineSearch = SteamHelper.FilterFriends(ranked, "zoe", onlineOnly: true);
    Check(onlineSearch.Count == 0, "onlineOnly excludes matching offline friend");

    // Stashed players sort first AND stay visible even when onlineOnly is true
    var withStashed = SteamHelper.FilterFriends(ranked, "", onlineOnly: true, stashedPlayerIds: new HashSet<ulong> { 100, 106 });
    Check(withStashed[0].SteamId == 106 && withStashed[1].SteamId == 100,
        $"stashed players should sort ahead of non-stashed friends, got: {string.Join(",", withStashed.Take(3).Select(f => f.SteamId))}");
    Check(withStashed.Any(f => f.SteamId == 100), "offline stashed player remains visible when onlineOnly is true");

    // English locale verification
    ModLoc.LanguageOverride = "eng";
    var rankedEn = SteamHelper.RankAndFormatFriends(sample, new ulong[] { 104, 999 });
    Check(rankedEn[0].Label == "Aaron · Playing STS2" && !rankedEn[0].Label.Contains("103"), $"bad en sts2 label: {rankedEn[0].Label}");
    Check(rankedEn[2].Label == "Alice · Online" && !rankedEn[2].Label.Contains("101"), $"bad en online label: {rankedEn[2].Label}");
    Check(rankedEn[5].Label == "Charlie · Offline" && !rankedEn[5].Label.Contains("107"), $"bad en offline label: {rankedEn[5].Label}");
    Check(ModLoc.FloatingButtonText == "+ Mid-Run Join", $"bad en floating button: {ModLoc.FloatingButtonText}");
    Check(ModLoc.FlowHistoryBellName.Contains("Visited Rooms"), $"bad en flow name: {ModLoc.FlowHistoryBellName}");
    ModLoc.LanguageOverride = "jpn"; // non-Chinese falls back to English
    Check(!ModLoc.IsChinese && ModLoc.FloatingButtonText == "+ Mid-Run Join", "non-Chinese locale should fall back to English");
    ModLoc.LanguageOverride = "zht"; // Traditional Chinese -> Chinese
    Check(ModLoc.IsChinese && ModLoc.FloatingButtonText == "＋ 中途加入", "zht should use Chinese");
    ModLoc.LanguageOverride = null;

    Console.WriteLine($"PASS: SteamHelper.RankAndFormatFriends + FilterFriends + ModLoc ({checks} checks)");
}

// 2. SteamHelper.GetCandidateFriends with Steamworks stubs & fault injection
{
    SteamFriends.Reset();
    Log.Reset();
    SteamInitializer.Initialized = false;
    SteamFriends.Friends.Add(new(76561198000000001, "Player1", SteamHelper.Sts2AppId, EPersonaState.k_EPersonaStateOnline));
    Check(SteamHelper.GetCandidateFriends([]).Count == 0, "uninitialized Steam returns empty");
    Check(SteamFriends.GetFriendCountCalls == 0, "uninitialized Steam must not call SteamFriends");

    SteamInitializer.Initialized = true;
    SteamFriends.Friends.Add(new(76561198000000002, "Host", SteamHelper.Sts2AppId, EPersonaState.k_EPersonaStateOnline));
    SteamFriends.Friends.Add(new(76561198000000003, "OtherGame", 646570, EPersonaState.k_EPersonaStateOnline));
    SteamFriends.Friends.Add(new(76561198000000004, "AwayFriend", 0, EPersonaState.k_EPersonaStateAway));
    SteamFriends.Friends.Add(new(76561198000000005, "OfflineFriend", 0, EPersonaState.k_EPersonaStateOffline));

    var list = SteamHelper.GetCandidateFriends([76561198000000002]);
    Check(list.Count == 4, $"expected 4 candidate friends, got {list.Count}");
    Check(list[0].SteamId == 76561198000000001 && list[0].StatusTag == "正在游玩 STS2" && !list[0].Label.Contains("76561198"), "STS2 friend first without exposing SteamID");
    Check(list[1].SteamId == 76561198000000004 && list[1].StatusTag == "在线", "Away friend is online");
    Check(list[2].SteamId == 76561198000000003 && list[2].StatusTag == "在线", "Other-game friend is online, not STS2");
    Check(list[3].SteamId == 76561198000000005 && list[3].StatusTag == "离线", "Offline friend last");

    // TryGetPlayerName & FormatPlayerIdentity (never expose raw SteamID64!)
    Check(SteamHelper.TryGetPlayerName(76561198000000002) == "Host", "TryGetPlayerName resolves local host via GetPersonaName");
    Check(SteamHelper.TryGetPlayerName(76561198000000001) == "Player1", "TryGetPlayerName resolves friend persona name");
    Check(SteamHelper.TryGetPlayerName(999999) == null, "TryGetPlayerName returns null for unknown/empty persona");
    Check(SteamHelper.FormatPlayerIdentity(76561198000000002, "Host") == "Host", "FormatPlayerIdentity with name hides SteamID");
    Check(SteamHelper.FormatPlayerIdentity(76561198000000099, null, 2) == "玩家 #2", "FormatPlayerIdentity fallback uses seat number, not SteamID");
    Check(SteamHelper.FormatPlayerIdentity(1, null) == "玩家 #1", "FormatPlayerIdentity small local ID fallback");

    // Fault injection: Steam API throws
    SteamFriends.ThrowOnEnumerate = true;
    Log.Reset();
    var afterFault = SteamHelper.GetCandidateFriends([]);
    Check(afterFault.Count == 0, "fault returns empty safely");
    Check(Log.Warnings.Any(w => w.Contains("Failed to enumerate Steam friends")), "fault logs warning");
    SteamFriends.Reset();
    Console.WriteLine($"PASS: SteamHelper.GetCandidateFriends + fault injection ({checks} checks)");
}

// 3. SteamHelper.TryInviteGuest tests
{
    SteamMatchmaking.Reset();
    Log.Reset();

    var validService = new FakeHostService(new SteamHost { LobbyId = new CSteamID(9000001) });
    var nullLobbyService = new FakeHostService(new SteamHost { LobbyId = null });
    var enetService = new FakeHostService(new object());
    var clientService = new FakeClientService();

    // Uninitialized Steam -> no invite
    SteamInitializer.Initialized = false;
    SteamHelper.TryInviteGuest(validService, 76561198000000003);
    Check(SteamMatchmaking.Invites.Count == 0, "uninitialized Steam must not invite");

    // Initialized Steam, invalid guestId 0 -> no invite
    SteamInitializer.Initialized = true;
    SteamHelper.TryInviteGuest(validService, 0);
    Check(SteamMatchmaking.Invites.Count == 0, "guestId 0 must not invite");

    // Non-Steam / client / null LobbyId -> no invite
    SteamHelper.TryInviteGuest(enetService, 3);
    SteamHelper.TryInviteGuest(clientService, 3);
    SteamHelper.TryInviteGuest(nullLobbyService, 3);
    Check(SteamMatchmaking.Invites.Count == 0, "non-Steam or null LobbyId must not invite");

    // Valid SteamHost -> invites exact lobby & guest
    SteamHelper.TryInviteGuest(validService, 76561198000000009);
    Check(SteamMatchmaking.Invites.Count == 1, "valid SteamHost sends 1 invite");
    Check(SteamMatchmaking.Invites[0] == (9000001UL, 76561198000000009UL), "invite lobby/guest ID mismatch");
    Check(Log.Infos.Any(m => m.Contains("Sent Steam lobby invite") && m.Contains("ok=True")), "invite logs info");

    // Invite throws -> caught and logged
    SteamMatchmaking.ThrowOnInvite = true;
    Log.Reset();
    SteamHelper.TryInviteGuest(validService, 76561198000000010);
    Check(Log.Warnings.Any(w => w.Contains("Failed to send Steam lobby invite")), "invite exception logged");
    SteamMatchmaking.Reset();
    Console.WriteLine($"PASS: SteamHelper.TryInviteGuest ({checks} checks)");
}

// 4. Performance micro-benchmark for friend ranking (inspired by AstralEmotes/tests/PerformanceTests.cs)
{
    var largeList = Enumerable.Range(1, 200)
        .Select(i => new RawFriendInfo((ulong)(76561198000000000 + i), $"Friend_{i:D3}", i % 10 == 0, i % 3 != 0))
        .ToArray();
    var excluded = new ulong[] { 76561198000000010, 76561198000000020 };
    for (int i = 0; i < 64; i++) SteamHelper.RankAndFormatFriends(largeList, excluded);
    var sw = Stopwatch.StartNew();
    const int iters = 1000;
    for (int i = 0; i < iters; i++) SteamHelper.RankAndFormatFriends(largeList, excluded);
    sw.Stop();
    double usPerOp = sw.Elapsed.TotalMicroseconds / iters;
    Check(usPerOp < 5000, $"RankAndFormatFriends(200) unexpectedly slow: {usPerOp:F1} us/op");
    Console.WriteLine($"PERF: RankAndFormatFriends(200 friends): {usPerOp:F1} us/op");
}

// 5. HistoryBellFormula pure reward calculation tests
{
    var zero = HistoryBellFormula.Compute(new CatchUpRoomCounts(0, 0, 0, 0, 0, 0, 0));
    Check(zero.FlatBonusGold == 0 && zero.TotalCardRewards == 0 && zero.TotalRelicRewards == 0
          && zero.CardUpgradeRewards == 0 && zero.CardRemovalRewards == 0, "zero rooms -> zero rewards");

    // 3 Monsters, 2 Elites, 1 Boss, 4 Unknown/Shop, 2 Treasures, 5 RestSites (floor(5/2)=2), 2 Ancients (1 each = 2)
    var rich = HistoryBellFormula.Compute(new CatchUpRoomCounts(
        MonsterCombats: 3, EliteCombats: 2, BossCombats: 1,
        UnknownAndShopRooms: 4, TreasureRooms: 2, RestSites: 5, AncientRooms: 2));
    Check(rich.FlatBonusGold == 40, $"4 unknown/shop -> 40 gold, got {rich.FlatBonusGold}");
    Check(rich.MonsterCardRewards == 3 && rich.EliteCardRewards == 2 && rich.BossCardRewards == 1 && rich.TotalCardRewards == 6,
        $"expected 6 combat card rewards, got {rich.TotalCardRewards}");
    Check(rich.RandomRelicRewards == 4, $"2 elite + 2 treasure -> 4 random relics, got {rich.RandomRelicRewards}");
    Check(rich.AncientRelicRewards == 2, $"2 ancients -> 2 ancient relic choices (1 each), got {rich.AncientRelicRewards}");
    Check(rich.CardUpgradeRewards == 2, $"5 rest sites -> floor(5/2)=2 upgrades, got {rich.CardUpgradeRewards}");
    Check(rich.CardRemovalRewards == 1, $"1 boss -> 1 card removal, got {rich.CardRemovalRewards}");

    // Edge cases: 1 rest site -> 0 upgrades; 2 rest sites -> 1 upgrade; negative inputs clamped to 0
    Check(HistoryBellFormula.Compute(new CatchUpRoomCounts(0, 0, 0, 0, 0, 1, 0)).CardUpgradeRewards == 0, "1 rest site -> 0 upgrades");
    Check(HistoryBellFormula.Compute(new CatchUpRoomCounts(0, 0, 0, 0, 0, 2, 0)).CardUpgradeRewards == 1, "2 rest sites -> 1 upgrade");
    var neg = HistoryBellFormula.Compute(new CatchUpRoomCounts(-1, -2, -3, -4, -5, -6, -7));
    Check(neg.FlatBonusGold == 0 && neg.TotalCardRewards == 0 && neg.TotalRelicRewards == 0
          && neg.CardUpgradeRewards == 0 && neg.CardRemovalRewards == 0, "negative counts clamped to 0");
    Console.WriteLine($"PASS: HistoryBellFormula pure reward math ({checks} checks)");

    // 5b. CatchUpHealthFormula average HP% sync tests
    Check(CatchUpHealthFormula.ComputeSyncedCurrentHp([(40, 80), (60, 75)], 70) == 46,
        "50% + 80% avg = 65% of 70 -> 46");
    Check(CatchUpHealthFormula.ComputeSyncedCurrentHp([(80, 80), (70, 70)], 75) == 75,
        "100% avg -> full MaxHp");
    Check(CatchUpHealthFormula.ComputeSyncedCurrentHp([(0, 80), (0, 70)], 70) == 1,
        "0% avg clamped to at least 1 HP");
    Check(CatchUpHealthFormula.ComputeSyncedCurrentHp([(20, 80)], 80) == 20,
        "single player 25% -> 20/80");
    Check(CatchUpHealthFormula.ComputeSyncedCurrentHp([], 70) == 70,
        "empty existing list -> full MaxHp");
    Console.WriteLine($"PASS: CatchUpHealthFormula HP% sync ({checks} checks)");
}

// 5b. SteamHelper.TryOpenWorkshopPage (Steam Overlay only, no OS shell)
{
    SteamFriends.Reset();
    SteamInitializer.Initialized = false;
    SteamUtils.OverlayEnabled = true;
    Check(!SteamHelper.TryOpenWorkshopPage("https://steamcommunity.com/sharedfiles/filedetails/?id=3844124271"),
        "TryOpenWorkshopPage returns false when Steam is uninitialized");

    SteamInitializer.Initialized = true;
    SteamUtils.OverlayEnabled = false;
    Check(!SteamHelper.TryOpenWorkshopPage("https://steamcommunity.com/sharedfiles/filedetails/?id=3844124271"),
        "TryOpenWorkshopPage returns false when Steam Overlay is disabled");

    SteamUtils.OverlayEnabled = true;
    Check(SteamHelper.TryOpenWorkshopPage("https://steamcommunity.com/sharedfiles/filedetails/?id=3844124271"),
        "TryOpenWorkshopPage returns true when Steam Overlay is enabled");
    Check(SteamFriends.OpenedWebPages.Count == 1 &&
          SteamFriends.OpenedWebPages[0].Contains("3844124271"),
        "TryOpenWorkshopPage opens exact workshop URL via SteamFriends");
    Console.WriteLine($"PASS: SteamHelper.TryOpenWorkshopPage ({checks} checks)");
}

// 6. Manifest & Release packaging hygiene check
{
    string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    string manifestPath = Path.Combine(projectRoot, "MidRunJoin.json");
    Check(File.Exists(manifestPath), $"Missing manifest: {manifestPath}");
    using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
    var root = doc.RootElement;
    Check(root.GetProperty("id").GetString() == "MidRunJoin", "manifest id == MidRunJoin");
    Check(root.GetProperty("has_dll").GetBoolean(), "manifest has_dll == true");
    Check(!root.GetProperty("has_pck").GetBoolean(), "manifest has_pck == false");
    Check(!root.GetProperty("affects_gameplay").GetBoolean(), "manifest affects_gameplay == false");
    Check(!string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()), "manifest version non-empty");

    string distDir = Path.Combine(projectRoot, "dist/MidRunJoin");
    if (Directory.Exists(distDir))
    {
        var files = Directory.GetFiles(distDir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Check(files.SequenceEqual(new[] { "MidRunJoin.dll", "MidRunJoin.json" }),
            $"dist/MidRunJoin must contain only MidRunJoin.dll and MidRunJoin.json, found: {string.Join(", ", files)}");
        Console.WriteLine($"PASS: Manifest & dist/MidRunJoin hygiene ({checks} checks)");
    }
}

Console.WriteLine($"=== UNIT TESTS PASS: {checks} total checks ===");

sealed class FakeHostService(object? netHost) : INetHostGameService
{
    public object? NetHost => netHost;
}

sealed class FakeClientService : INetGameService { }

using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Platform.Steam;
using Steamworks;

namespace MidRunJoin;

internal readonly record struct SteamFriendEntry(
    ulong SteamId,
    string Name,
    string Label,
    string StatusTag,
    bool IsPlayingSts2,
    bool IsOnline);

internal readonly record struct RawFriendInfo(ulong SteamId, string Name, bool IsPlayingSts2, bool IsOnline);

internal static class SteamHelper
{
    internal const ulong Sts2AppId = 2868840;

    internal static bool IsSteamReady => SteamInitializer.Initialized;

    internal static List<SteamFriendEntry> FilterFriends(
        IReadOnlyList<SteamFriendEntry> friends,
        string? query,
        bool onlineOnly,
        IReadOnlySet<ulong>? stashedPlayerIds = null)
    {
        string trimmed = (query ?? "").Trim();
        var stashed = new List<SteamFriendEntry>();
        var regular = new List<SteamFriendEntry>(friends.Count);
        foreach (var f in friends)
        {
            bool isStashed = stashedPlayerIds != null && stashedPlayerIds.Contains(f.SteamId);
            if (!isStashed && onlineOnly && !f.IsOnline && !f.IsPlayingSts2)
                continue;
            if (trimmed.Length > 0 &&
                !f.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                continue;
            if (isStashed)
                stashed.Add(f);
            else
                regular.Add(f);
        }
        if (stashed.Count == 0)
            return regular;
        stashed.AddRange(regular);
        return stashed;
    }

    internal static List<SteamFriendEntry> RankAndFormatFriends(
        IEnumerable<RawFriendInfo> friends,
        IEnumerable<ulong> existingPlayerIds)
    {
        var excluded = new HashSet<ulong>(existingPlayerIds);
        var raw = new List<(ulong id, string name, int rank, string tag, bool playingSts2, bool online)>();

        foreach (var f in friends)
        {
            if (f.SteamId == 0 || excluded.Contains(f.SteamId))
                continue;

            string name = string.IsNullOrWhiteSpace(f.Name) ? ModLoc.UnnamedPlayer(0) : f.Name.Trim();
            int rank;
            string tag;
            if (f.IsPlayingSts2)
            {
                rank = 0;
                tag = ModLoc.StatusPlayingSts2;
            }
            else if (f.IsOnline)
            {
                rank = 1;
                tag = ModLoc.StatusOnline;
            }
            else
            {
                rank = 2;
                tag = ModLoc.StatusOffline;
            }

            raw.Add((f.SteamId, name, rank, tag, f.IsPlayingSts2, f.IsOnline));
        }

        var result = new List<SteamFriendEntry>(raw.Count);
        foreach (var f in raw.OrderBy(x => x.rank).ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.id))
        {
            // Never expose raw SteamID64 to players in the UI label.
            result.Add(new SteamFriendEntry(f.id, f.name, $"{f.name} · {f.tag}", f.tag, f.playingSts2, f.online));
        }
        return result;
    }

    internal static List<SteamFriendEntry> GetCandidateFriends(IEnumerable<ulong> existingPlayerIds)
    {
        if (!SteamInitializer.Initialized)
            return new List<SteamFriendEntry>();

        try
        {
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            var raw = new List<RawFriendInfo>(Math.Max(0, count));

            for (int i = 0; i < count; i++)
            {
                CSteamID sid = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                ulong id = sid.m_SteamID;
                string name = SteamFriends.GetFriendPersonaName(sid);
                bool playingSts2 = SteamFriends.GetFriendGamePlayed(sid, out var gameInfo)
                                   && gameInfo.m_gameID.m_GameID == Sts2AppId;
                bool online = playingSts2
                              || SteamFriends.GetFriendPersonaState(sid) != EPersonaState.k_EPersonaStateOffline;
                raw.Add(new RawFriendInfo(id, name, playingSts2, online));
            }

            return RankAndFormatFriends(raw, existingPlayerIds);
        }
        catch (Exception ex)
        {
            Log.Warn($"[MidRunJoin] Failed to enumerate Steam friends: {ex.Message}");
            return new List<SteamFriendEntry>();
        }
    }

    internal static string? TryGetPlayerName(ulong steamId)
    {
        if (!SteamInitializer.Initialized || steamId == 0)
            return null;
        try
        {
            string name = steamId == SteamUser.GetSteamID().m_SteamID
                ? SteamFriends.GetPersonaName()
                : SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
            if (string.IsNullOrWhiteSpace(name) || name.Equals("[unknown]", StringComparison.OrdinalIgnoreCase))
                return null;
            return name.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a player's display name for the UI without exposing raw SteamID64 values.
    /// </summary>
    internal static string FormatPlayerIdentity(ulong id, string? name, int fallbackSeatNumber = 0)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();
        if (fallbackSeatNumber > 0)
            return ModLoc.UnnamedPlayer(fallbackSeatNumber);
        if (id > 0 && id <= 9999)
            return ModLoc.UnnamedPlayer((int)id);
        return ModLoc.UnnamedPlayer(0);
    }

    internal static void TryInviteGuest(INetGameService gameService, ulong guestId)
    {
        if (!SteamInitializer.Initialized || guestId == 0)
            return;
        try
        {
            if (gameService is INetHostGameService { NetHost: SteamHost { LobbyId: { } lobbyId } })
            {
                bool ok = SteamMatchmaking.InviteUserToLobby(lobbyId, new CSteamID(guestId));
                Log.Info($"[MidRunJoin] Sent Steam lobby invite to guest {guestId} (lobby={lobbyId.m_SteamID}, ok={ok}).");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[MidRunJoin] Failed to send Steam lobby invite to {guestId}: {ex.Message}");
        }
    }

    internal static bool TryOpenWorkshopPage(string url)
    {
        if (!SteamInitializer.Initialized || string.IsNullOrWhiteSpace(url))
            return false;
        try
        {
            if (!SteamUtils.IsOverlayEnabled())
                return false;
            SteamFriends.ActivateGameOverlayToWebPage(
                url,
                EActivateGameOverlayToWebPageMode.k_EActivateGameOverlayToWebPageMode_Default);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[MidRunJoin] Failed to open Steam Overlay web page: {ex.Message}");
            return false;
        }
    }
}

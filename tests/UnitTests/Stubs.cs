using System;
using System.Collections.Generic;
using Steamworks;

namespace MegaCrit.Sts2.Core.Localization
{
    public sealed class LocManager
    {
        public static LocManager? Instance { get; set; }
        public string Language { get; set; } = "zhs";
    }
}

namespace MegaCrit.Sts2.Core.Logging
{
    public static class Log
    {
        public static readonly List<string> Infos = new();
        public static readonly List<string> Warnings = new();
        public static void Info(string msg) => Infos.Add(msg);
        public static void Warn(string msg) => Warnings.Add(msg);
        public static void Reset() { Infos.Clear(); Warnings.Clear(); }
    }
}

namespace MegaCrit.Sts2.Core.Platform.Steam
{
    public static class SteamInitializer
    {
        public static bool Initialized { get; set; }
    }
}

namespace MegaCrit.Sts2.Core.Multiplayer.Game
{
    public interface INetGameService { }
    public interface INetHostGameService : INetGameService
    {
        object? NetHost { get; }
    }
}

namespace MegaCrit.Sts2.Core.Multiplayer.Transport.Steam
{
    public sealed class SteamHost
    {
        public CSteamID? LobbyId { get; set; }
    }
}

namespace Steamworks
{
    [Flags]
    public enum EFriendFlags
    {
        k_EFriendFlagImmediate = 0x04
    }

    public enum EPersonaState
    {
        k_EPersonaStateOffline = 0,
        k_EPersonaStateOnline = 1,
        k_EPersonaStateBusy = 2,
        k_EPersonaStateAway = 3,
        k_EPersonaStateSnooze = 4
    }

    public enum EActivateGameOverlayToWebPageMode
    {
        k_EActivateGameOverlayToWebPageMode_Default = 0,
        k_EActivateGameOverlayToWebPageMode_Modal = 1
    }

    public static class SteamUtils
    {
        public static bool OverlayEnabled = true;
        public static bool IsOverlayEnabled() => OverlayEnabled;
    }

    public struct CSteamID
    {
        public ulong m_SteamID;
        public CSteamID(ulong id) => m_SteamID = id;
    }

    public struct CGameID
    {
        public ulong m_GameID;
    }

    public struct FriendGameInfo_t
    {
        public CGameID m_gameID;
    }

    public sealed record FakeSteamFriend(ulong SteamId, string Name, ulong GameId, EPersonaState State);

    public static class SteamUser
    {
        public static ulong LocalSteamId = 76561198000000002;
        public static CSteamID GetSteamID() => new(LocalSteamId);
    }

    public static class SteamFriends
    {
        public static readonly List<FakeSteamFriend> Friends = new();
        public static readonly List<string> OpenedWebPages = new();
        public static string LocalPersonaName = "Host";
        public static bool ThrowOnEnumerate;
        public static int GetFriendCountCalls;

        public static void Reset()
        {
            Friends.Clear();
            OpenedWebPages.Clear();
            LocalPersonaName = "Host";
            ThrowOnEnumerate = false;
            GetFriendCountCalls = 0;
        }

        public static void ActivateGameOverlayToWebPage(string url, EActivateGameOverlayToWebPageMode mode) =>
            OpenedWebPages.Add(url);

        public static string GetPersonaName() => LocalPersonaName;

        public static int GetFriendCount(EFriendFlags flags)
        {
            GetFriendCountCalls++;
            if (ThrowOnEnumerate)
                throw new InvalidOperationException("Simulated SteamFriends failure");
            return Friends.Count;
        }

        public static CSteamID GetFriendByIndex(int i, EFriendFlags flags) => new(Friends[i].SteamId);

        public static string GetFriendPersonaName(CSteamID id) =>
            Friends.Find(f => f.SteamId == id.m_SteamID)?.Name ?? "";

        public static bool GetFriendGamePlayed(CSteamID id, out FriendGameInfo_t info)
        {
            var f = Friends.Find(x => x.SteamId == id.m_SteamID);
            if (f != null && f.GameId != 0)
            {
                info = new FriendGameInfo_t { m_gameID = new CGameID { m_GameID = f.GameId } };
                return true;
            }
            info = default;
            return false;
        }

        public static EPersonaState GetFriendPersonaState(CSteamID id) =>
            Friends.Find(f => f.SteamId == id.m_SteamID)?.State ?? EPersonaState.k_EPersonaStateOffline;
    }

    public static class SteamMatchmaking
    {
        public static readonly List<(ulong LobbyId, ulong GuestId)> Invites = new();
        public static bool NextInviteResult = true;
        public static bool ThrowOnInvite;

        public static void Reset()
        {
            Invites.Clear();
            NextInviteResult = true;
            ThrowOnInvite = false;
        }

        public static bool InviteUserToLobby(CSteamID lobbyId, CSteamID invitee)
        {
            if (ThrowOnInvite)
                throw new InvalidOperationException("Simulated SteamMatchmaking failure");
            Invites.Add((lobbyId.m_SteamID, invitee.m_SteamID));
            return NextInviteResult;
        }
    }
}

using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace MidRunJoin;

/// <summary>
/// Prevents post-run settlement and Run History crashes for mid-run joining players.
/// Earlier floors visited before a guest joined do not contain that guest's NetId in
/// <see cref="MapPointHistoryEntry.PlayerStats"/>. Vanilla post-run code (such as
/// <c>NGameOverScreen.AnimateScoreLines</c>, <c>ProgressSaveManager.UpdateProgressWithRunData</c>,
/// <c>MetricUtilities</c>, <c>NMapPointHistoryEntry</c>, and <c>NMapPointHistoryHoverTip</c>)
/// iterates every historical floor and throws <c>InvalidOperationException</c> when a player
/// is absent, leaving late-joining players stuck on the Game Over / Victory summary screen
/// before the Return to Main Menu button is enabled.
///
/// Returning a detached empty <see cref="PlayerMapPointHistoryEntry"/> preserves the original
/// historical floor data without mutating <see cref="MapPointHistoryEntry.PlayerStats"/>.
/// </summary>
[HarmonyPatch]
internal static class HistorySafety
{
    [HarmonyPrefix, HarmonyPatch(typeof(MapPointHistoryEntry), nameof(MapPointHistoryEntry.GetEntry))]
    private static bool SafeGetEntry(
        MapPointHistoryEntry __instance,
        ulong playerId,
        ref PlayerMapPointHistoryEntry __result)
    {
        var existing = __instance.PlayerStats.FirstOrDefault(e => e.PlayerId == playerId);
        if (existing != null)
        {
            __result = existing;
            return false;
        }
        __result = new PlayerMapPointHistoryEntry { PlayerId = playerId };
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(NMapPointHistoryHoverTip), nameof(NMapPointHistoryHoverTip.Create))]
    private static void EnsureHoverTipHasPlayerEntry(ulong playerId, ref MapPointHistoryEntry historyEntry)
    {
        if (historyEntry == null || historyEntry.PlayerStats.Any(e => e.PlayerId == playerId))
            return;

        var safeCopy = new MapPointHistoryEntry
        {
            MapPointType = historyEntry.MapPointType,
            Rooms = historyEntry.Rooms,
            PlayerStats = [.. historyEntry.PlayerStats, new PlayerMapPointHistoryEntry { PlayerId = playerId }]
        };
        historyEntry = safeCopy;
    }
}

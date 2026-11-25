using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace MidRunJoin;

/// <summary>
/// Prevents Rest Site rooms from breaking with 5+ players (when paired with a player-limit-break mod
/// that does not patch <see cref="NRestSiteRoom._Ready"/>).
/// Vanilla <see cref="NRestSiteRoom._Ready"/> hardcodes 4 character slot containers
/// (<c>BgContainer/Character_1..4</c>) and throws <see cref="ArgumentOutOfRangeException"/> on the
/// 5th player BEFORE <c>UpdateRestSiteOptions()</c> runs, leaving all Rest Site options blank.
///
/// Using a Harmony Finalizer guarantees zero interference when a limit-break mod already handles
/// 5+ slots, while seamlessly creating extra containers and completing room initialization if the
/// vanilla 4-slot cap throws.
/// </summary>
[HarmonyPatch]
internal static class RestSiteSafety
{
    private static readonly PropertyInfo? BgContainerProp =
        AccessTools.Property(typeof(NRestSiteRoom), "BgContainer");
    private static readonly PropertyInfo? DescriptionProp =
        AccessTools.Property(typeof(NRestSiteRoom), "Description");
    private static readonly MethodInfo? UpdateRestSiteOptionsMethod =
        AccessTools.Method(typeof(NRestSiteRoom), "UpdateRestSiteOptions");
    private static readonly MethodInfo? ShowFtueIfNeededMethod =
        AccessTools.Method(typeof(NRestSiteRoom), "ShowFtueIfNeeded");
    private static readonly MethodInfo? OnHoverChangedMethod =
        AccessTools.Method(typeof(NRestSiteRoom), "OnPlayerChangedHoveredRestSiteOption", [typeof(ulong)]);
    private static readonly MethodInfo? OnBeforeSelectedMethod =
        AccessTools.Method(typeof(NRestSiteRoom), "OnBeforePlayerSelectedRestSiteOption", [typeof(RestSiteOption), typeof(ulong)]);
    private static readonly MethodInfo? OnAfterSelectedMethod =
        AccessTools.Method(typeof(NRestSiteRoom), "OnAfterPlayerSelectedRestSiteOption", [typeof(RestSiteOption), typeof(bool), typeof(ulong)]);

    [HarmonyFinalizer, HarmonyPatch(typeof(NRestSiteRoom), nameof(NRestSiteRoom._Ready))]
    private static Exception? RecoverExtraRestSiteSlots(
        NRestSiteRoom __instance,
        Exception? __exception,
        IRunState? ____runState,
        List<Control>? ____characterContainers,
        List<NRestSiteCharacter>? ___characterAnims,
        ref float ____originalDescriptionYPos)
    {
        if (__exception is not ArgumentOutOfRangeException ||
            ____runState == null ||
            ____characterContainers == null ||
            ____characterContainers.Count == 0 ||
            ____runState.Players.Count <= ____characterContainers.Count)
        {
            return __exception;
        }

        try
        {
            int originalSlots = ____characterContainers.Count;
            int totalPlayers = ____runState.Players.Count;
            Control? bgContainer = BgContainerProp?.GetValue(__instance) as Control;

            for (int slot = originalSlots; slot < totalPlayers; slot++)
            {
                var template = ____characterContainers[slot % originalSlots];
                int ring = slot / originalSlots;
                float xOffset = (slot % 2 == 0 ? -1f : 1f) * 68f * ring;
                float yOffset = -18f * ring;

                var extraSlot = new Control
                {
                    Name = $"Character_{slot + 1}",
                    Position = template.Position + new Vector2(xOffset, yOffset),
                    Size = template.Size
                };
                if (bgContainer != null)
                    bgContainer.AddChildSafely(extraSlot);
                else
                    __instance.AddChildSafely(extraSlot);
                ____characterContainers.Add(extraSlot);
            }

            int startIdx = __instance.Characters.Count;
            for (int i = startIdx; i < totalPlayers; i++)
            {
                NRestSiteCharacter anim;
                if (___characterAnims != null && i < ___characterAnims.Count)
                {
                    anim = ___characterAnims[i];
                }
                else
                {
                    anim = NRestSiteCharacter.Create(____runState.Players[i], i);
                    ___characterAnims?.Add(anim);
                }

                ____characterContainers[i].AddChildSafely(anim);
                anim.Position = Vector2.Zero;
                if (i % 2 == 1)
                    anim.FlipX();
                __instance.Characters.Add(anim);
            }

            if (DescriptionProp?.GetValue(__instance) is Control descControl)
                ____originalDescriptionYPos = descControl.Position.Y;

            UpdateRestSiteOptionsMethod?.Invoke(__instance, null);

            if (ShowFtueIfNeededMethod?.Invoke(__instance, null) is Task ftueTask)
                TaskHelper.RunSafely(ftueTask);

            var sync = RunManager.Instance?.RestSiteSynchronizer;
            if (sync != null)
            {
                if (OnHoverChangedMethod != null)
                    sync.PlayerHoverChanged += (Action<ulong>)Delegate.CreateDelegate(typeof(Action<ulong>), __instance, OnHoverChangedMethod);
                if (OnBeforeSelectedMethod != null)
                    sync.BeforePlayerOptionChosen += (Action<RestSiteOption, ulong>)Delegate.CreateDelegate(typeof(Action<RestSiteOption, ulong>), __instance, OnBeforeSelectedMethod);
                if (OnAfterSelectedMethod != null)
                    sync.AfterPlayerOptionChosen += (Action<RestSiteOption, bool, ulong>)Delegate.CreateDelegate(typeof(Action<RestSiteOption, bool, ulong>), __instance, OnAfterSelectedMethod);
            }

            Log.Info($"[MidRunJoin] Recovered NRestSiteRoom._Ready for {totalPlayers} players (added {totalPlayers - originalSlots} extra slot(s)).");
            return null;
        }
        catch (Exception recoveryEx)
        {
            Log.Error($"[MidRunJoin] Failed to recover NRestSiteRoom._Ready for 5+ players: {recoveryEx}");
            return __exception;
        }
    }
}

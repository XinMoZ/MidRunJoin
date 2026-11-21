using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace MidRunJoin;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public static void Init()
    {
        new HarmonyLib.Harmony("xinmo.MidRunJoin").PatchAll(typeof(Entry).Assembly);
        Log.Info("[MidRunJoin] Checkpoint lobby enabled.");
    }
}


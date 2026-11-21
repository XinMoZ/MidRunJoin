using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace MidRunJoin;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public static void Init()
    {
        Log.Info("[MidRunJoin] Project skeleton loaded.");
    }
}

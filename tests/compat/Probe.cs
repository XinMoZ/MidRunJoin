using Godot;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

public partial class Probe : Node
{
    public override void _Ready()
    {
        try
        {
            var args = OS.GetCmdlineUserArgs();
            if (args.Length != 2)
                throw new ArgumentException("Expected <game-data-dir> <mod-dll-path>");
            string data = Path.GetFullPath(args[0]);
            string modPath = Path.GetFullPath(args[1]);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                string path = Path.Combine(data, name.Name + ".dll");
                return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
            };
            RunProbe(data, modPath);
            GetTree().Quit(0);
        }
        catch (Exception ex)
        {
            GD.PrintErr("COMPAT FAIL: " + ex);
            GetTree().Quit(1);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunProbe(string data, string modPath)
    {
        var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(data, "sts2.dll"));
        var version = game.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        GD.Print($"GAME: {version}");
        var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);

        // 1. Verify critical game API surface that MidRunJoin depends on
        VerifyTypeAndMember(game, "MegaCrit.Sts2.Core.Runs.RunState", "AddPlayerDebug");
        VerifyTypeAndMember(game, "MegaCrit.Sts2.Core.Entities.Players.Player", "CreateForNewRun");
        VerifyTypeAndMember(game, "MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager", "ApplyEffectsTo");
        VerifyTypeAndMember(game, "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu", "StartHost");
        VerifyTypeAndMember(game, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NMultiplayerLoadGameScreen", "InitializeAsHost");
        VerifyTypeAndMember(game, "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby", "IsAboutToBeginGame");
        VerifyTypeAndMember(game, "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby", "IsPlayerReady");
        VerifyTypeAndProperty(game, "MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamHost", "LobbyId");
        VerifyTypeAndProperty(game, "MegaCrit.Sts2.Core.Platform.Steam.SteamInitializer", "Initialized");
        VerifyTypeAndMember(game, "MegaCrit.Sts2.Core.Nodes.NGame", "LoadRun");
        VerifyTypeAndMember(game, "MegaCrit.Sts2.Core.Commands.RewardsCmd", "OfferCustom");
        _ = game.GetType("MegaCrit.Sts2.Core.Rewards.GoldReward", true)!;
        _ = game.GetType("MegaCrit.Sts2.Core.Rewards.CardReward", true)!;
        _ = game.GetType("MegaCrit.Sts2.Core.Rewards.RelicReward", true)!;
        _ = game.GetType("MegaCrit.Sts2.Core.Rewards.CardRemovalReward", true)!;
        GD.Print("API SURFACE PASS: all 15 critical game APIs present");

        // 2. Prepare every method body in MidRunJoin.dll (catches missing members / type-load errors)
        int prepared = 0;
        foreach (var type in mod.GetTypes())
        {
            var members = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static));
            foreach (var method in members)
            {
                if (method.ContainsGenericParameters || method.IsAbstract || method.GetMethodBody() == null)
                    continue;
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    prepared++;
                }
                catch (Exception ex)
                {
                    throw new Exception($"Method binding failed: {type.FullName}.{method.Name}", ex);
                }
            }
        }
        GD.Print($"JIT PREPARE PASS: {prepared} method bodies prepared");

        // 3. Install all Harmony patches and verify expected targets were patched
        var harmonyAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(data, "0Harmony.dll"));
        var harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", true)!;
        object harmony = Activator.CreateInstance(harmonyType, "xinmo.midrunjoin.compat-probe")!;
        harmonyType.GetMethod("PatchAll", [typeof(Assembly)])!.Invoke(harmony, [mod]);

        var patched = ((IEnumerable<MethodBase>)harmonyType.GetMethod("GetPatchedMethods")!.Invoke(harmony, null)!).ToArray();
        var patchedNames = patched.Select(m => $"{m.DeclaringType?.Name}.{m.Name}").OrderBy(x => x).ToArray();
        string[] expected =
        [
            "LoadRunLobby.IsAboutToBeginGame",
            "MapPointHistoryEntry.GetEntry",
            "NCustomRunLoadScreen.InitializeAsHost",
            "NGame.LoadRun",
            "NMapPointHistoryHoverTip.Create",
            "NMultiplayerLoadGameScreen.InitializeAsHost",
            "NMultiplayerSubmenu._Ready"
        ];
        foreach (string exp in expected)
        {
            if (!patchedNames.Contains(exp))
                throw new Exception($"Expected Harmony patch target missing: {exp}. Actual: {string.Join(", ", patchedNames)}");
        }
        GD.Print($"HARMONY PASS: {patchedNames.Length} targets patched ({string.Join(", ", patchedNames)})");
        GD.Print($"COMPAT PASS: {prepared} method bodies prepared; all Harmony patches verified; {Path.GetFileName(modPath)}");
    }

    private static void VerifyTypeAndMember(Assembly game, string typeName, string methodName)
    {
        var t = game.GetType(typeName, true)!;
        if (t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).All(m => m.Name != methodName))
            throw new Exception($"Missing method {typeName}.{methodName}");
    }

    private static void VerifyTypeAndProperty(Assembly game, string typeName, string propName)
    {
        var t = game.GetType(typeName, true)!;
        if (t.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static) == null)
            throw new Exception($"Missing property {typeName}.{propName}");
    }
}

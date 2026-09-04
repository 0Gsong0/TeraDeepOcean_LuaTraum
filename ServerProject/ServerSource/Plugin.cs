using HarmonyLib;

namespace TeraDeepOcean
{
    //服务器
    public partial class Plugin : IAssemblyPlugin
    {
        // Server-specific code
        partial void PreInitSever(Harmony harmony)
        {
            if (GameMain.NetworkMember is { IsServer: true })
            {
                TLConfigService.LoadLocal();
            }
            var roundStarted = AccessTools.Method(typeof(GameSession), nameof(GameSession.StartRound), new[]
            {
                typeof(LevelData),
                typeof(bool),
                typeof(SubmarineInfo),
                typeof(SubmarineInfo),
            });
            if (roundStarted != null)
            {
                harmony.Patch(roundStarted, prefix:new HarmonyMethod(typeof(Plugin),nameof(OnRoundStarted)));
            }
        }
        private static void OnRoundStarted()
        {
            if (GameMain.NetworkMember is not { IsServer: true }) return;
            TLConfigService.LoadLocal();

            TLConfigBridge.SendUpdateToClient(null, TLConfigService.ToJson());
        }
    }
}

using Barotrauma.Networking;
using HarmonyLib;

namespace TeraDeepOcean
{
    //客户端
    public partial class Plugin : IAssemblyPlugin
    {
        // Client-specific code
        static bool LuaCsLoaded;
        static bool RequestedConfig;
        partial void InitializeClient(Harmony harmony)
        {
            var update = AccessTools.Method(typeof(GUI), nameof(GUI.Update));
            if (update != null)
            {
                harmony.Patch(update, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnUpdate)));
            }
            //无法使用，客户端模组初始化时机太晚
            //var clientConnected = AccessTools.Method(typeof(GameClient), "OnConnectionInitializationComplete");
            //if(clientConnected != null)
            //{
            //    harmony.Patch(clientConnected, postfix: new HarmonyMethod(typeof(Plugin), nameof(Plugin.OnClientConnected)));
            //}
            var roundStarted = AccessTools.Method(typeof(GameSession), nameof(GameSession.StartRound), new[]
            {
                typeof(LevelData),
                typeof(bool),
                typeof(SubmarineInfo),
                typeof(SubmarineInfo),
            });
            if (roundStarted != null)
            {
                harmony.Patch(roundStarted, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnRoundStarted)));
            }
        }
        private static void OnUpdate(float deltaTime)
        {
            TLVideoPlayer.Update();
            TLConfigMenu.Update(deltaTime);
            WaitLuaCsLoad();
            OnJoinSeverRequestConfig();
        }
        private static void WaitLuaCsLoad()
        {
            if (LuaCsLoaded) return;
            var lua = LuaCsSetup.Instance?.Lua;
            if (lua == null) { return; }
            var value = lua.Globals.Get("TLConfigNet");
            if (value == null) { return; }
            LuaCsLoaded = true;
        }
        private static void OnJoinSeverRequestConfig()
        {
            if (RequestedConfig) return;
            bool inLobby =
                GameMain.NetworkMember != null &&
                GameMain.NetLobbyScreen != null &&//大厅界面已经建立
                !GameMain.Instance.LoadingScreenOpen &&//没有卡在加载界面
                 Screen.Selected == GameMain.NetLobbyScreen &&//当前屏幕停在大厅
                !GameMain.Client.GameStarted;//还没进开局后的战局
            if (inLobby && LuaCsLoaded)
            {
                DebugConsole.NewMessage("[TeraDeepOcean]已连接到服务器，正在请求TeraDeepOcean数据");
                TLConfigBridge.ClientRequestUpdate();
                RequestedConfig = true;
                DebugConsole.NewMessage("[TeraDeepOcean]请求成功，已写入内存");
            }
        }
        private static void OnRoundStarted()
        {
            TLConfigBridge.ClientRequestUpdate();
        }
    }
}

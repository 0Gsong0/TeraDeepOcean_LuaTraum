namespace TeraDeepOcean;

using Barotrauma;
using Barotrauma.Plugins;
using HarmonyLib;
using Microsoft.Xna.Framework;
using System.Net;

public partial class Plugin : IBarotraumaPlugin
{
    public static readonly IDebugConsole DebugConsole = PluginServiceProvider.GetService<IDebugConsole>();
    public static IHarmonyProvider HarmonyProvider = PluginServiceProvider.GetService<IHarmonyProvider>();
    public static IGameNetwork Network = PluginServiceProvider.GetService<IGameNetwork>();
    public static string ModDir { get; private set; } = string.Empty;
    private ContentPackage contentPackage = null!;
    public void Init()
    {
        DebugConsole.NewMessage("TeraDeepOcean Plugin Init loaded", Color.Lime);
        var packageService = PluginServiceProvider.GetService<IContentPackageRetrievalService>();
        contentPackage = packageService.ContentPackage;
        ModDir = contentPackage.Dir;

        PluginServiceProvider.GetService<IItemComponentRegistrar>().RegisterAllItemComponents();

        Network.RegisterNetworkHeaders<TLNetworkHeader>();
        TLConfigBridge.RegisterHandlers();
        TLLinkedMotionSystem.RegisterHandlers();
        TLCybNoticeBridge.RegisterHandlers();

#if SERVER
        // 服务器读取自己的配置文件。
        TLConfigService.LoadLocal();
#elif CLIENT
        // 单机读取本地配置；多人等待服务器下发。
        if (!GameMain.IsMultiplayer)
        {
            TLConfigService.LoadLocal();
        }
#endif

        Harmony harmony = HarmonyProvider.GetHarmony();
        TLCharacterControlSystem.Init(harmony);
        TLCybInstallSystem.Reset();
        TLCybDamageSystem.Reset();
        TLCybInstallSystem.Init(harmony);
        TLCybDamageSystem.Init(harmony);
        TLCybRepairSystem.Init(harmony);
        TLRtsSystem.Init(harmony);
        TLRtsFriendlyPolicy.Init(harmony);
        TLRtsNetwork.Init();
        TLRtsDeploymentCostSystem.Init();
        TLRtsDeploymentBridge.Init();
        harmony.Patch(AccessTools.Method(typeof(GameSession), nameof(GameSession.Update), new[] { typeof(float) }),postfix: new HarmonyMethod(typeof(Plugin), nameof(UpdateRts)));

        InitProjectSpecific();
    }

    public partial void InitProjectSpecific();
    partial void DisposeProjectSpecific();

    private static void UpdateRts(float deltaTime)
    {
        TLLinkedMotionSystem.Update(deltaTime);
        TLRtsSystem.Update(deltaTime);
        TLRtsNetwork.Update(deltaTime);
    }

    public void Dispose()
    {
        DisposeProjectSpecific();
        TLLinkedMotionSystem.Clear();
        TLCybInstallSystem.Reset();
        TLCybDamageSystem.Reset();
        TLRtsSystem.Dispose();
        TLRtsNetwork.Dispose();
        TLRtsDeploymentBridge.Dispose();
        TLRtsDeploymentCostSystem.Dispose();
        TLRtsDeploymentRegistry.Clear();
        DebugConsole.NewMessage("TeraDeepOcean Plugin unloaded", Color.Red);
    }

    public void OnContentLoaded()
    {
        TLRtsDeploymentCatalog.RegisterDefaults(contentPackage);
        DebugConsole.NewMessage("TeraDeepOcean Plugin OnContentLoaded loaded", Color.Lime);
    }
}

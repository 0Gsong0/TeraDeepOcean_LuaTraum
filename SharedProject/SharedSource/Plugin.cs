using HarmonyLib;

namespace TeraDeepOcean
{
    //共享
    public partial class Plugin : IAssemblyPlugin
    {
        // These are automatically assigned by the plugin service after the Constructor is called
        public IConfigService ConfigService { get; set; }
        public IPluginManagementService PluginService { get; set; }
        public ILoggerService LoggerService { get; set; }
        public static ILoggerService? LogDebug { get; private set; }
        public static string ModDir { get; private set; }
        public static Harmony? harmony;
        partial void PreInitSever(Harmony harmony);
        public void PreInitPatching()
        {
            //Called right after the constructor
            LogDebug = LoggerService;
            harmony = new Harmony("TeraDeepOcean_harmonyPath");
            if (PluginService.TryGetPackageForPlugin<Plugin>(out var package))
            {
                ModDir = package.Dir;
            }
            if (!GameMain.IsMultiplayer)
            {
                TLConfigService.LoadLocal();
            }
            PreInitSever(harmony);
            InitializeClient(harmony);

            TLCybInstallSystem.Init(harmony);
            TLCybDamageSystem.Init(harmony);
            TLCybRepairSystem.Init(harmony);
            TLCharacterControlSystem.Init(harmony);
        }
        partial void InitializeClient(Harmony harmony);
        public void Initialize()
        {
            // When your plugin is loading, use this instead of the constructor for code relying on
            // the services above.
            
            // Put any code here that does not rely on other plugins.
            LoggerService.Log($"TeraDeepOcean Plugin Initialized. Welcome to modding!");
        }

        public void OnLoadCompleted()
        {
            // After all plugins have loaded
            // Put code that interacts with other plugins here.
        }

        public void Dispose()
        {
            // Cleanup your plugin!
            if(harmony != null)
            {
                harmony.UnpatchSelf();
                harmony = null;
            }
        }
    }
}

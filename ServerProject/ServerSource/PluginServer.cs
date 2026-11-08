using Barotrauma;
using Barotrauma.Plugins;

namespace TeraDeepOcean;

public partial class Plugin
{
    public partial void InitProjectSpecific()
    {
        TLAmmoStackSync.Init(HarmonyProvider.GetHarmony());
        TestCommand.Init();
    }
    partial void DisposeProjectSpecific()
    {
        TestCommand.Dispose();
        TLAmmoStackSync.Dispose();
    }
}
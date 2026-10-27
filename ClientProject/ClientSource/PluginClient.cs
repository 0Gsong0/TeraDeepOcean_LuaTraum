using Barotrauma;
using Barotrauma.Plugins;

namespace TeraDeepOcean;

public partial class Plugin
{
    public partial void InitProjectSpecific()
    {
        TLConfigMenuEntry.Init(HarmonyProvider.GetHarmony());
        TLRtsClient.Init(HarmonyProvider.GetHarmony());
        TLRotationPivotPicker.Init(HarmonyProvider.GetHarmony());
    }

    partial void DisposeProjectSpecific()
    {
        TLRtsClient.Dispose();
        TLConfigMenuEntry.Dispose();
    }
}

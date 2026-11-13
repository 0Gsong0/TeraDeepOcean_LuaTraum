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
        TLAmmoStackDraw.Init(HarmonyProvider);
        TLAmmoSplitDrag.Init(HarmonyProvider.GetHarmony());
        InitCommand();
    }

    partial void DisposeProjectSpecific()
    {
        TLAmmoSplitDrag.Dispose();
        TLRtsClient.Dispose();
        TLConfigMenuEntry.Dispose();
    }
    private static void InitCommand()
    {
        Plugin.DebugConsole.RegisterCommand(
            command: "tl_setammocount",
            helpMessage: "列出散弹堆，或用 tl_setammocount <物品ID> <发数> 修改数量。",
            flags: CommandFlags.None,
            onCommandExecuted: static _ => Plugin.DebugConsole.NewMessage("此命令需要在联机回合中由服务器执行。"));
    }
}

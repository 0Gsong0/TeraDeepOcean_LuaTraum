param([switch]$Server)

# 检查实际游戏程序集中的网络编码及方法绑定，不启动窗口或写入游戏目录。
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$side = if ($Server) { 'Server' } else { 'Client' }
$pluginPath = Join-Path $projectRoot "$($side)Project\obj\Windows\Release\net8.0\TeraDeepOcean$side.dll"

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

public static class ElevatorUiChecks
{
    // 使用实际游戏运行库验证本次编译的插件。
    public static void Run(string pluginPath, bool server)
    {
        string directory = @"D:\APP\Steam\steamapps\common\Barotrauma";
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            Path.Combine(directory, server ? "DedicatedServer.dll" : "Barotrauma.dll"));
        var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
        var contract = game.GetType("Barotrauma.INetSerializableStruct", true);
        var writerType = game.GetType("Barotrauma.Networking.WriteOnlyMessage", true);
        var readerType = game.GetType("Barotrauma.Networking.ReadOnlyMessage", true);
        var write = contract.GetMethod("Write");
        var read = contract.GetMethod("ReadGeneric", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var requestType = plugin.GetType("TeraDeepOcean.TLElevatorPanelRequest", true);
        var replyType = plugin.GetType("TeraDeepOcean.TLElevatorPanelReplyMessage", true);
        foreach (object source in new[] {
            Activator.CreateInstance(requestType, 42u, (ushort)123, true, false, -99),
            Activator.CreateInstance(requestType, 43u, (ushort)124, false, true, 99),
            Activator.CreateInstance(replyType, "{\"Error\":\"测试：外呼与选层\",\"Floors\":[-1,0,2],\"RequestedFloors\":[2]}")
        })
        {
            object writer = Activator.CreateInstance(writerType);
            write.Invoke(source, new[] { writer });
            byte[] bytes = (byte[])writerType.GetProperty("Buffer").GetValue(writer);
            int length = (int)writerType.GetProperty("LengthBytes").GetValue(writer);
            object reader = Activator.CreateInstance(readerType, bytes, false, 0, length, null);
            object restored = read.Invoke(null, new[] { source.GetType(), reader });
            if (!source.Equals(restored)) throw new Exception("Network codec mismatch");
            Console.WriteLine("PASS codec: " + source.GetType().Name + " (" + length + " bytes)");
        }

        var header = plugin.GetType("TeraDeepOcean.TLNetworkHeader", true);
        int[] values = Enum.GetValues(header).Cast<object>().Select(Convert.ToInt32).ToArray();
        if (values.Distinct().Count() != values.Length) throw new Exception("Duplicate network headers");
        Console.WriteLine("PASS network header uniqueness");

        // 实际方法 JIT 可发现引用 DLL 和运行时 DLL 的签名差异。
        string[] names = server
            ? new[] { "TLElevatorUiNetwork", "TLElevatorCallPanelComponent", "TLElevatorControllerComponent" }
            : new[] { "TLElevatorUiNetwork", "TLElevatorPanelUI", "TLElevatorCallPanelComponent", "TLElevatorControllerComponent" };
        foreach (string name in names)
        {
            var type = plugin.GetType("TeraDeepOcean." + name, true);
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!method.ContainsGenericParameters) RuntimeHelpers.PrepareMethod(method.MethodHandle);
            foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                RuntimeHelpers.PrepareMethod(constructor.MethodHandle);
            Console.WriteLine("PASS runtime method binding: " + name);
        }
    }
}
'@

[ElevatorUiChecks]::Run($pluginPath, $Server.IsPresent)

# XML 中两个组件都必须可选择，并且有自己的 GUI 容器。
[xml]$items = Get-Content -Raw (Join-Path $projectRoot 'ContentPackageBuilder\Content\Items\Sundrys\Sundrys.xml')
foreach ($id in @('tl_elevator_controller', 'tl_elevator_call_panel')) {
    $item = $items.SelectSingleNode("/Items/Item[@identifier='$id']")
    $component = $item.SelectSingleNode('TLElevatorControllerComponent | TLElevatorCallPanelComponent')
    if ($null -eq $component -or $component.canbeselected -ne 'true' -or $null -eq $component.GuiFrame) {
        throw "Missing interaction configuration: $id"
    }
    Write-Output "PASS XML interaction: $id"
}

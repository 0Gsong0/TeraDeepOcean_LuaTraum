param([switch]$Server)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

public static class RtsCodecCheck
{
    public static void Run(string gameDirectory, string pluginPath)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(gameDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        bool isServer = pluginPath.Contains("Server.dll");
        var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gameDirectory, isServer ? "DedicatedServer.dll" : "Barotrauma.dll"));
        var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
        var contract = game.GetType("Barotrauma.INetSerializableStruct", true);
        var writerType = game.GetType("Barotrauma.Networking.WriteOnlyMessage", true);
        var readerType = game.GetType("Barotrauma.Networking.ReadOnlyMessage", true);
        var read = contract.GetMethod("ReadGeneric", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var write = contract.GetMethod("Write");
        foreach (var name in new[] { "TLRtsJsonMessage", "TLRtsEmptyMessage" })
        {
            var type = plugin.GetType("TeraDeepOcean." + name, true);
            foreach (string json in new[] { "{\"Reason\":\"测试：指挥官\\n部署\",\"Sequence\":42}", new string('测', 16000) })
            {
                object source = name == "TLRtsEmptyMessage" ? Activator.CreateInstance(type) : Activator.CreateInstance(type, json);
                object writer = Activator.CreateInstance(writerType);
                write.Invoke(source, new[] { writer });
                byte[] buffer = (byte[])writerType.GetProperty("Buffer").GetValue(writer);
                int length = (int)writerType.GetProperty("LengthBytes").GetValue(writer);
                object reader = Activator.CreateInstance(readerType, buffer, false, 0, length, null);
                object restored = read.Invoke(null, new[] { type, reader });
                if (!source.Equals(restored)) throw new Exception(name + " round-trip mismatch");
                Console.WriteLine(name + " actual game codec round-trip PASS (" + length + " bytes)");
            }
        }
        var header = plugin.GetType("TeraDeepOcean.TLNetworkHeader", true);
        var values = Enum.GetValues(header).Cast<object>().Select(Convert.ToInt32).ToArray();
        if (values.Distinct().Count() != values.Length) throw new Exception("Duplicate network header");
        Console.WriteLine("Network header uniqueness PASS (" + values.Length + " headers)");
        var harmonyAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gameDirectory, "0Harmony.dll"));
        var harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", true);
        object harmony = Activator.CreateInstance(harmonyType, "TeraDeepOcean.RtsMigrationValidation");
        try
        {
            plugin.GetType("TeraDeepOcean.TLRtsSystem", true).GetMethod("Init").Invoke(null, new[] { harmony });
            var methods = (System.Collections.IEnumerable)harmonyType.GetMethod("GetPatchedMethods").Invoke(harmony, null);
            int count = 0;
            foreach (var method in methods) { Console.WriteLine("RTS patch installed: " + method); count++; }
            if (count != 7) throw new Exception("Expected 7 RTS core patch targets, got " + count);
            Console.WriteLine("RTS Harmony installation against actual game assembly PASS");
            if (!isServer)
            {
                plugin.GetType("TeraDeepOcean.TLRtsClient", true).GetMethod("Init").Invoke(null, new[] { harmony });
                methods = (System.Collections.IEnumerable)harmonyType.GetMethod("GetPatchedMethods").Invoke(harmony, null);
                count = 0;
                foreach (var method in methods) count++;
                if (count != 10) throw new Exception("Expected 10 core + client patch targets, got " + count);
                Console.WriteLine("RTS GUI Update/Draw/AddToGUIUpdateList Harmony installation PASS");
            }
        }
        finally
        {
            harmonyType.GetMethod("UnpatchSelf").Invoke(harmony, null);
        }

        var definitionType = plugin.GetType("TeraDeepOcean.TLRtsDeploymentPrefabDefinition", true);
        var definition = Activator.CreateInstance(definitionType);
        definitionType.GetProperty("Identifier").SetValue(definition, "test:codec_crawler");
        definitionType.GetProperty("DisplayName").SetValue(definition, "测试部署");
        definitionType.GetProperty("UnitType").SetValue(definition, Enum.ToObject(plugin.GetType("TeraDeepOcean.TLRtsDeploymentUnitType"), 1));
        definitionType.GetProperty("SpeciesIdentifier").SetValue(definition, "crawler");
        foreach (var property in new[] { "Description", "NpcIdentifier", "NpcSetIdentifier" })
            definitionType.GetProperty(property).SetValue(definition, null);
        var register = plugin.GetType("TeraDeepOcean.TLRtsDeploymentRegistry", true).GetMethod("TryRegisterPrepared", BindingFlags.Static | BindingFlags.NonPublic);
        object[] registrationArgs = { "TeraDeepOcean", definition, null };
        if (!(bool)register.Invoke(null, registrationArgs)) throw new Exception("Deployment normalization failed: " + registrationArgs[2]);
        registrationArgs[0] = "AnotherMod";
        if ((bool)register.Invoke(null, registrationArgs)) throw new Exception("Cross-package overwrite was incorrectly accepted");
        Console.WriteLine("Deployment null optional fields and source ownership checks PASS");
    }
}
'@
$target = if ($Server) { 'Server' } else { 'Client' }
[RtsCodecCheck]::Run('D:\APP\Steam\steamapps\common\Barotrauma', "E:\ModDev\TeraDeepOcean_LuaTraum\.migration-check\RTSRelease\TeraDeepOcean\Windows\$target\TeraDeepOcean$target.dll")

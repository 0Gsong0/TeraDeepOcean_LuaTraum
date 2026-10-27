param([switch]$Server)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
public static class MultiplayerMotionCheck {
 public static void Run(bool server) {
  string dir = @"D:\APP\Steam\steamapps\common\Barotrauma";
  AssemblyLoadContext.Default.Resolving += (context, name) => {
   string p = Path.Combine(dir, name.Name + ".dll");
   return File.Exists(p) ? context.LoadFromAssemblyPath(p) : null;
  };
  string side = server ? "Server" : "Client";
  var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir, server ? "DedicatedServer.dll" : "Barotrauma.dll"));
  var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(@"E:\ModDev\TeraDeepOcean_LuaTraum\.migration-check\MultiplayerMotionFix\TeraDeepOcean\Windows\" + side + "\\TeraDeepOcean" + side + ".dll");
  var factory = plugin.GetType("TeraDeepOcean.TLRtsCharacterInfoFactory", true);
  var ctor = (ConstructorInfo)factory.GetMethod("FindConstructor", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
  if (ctor.GetParameters()[6].ParameterType.FullName.IndexOf("ValueTuple") < 0) throw new Exception("Wrong constructor resolved");
  Console.WriteLine(side + " resolved: " + ctor);
  RuntimeHelpers.PrepareMethod(factory.GetMethod("Create").MethodHandle);
  var service = plugin.GetType("TeraDeepOcean.TLRtsDeploymentService", true);
  RuntimeHelpers.PrepareMethod(service.GetMethod("TryPrepareCharacter", BindingFlags.NonPublic | BindingFlags.Static).MethodHandle);
  RuntimeHelpers.PrepareMethod(service.GetMethod("TryDeploy").MethodHandle);
  foreach (string typeName in new[] { "TLLinkedMotionSystem", "TLLinkedMoverComponent" }) {
   var motion = plugin.GetType("TeraDeepOcean." + typeName, true);
   int count = 0;
   foreach (var method in motion.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
    if (!method.ContainsGenericParameters) { RuntimeHelpers.PrepareMethod(method.MethodHandle); count++; }
   }
   Console.WriteLine(side + " " + typeName + " JIT PASS: " + count + " methods");
  }
  RuntimeHelpers.PrepareMethod(service.GetMethod("RemoveFailedDeployment", BindingFlags.NonPublic | BindingFlags.Static).MethodHandle);
  if (server) Console.WriteLine("Resolved removal API: " + service.GetField("enqueueRemoval", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null));
  Console.WriteLine(side + " deployment JIT binding PASS");
 }
}
"@
[MultiplayerMotionCheck]::Run($Server.IsPresent)

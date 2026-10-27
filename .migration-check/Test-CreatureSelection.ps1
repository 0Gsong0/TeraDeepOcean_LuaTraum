$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
public static class CreatureSelectionCheck {
 public static void Run() {
  string dir = @"D:\APP\Steam\steamapps\common\Barotrauma";
  AssemblyLoadContext.Default.Resolving += (c,n) => { string p=Path.Combine(dir,n.Name+".dll"); return File.Exists(p)?c.LoadFromAssemblyPath(p):null; };
  var game=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir,"Barotrauma.dll"));
  var plugin=AssemblyLoadContext.Default.LoadFromAssemblyPath(@"E:\ModDev\TeraDeepOcean_LuaTraum\.migration-check\RtsCreatureSelectionFix\TeraDeepOcean\Windows\Client\TeraDeepOceanClient.dll");
  var net=plugin.GetType("TeraDeepOcean.TLRtsNetwork",true);
  net.GetMethod("ClientReceiveRegistrySnapshot").Invoke(null,new object[]{"{\"Revision\":1,\"Units\":[{\"CharacterId\":500,\"CanControl\":true,\"TeamId\":1,\"UnitClass\":\"creature\"},{\"CharacterId\":501,\"CanControl\":true,\"TeamId\":2},{\"CharacterId\":502,\"CanControl\":false,\"TeamId\":1}]}"});
  object team=Enum.ToObject(game.GetType("Barotrauma.CharacterTeamType",true),1);
  var check=net.GetMethod("IsSyncedUnitControllable");
  foreach(ushort id in new ushort[]{500,501,502,503}) {
   bool result=(bool)check.Invoke(null,new object[]{id,team});
   if(result!=(id==500)) throw new Exception("Incorrect selection permission for "+id);
  }
  RuntimeHelpers.PrepareMethod(plugin.GetType("TeraDeepOcean.TLRtsClient",true).GetMethod("IsSelectable",BindingFlags.NonPublic|BindingFlags.Static).MethodHandle);
  Console.WriteLine("PASS: allied creature accepted; enemy, disabled and unregistered units rejected; selection method JIT bound to installed game.");
 }
}
"@
[CreatureSelectionCheck]::Run()
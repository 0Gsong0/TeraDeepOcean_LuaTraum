param([switch]$Server)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
public static class CyberMigrationCheck {
 public static void Run(bool server) {
  string dir=@"D:\APP\Steam\steamapps\common\Barotrauma";
  AssemblyLoadContext.Default.Resolving+=(c,n)=>{string p=Path.Combine(dir,n.Name+".dll"); return File.Exists(p)?c.LoadFromAssemblyPath(p):null;};
  string side=server?"Server":"Client";
  var game=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir,server?"DedicatedServer.dll":"Barotrauma.dll"));
  var plugin=AssemblyLoadContext.Default.LoadFromAssemblyPath(@"E:\ModDev\TeraDeepOcean_LuaTraum\.migration-check\CyberMigration\TeraDeepOcean\Windows\"+side+"\\TeraDeepOcean"+side+".dll");
  int methodCount=0;
  foreach(var t in plugin.GetTypes().Where(t=>t.Name.StartsWith("TLCyb")||t.FullName.Contains("TLCyb")||t.Name=="TLLinkedRotatorComponent")) {
   foreach(var m in t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly))
    if(!m.ContainsGenericParameters&&!m.IsAbstract) {RuntimeHelpers.PrepareMethod(m.MethodHandle); methodCount++;}
   foreach(var c in t.GetConstructors(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)) RuntimeHelpers.PrepareMethod(c.MethodHandle);
  }
  Console.WriteLine(side+" Cyber + rotator JIT PASS: "+methodCount+" methods");
  var api=plugin.GetType("TeraDeepOcean.TLCybGameApi",true);
  Console.WriteLine("Item removal resolved: "+api.GetField("removeItem",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null));
  var harmonyAssembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir,"0Harmony.dll"));
  var harmonyType=harmonyAssembly.GetType("HarmonyLib.Harmony",true);
  object harmony=Activator.CreateInstance(harmonyType,"TeraDeepOcean.CyberMigrationCheck");
  try {
   foreach(string name in new[]{"TLCybInstallSystem","TLCybDamageSystem","TLCybRepairSystem"})
    plugin.GetType("TeraDeepOcean."+name,true).GetMethod("Init").Invoke(null,new[]{harmony});
   int count=0;
   foreach(var m in (System.Collections.IEnumerable)harmonyType.GetMethod("GetPatchedMethods").Invoke(harmony,null)) {Console.WriteLine("Cyber patch installed: "+m);count++;}
   if(count!=4) throw new Exception("Expected four patched game methods, got "+count);
  } finally {harmonyType.GetMethod("UnpatchSelf").Invoke(harmony,null);}
  var state=plugin.GetType("TeraDeepOcean.TLCybState",true);
  var limb=game.GetType("Barotrauma.LimbType",true);
  foreach(var pair in new[]{new[]{"LeftHand","LeftArm"},new[]{"RightForearm","RightArm"},new[]{"LeftThigh","LeftLeg"},new[]{"RightFoot","RightLeg"}}) {
   object result=state.GetMethod("NormalizeLimbType").Invoke(null,new[]{Enum.Parse(limb,pair[0])});
   if(result.ToString()!=pair[1]) throw new Exception("Wrong limb normalization");
  }
  var form=plugin.GetType("TeraDeepOcean.TLCybDataForm",true);
  if(form.GetMethod("FindByItemId").Invoke(null,new object[]{"TLCyb_CivilianArm"})==null||form.GetMethod("FindByInstallAffId").Invoke(null,new object[]{"TLCyb_CivilianLeg_Init"})==null)
   throw new Exception("Missing base cybernetic limb data");
  var contract=game.GetType("Barotrauma.INetSerializableStruct",true);
  var messageType=plugin.GetType("TeraDeepOcean.TLCybNoticeMessage",true);
  object message=Activator.CreateInstance(messageType,(ushort)500,"entityname.TLCyb_CivilianArm","测试安装提示","","TLCyb_CybBack",false);
  var wt=game.GetType("Barotrauma.Networking.WriteOnlyMessage",true);
  var rt=game.GetType("Barotrauma.Networking.ReadOnlyMessage",true);
  object writer=Activator.CreateInstance(wt); contract.GetMethod("Write").Invoke(message,new[]{writer});
  object reader=Activator.CreateInstance(rt,(byte[])wt.GetProperty("Buffer").GetValue(writer),false,0,(int)wt.GetProperty("LengthBytes").GetValue(writer),null);
  object decoded=contract.GetMethod("ReadGeneric",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new[]{messageType,reader});
  if(!message.Equals(decoded))throw new Exception("Cyber notice serialization mismatch");
  var headers=Enum.GetValues(plugin.GetType("TeraDeepOcean.TLNetworkHeader",true)).Cast<object>().Select(Convert.ToInt32).ToArray();
  if(headers.Distinct().Count()!=headers.Length)throw new Exception("Duplicate network headers");
  Console.WriteLine(side+" limb normalization, data lookup, notice serialization and header uniqueness PASS");
 }
}
"@
[CyberMigrationCheck]::Run($Server.IsPresent)
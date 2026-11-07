param([switch]$Server)

# 使用真实插件类型检查停靠状态机。替换输出末端以记录信号，不启动游戏窗口。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$side = if ($Server) { 'Server' } else { 'Client' }
$pluginPath = Join-Path $root "$($side)Project\obj\Windows\Release\net8.0\TeraDeepOcean$side.dll"
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

public static class ElevatorRuntimeTests
{
    private static Type motion, itemType, entityType, controllerType, panelType, vectorType, floorType, layoutType;
    private static object controller, group;
    private static readonly Dictionary<ushort, (bool Docked, bool Arrived)> signals = new();
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // 在真实类型层次中查找字段，支持受保护字段和自动属性存储。
    private static FieldInfo Field(Type type, string name) {
        for (; type != null; type = type.BaseType) { var f = type.GetField(name, All | BindingFlags.DeclaredOnly); if (f != null) return f; }
        throw new Exception("Missing field: " + name);
    }
    // 读取、写入真实对象的属性。
    private static object Get(object o, string p) => o.GetType().GetProperty(p, All).GetValue(o);
    private static void Set(object o, string p, object v) => o.GetType().GetProperty(p, All).SetValue(o, v);
    // 调用实际实现，不复制状态机算法。
    private static object Call(Type t, object o, string m, params object[] args) => t.GetMethod(m, All).Invoke(o, args);
    private static object V(float x, float y) => Activator.CreateInstance(vectorType, x, y);
    private static void Check(bool ok, string error) { if (!ok) throw new Exception(error); }

    // 创建最小物品并登记到真实 Entity 字典，使 IsGroupValid 使用真实身份检查。
    private static object Item(ushort id, int y) {
        object item = RuntimeHelpers.GetUninitializedObject(itemType);
        Field(entityType, "ID").SetValue(item, id);
        var r = itemType.GetProperty("Rect");
        r.SetValue(item, Activator.CreateInstance(r.PropertyType, 0, y, 0, 0));
        foreach (string name in new[] { "components", "updateableComponents" }) {
            var f = Field(itemType, name); f.SetValue(item, Activator.CreateInstance(f.FieldType));
        }
        ((IDictionary)Field(entityType, "dictionary").GetValue(null))[id] = item;
        return item;
    }

    // 创建外呼组件，物品位置就是本层基准高度。
    private static object Panel(ushort id, int floor, int y) {
        object panel = RuntimeHelpers.GetUninitializedObject(panelType);
        Field(panelType, "item").SetValue(panel, Item(id, y));
        Set(panel, "Floor", floor);
        return panel;
    }

    // 创建参数类型完全匹配的泛型列表。
    private static object ListOf(Type type, params object[] items) {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
        foreach (var item in items) list.Add(item);
        return list;
    }

    // Harmony 前缀只替换实际发线末端，记录主控要求输出的值。
    public static bool Capture(object __instance, bool docked, bool arrived) {
        ushort id = (ushort)Field(entityType, "ID").GetValue(Get(__instance, "PanelItem"));
        signals[id] = (docked, arrived);
        return false;
    }

    // 执行一次真实主控更新，清除上一周期记录。
    private static void Tick() { signals.Clear(); Call(controllerType, controller, "UpdateElevatorRuntime", 0.016f); }

    // 用指定进度和位置模拟共用运动系统已经完成的位移。
    private static void At(int y, float progress) {
        object item = Get(controller, "ControllerItem");
        var r = itemType.GetProperty("Rect");
        r.SetValue(item, Activator.CreateInstance(r.PropertyType, 0, y, 0, 0));
        Set(group, "Progress", progress);
    }

    public static void Run(string path, bool server) {
        string dir = @"D:\APP\Steam\steamapps\common\Barotrauma";
        AssemblyLoadContext.Default.Resolving += (c,n) => { string p=Path.Combine(dir,n.Name+".dll"); return File.Exists(p)?c.LoadFromAssemblyPath(p):null; };
        var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir, server?"DedicatedServer.dll":"Barotrauma.dll"));
        var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        motion=plugin.GetType("TeraDeepOcean.TLLinkedMotionSystem",true);
        itemType=game.GetType("Barotrauma.Item",true); entityType=game.GetType("Barotrauma.Entity",true);
        controllerType=plugin.GetType("TeraDeepOcean.TLElevatorControllerComponent",true);
        panelType=plugin.GetType("TeraDeepOcean.TLElevatorCallPanelComponent",true);
        var builder=plugin.GetType("TeraDeepOcean.TLElevatorLayoutBuilder",true);
        floorType=builder.GetNestedType("FloorEntry"); layoutType=builder.GetNestedType("ElevatorLayout");
        vectorType=itemType.GetProperty("Position").PropertyType;
        Call(motion,null,"Clear");
        object car=Item(100,0);
        controller=RuntimeHelpers.GetUninitializedObject(controllerType);
        Field(controllerType,"item").SetValue(controller,car);
        Set(controller,"TravelSpeed",100f); Set(controller,"StopDuration",0.1f);
        Set(controller,"DockedFloor",(int?)1); Field(controllerType,"motionReady").SetValue(controller,true);
        object p1=Panel(101,1,0), p2=Panel(102,2,100), p3=Panel(103,3,200), p3b=Panel(104,3,200);
        object Floor(int number,float y,params object[] panels) => Activator.CreateInstance(floorType,All,null,new[]{(object)number,y,ListOf(panelType,panels)},null);
        object layout=Activator.CreateInstance(layoutType,All,null,new[]{controller,ListOf(floorType,Floor(1,0,p1),Floor(2,100,p2),Floor(3,200,p3,p3b)),ListOf(game.GetType("Barotrauma.MapEntity"),car)},null);
        Set(controller,"Layout",layout);
        var queueType=plugin.GetType("TeraDeepOcean.TLElevatorRequestQueue",true);
        object queue=Activator.CreateInstance(queueType,layout);
        Field(controllerType,"requestQueue").SetValue(controller,queue);
        var groupType=motion.GetNestedType("MotionGroup",BindingFlags.NonPublic);
        var targetType=motion.GetNestedType("MotionTarget",BindingFlags.NonPublic);
        group=Activator.CreateInstance(groupType,true);
        Set(group,"Controller",car); Set(group,"Offset",V(0,0)); Set(group,"Speed",100f); Set(group,"IsElevator",true);
        object target=Activator.CreateInstance(targetType,true); Set(target,"Entity",car); Set(target,"Home",V(0,0));
        ((IList)Get(group,"Targets")).Add(target);
        ((IDictionary)Field(motion,"groups").GetValue(null))[(ushort)100]=group;
        ((IDictionary)Field(motion,"targetOwners").GetValue(null))[(ushort)100]=(ushort)100;

        var harmonyAssembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir,"0Harmony.dll"));
        var harmonyType=harmonyAssembly.GetType("HarmonyLib.Harmony",true);
        var hmType=harmonyAssembly.GetType("HarmonyLib.HarmonyMethod",true);
        object harmony=Activator.CreateInstance(harmonyType,"TLElevator.RuntimeTests");
        var patch=harmonyType.GetMethods().First(m=>m.Name=="Patch" && m.GetParameters().Length>=5);
        object[] patchArgs=new object[patch.GetParameters().Length];
        patchArgs[0]=panelType.GetMethod("EmitElevatorSignals");
        patchArgs[1]=Activator.CreateInstance(hmType,typeof(ElevatorRuntimeTests).GetMethod("Capture"));
        patch.Invoke(harmony,patchArgs);
        try {
            Tick(); Check(signals[101].Docked && !signals.Values.Any(s=>s.Arrived),"Initial docking emitted arrival");
            Call(queueType,queue,"TryEnqueue",3,null); Tick();
            Check(Get(controller,"DockedFloor")==null && !signals.Values.Any(s=>s.Docked||s.Arrived),"Departure retained docked output");
            At(100,0.5f); Tick(); Check(!signals[102].Docked && !signals[102].Arrived,"Passing floor emitted arrival");
            At(200,1f); Tick();
            Check(signals[103].Docked && signals[103].Arrived && signals[104].Arrived,"Arrival not emitted to all same-floor panels");
            Tick(); Check(signals[103].Docked && !signals.Values.Any(s=>s.Arrived),"Arrival pulse repeated");
            Field(controllerType,"dwellRemaining").SetValue(controller,0f);
            Call(queueType,queue,"TryEnqueue",3,null); Tick(); Check(!signals.Values.Any(s=>s.Arrived),"Same-floor recall emitted new arrival");
            Console.WriteLine("PASS: initial dock, departure, passing floor, one-update pulse, same-floor multi-panel and recall");
            Field(controllerType,"dwellRemaining").SetValue(controller,0f);
            Call(queueType,queue,"TryEnqueue",1,null); Tick(); At(100,0.5f); Tick();
            Check(!signals[102].Arrived,"Return passing floor emitted arrival"); At(0,1f); Tick();
            Check(signals[101].Arrived && signals[101].Docked,"Return did not dock");
            Console.WriteLine("PASS: consecutive trips and return docking");
            object contender=Activator.CreateInstance(groupType,true); Set(contender,"Controller",Item(110,0));
            ((IList)Get(contender,"Targets")).Add(target);
            Check(!(bool)Call(motion,null,"TryClaimGroupTargets",contender),"Parked elevator ownership was stolen");
            Console.WriteLine("PASS: parked elevator retains ownership");
            var snapshotType=motion.GetNestedType("MotionSnapshot"); var targetSnapshotType=motion.GetNestedType("TargetSnapshot");
            object snapshot=Activator.CreateInstance(snapshotType); Set(snapshot,"Revision",1L); Set(snapshot,"ControllerId",(ushort)100);
            Set(snapshot,"IsElevator",true); Set(snapshot,"Speed",100f);
            object ts=Activator.CreateInstance(targetSnapshotType); Set(ts,"Id",(ushort)100); ((IList)Get(snapshot,"Targets")).Add(ts);
            Check((bool)Call(motion,null,"IsSnapshotValid",snapshot),"Idle elevator self-target snapshot rejected");
            Set(snapshot,"Progress",0.5f); Set(snapshot,"Goal",1f);
            Check(!(bool)Call(motion,null,"IsSnapshotValid",snapshot),"Zero-distance moving snapshot accepted");
            Console.WriteLine("PASS: elevator snapshot validation");
        } finally { harmonyType.GetMethod("UnpatchSelf").Invoke(harmony,null); Call(motion,null,"Clear"); }
    }
}
'@
[ElevatorRuntimeTests]::Run($pluginPath, $Server.IsPresent)

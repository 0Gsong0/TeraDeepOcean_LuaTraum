param([switch]$Server)

# 独立进程中的控制权交接回归检查。
# 使用刚编译的程序集和真实游戏类型，不启动游戏，不部署模组。
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$side = if ($Server) { 'Server' } else { 'Client' }
$assemblyPath = Join-Path $projectRoot "$($side)Project\obj\Windows\Release\net8.0\TeraDeepOcean$side.dll"

Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

public static class MotionHandoffTests
{
    private static Type system, groupType, targetType, itemType, vectorType, entityType;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    // 调用实际程序集里的私有方法，验证实际交接实现。
    private static object Call(string name, params object[] args) =>
        system.GetMethod(name, PrivateStatic).Invoke(null, args);

    // 读取实际任务的属性。
    private static object Get(object value, string name) => value.GetType().GetProperty(name).GetValue(value);

    // 构造测试任务的初始状态，不依赖游戏加载器。
    private static void Set(object value, string name, object propertyValue) =>
        value.GetType().GetProperty(name).SetValue(value, propertyValue);

    // 获取实际系统使用的字典。
    private static IDictionary Map(string name) =>
        (IDictionary)system.GetField(name, PrivateStatic).GetValue(null);

    // 创建与游戏程序集完全一致的 Vector2。
    private static object Vector(float x, float y) => Activator.CreateInstance(vectorType, x, y);

    // 构造只用于状态计算的物品，不注册到游戏实体列表。
    private static object Item(ushort id, int x = 0, int y = 0)
    {
        object item = RuntimeHelpers.GetUninitializedObject(itemType);
        entityType.GetField("ID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(item, id);
        var rectProperty = itemType.GetProperty("Rect");
        rectProperty.SetValue(item, Activator.CreateInstance(rectProperty.PropertyType, x, y, 0, 0));
        return item;
    }

    // 创建真实 MotionGroup 和 MotionTarget 类型的测试对象。
    private static object Group(object controller, bool rotation, float progress, float goal, params object[] items)
    {
        object group = Activator.CreateInstance(groupType, true);
        Set(group, "Controller", controller);
        Set(group, "Offset", Vector(200f, 0f));
        Set(group, "Pivot", Vector(0f, 0f));
        Set(group, "Speed", 100f);
        Set(group, "IsRotation", rotation);
        Set(group, "AngleDegrees", 90f);
        Set(group, "Progress", progress);
        Set(group, "Goal", goal);
        foreach (object item in items)
        {
            object target = Activator.CreateInstance(targetType, true);
            Set(target, "Entity", item);
            Set(target, "Home", itemType.GetProperty("Position").GetValue(item));
            Set(target, "HomeRotation", 0f);
            ((IList)Get(group, "Targets")).Add(target);
        }
        return group;
    }

    // 断言失败立即中止，避免把只运行成功当作测试通过。
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // 从实际目标中读取 Home 分量。
    private static float Home(object group, string component)
    {
        object target = ((IList)Get(group, "Targets"))[0];
        return (float)vectorType.GetField(component).GetValue(Get(target, "Home"));
    }

    public static void Run(string pluginPath, bool server)
    {
        string gameDirectory = @"D:\APP\Steam\steamapps\common\Barotrauma";
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            string path = Path.Combine(gameDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            Path.Combine(gameDirectory, server ? "DedicatedServer.dll" : "Barotrauma.dll"));
        var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
        system = plugin.GetType("TeraDeepOcean.TLLinkedMotionSystem", true);
        groupType = system.GetNestedType("MotionGroup", BindingFlags.NonPublic);
        targetType = system.GetNestedType("MotionTarget", BindingFlags.NonPublic);
        itemType = game.GetType("Barotrauma.Item", true);
        entityType = game.GetType("Barotrauma.Entity", true);
        vectorType = itemType.GetProperty("Position").PropertyType;
        var clear = system.GetMethod("Clear");
        clear.Invoke(null, null);

        object a = Item(1), b = Item(2), target = Item(10, 300, 40);
        object oldGroup = Group(a, false, 0.5f, 1f, target);
        object incoming = Group(b, true, 0f, 0f, target);
        Map("groups")[(ushort)1] = oldGroup;
        Map("targetOwners")[(ushort)10] = (ushort)1;
        Call("RememberAcceptedGoal", oldGroup);

        Check(!(bool)Call("TryClaimGroupTargets", incoming), "Moving owner must block takeover");
        Check(Map("groups").Contains((ushort)1), "Rejected takeover removed old group");
        Set(oldGroup, "Progress", 1f);
        Check((bool)Call("TryClaimGroupTargets", incoming), "Idle owner must allow takeover");
        Check(!Map("groups").Contains((ushort)1), "Handed-off group still updates");
        Check((ushort)Map("targetOwners")[(ushort)10] == 2, "Wrong new owner");
        Check((bool)Call("IsRepeatedHandedOffCommand", a, (bool?)true), "Held signal can reclaim ownership");
        Check(!(bool)Call("IsRepeatedHandedOffCommand", a, (bool?)false), "Changed signal was ignored");
        Check(!(bool)Call("IsRepeatedHandedOffCommand", a, null), "Trigger was ignored");
        Check(!(bool)Call("IsRepeatedHandedOffCommand", Item(1), (bool?)true), "Reused ID inherited command state");
        Console.WriteLine("PASS: active rejection, idle takeover, held signal, trigger and entity identity");

        // 平移重新接管后的 Progress=1 应仍对应当前物品位置。
        object rebased = Group(a, false, 0f, 0f, target);
        Call("PrepareReacquiredGroupPose", rebased);
        Check(Math.Abs(Home(rebased, "X") + 200f - 300f) < 0.001f, "Translation rebasing jumps");
        Check((float)Get(rebased, "Progress") == 1f, "Lost previous logical endpoint");

        // 旋转重新接管：逆变换得到 Home，再正转应回到当前位置。
        object rotation = Group(a, true, 0f, 0f, target);
        Call("PrepareReacquiredGroupPose", rotation);
        Check(Math.Abs(-Home(rotation, "Y") - 300f) < 0.001f &&
              Math.Abs(Home(rotation, "X") - 40f) < 0.001f, "Rotation rebasing jumps");
        object rotationTarget = ((IList)Get(rotation, "Targets"))[0];
        Check(Math.Abs((float)Get(rotationTarget, "HomeRotation") - (float)Math.PI / 2f) < 0.001f,
            "Rotation orientation was not rebased");
        Console.WriteLine("PASS: translation and rotation resume without position/orientation jumps");

        // 先遇到可交接组，后遇到运动组时，也必须整组拒绝且不修改前者。
        clear.Invoke(null, null);
        object c = Item(3), target2 = Item(11);
        object idle = Group(a, false, 1f, 1f, target);
        object moving = Group(c, true, 0.3f, 1f, target2);
        Map("groups")[(ushort)1] = idle;
        Map("groups")[(ushort)3] = moving;
        Map("targetOwners")[(ushort)10] = (ushort)1;
        Map("targetOwners")[(ushort)11] = (ushort)3;
        Check(!(bool)Call("TryClaimGroupTargets", Group(b, false, 0f, 0f, target, target2)), "Partial takeover accepted");
        Check(Map("groups").Count == 2 && (ushort)Map("targetOwners")[(ushort)10] == 1,
            "Rejected request partially changed ownership");
        Console.WriteLine("PASS: all-or-nothing takeover");

        // 客户端较新的跨控制器快照到达后，旧快照不得重新占用目标。
        clear.Invoke(null, null);
        object current = Group(b, true, 0.5f, 1f, target);
        Call("ReplaceClientTargetOwners", current, 20L);
        Map("groups")[(ushort)2] = current;
        object stale = Activator.CreateInstance(system.GetNestedType("MotionSnapshot"));
        Set(stale, "ControllerId", (ushort)1);
        Set(stale, "Revision", 19L);
        object staleTarget = Activator.CreateInstance(system.GetNestedType("TargetSnapshot"));
        Set(staleTarget, "Id", (ushort)10);
        ((IList)Get(stale, "Targets")).Add(staleTarget);
        Check((bool)Call("IsSupersededTargetSnapshot", stale), "Old snapshot can reclaim target");
        Set(stale, "Revision", 21L);
        Check(!(bool)Call("IsSupersededTargetSnapshot", stale), "Newer takeover was rejected");
        Set(stale, "ControllerId", (ushort)2);
        Call("RecordDeletedTargetRevisions", stale);
        Call("RemoveGroup", (ushort)2, false, false);
        Set(stale, "Revision", 20L);
        Check((bool)Call("IsSupersededTargetSnapshot", stale), "Deletion lost target revision boundary");
        clear.Invoke(null, null);
        Check(Map("clientTargetRevisions").Count == 0 && Map("acceptedControllerGoals").Count == 0,
            "Round clear retained ownership state");
        Console.WriteLine("PASS: out-of-order snapshots, deletion boundary and round cleanup");
    }
}
'@

[MotionHandoffTests]::Run($assemblyPath, $Server.IsPresent)

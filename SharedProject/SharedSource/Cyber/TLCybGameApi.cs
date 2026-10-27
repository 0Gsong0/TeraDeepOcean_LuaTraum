using System;
using System.Reflection;
using HarmonyLib;

namespace TeraDeepOcean;

// 模板引用为单参数，当前游戏增加了可选 onRemoved 参数。
internal static class TLCybGameApi
{
    private static readonly MethodInfo removeItem =
        AccessTools.Method(typeof(EntitySpawner), "AddItemToRemoveQueue", new[] { typeof(Item), typeof(Action<Entity>) })
        ?? AccessTools.Method(typeof(EntitySpawner), "AddItemToRemoveQueue", new[] { typeof(Item) })
        ?? throw new MissingMethodException(typeof(EntitySpawner).FullName, "AddItemToRemoveQueue");

    internal static void RemoveItem(Item item)
    {
        if (GameMain.NetworkMember is { IsClient: true } || Entity.Spawner == null) return;
        object?[] arguments = removeItem.GetParameters().Length == 2
            ? new object?[] { item, null }
            : new object?[] { item };
        removeItem.Invoke(Entity.Spawner, arguments);
    }
}

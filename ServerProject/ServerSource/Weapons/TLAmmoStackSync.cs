using Barotrauma.Plugins;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeraDeepOcean
{
    internal static class TLAmmoStackSync
    {
        private static readonly ISimpleHookService Hooks = PluginServiceProvider.GetService<ISimpleHookService>();
        public static void Init(Harmony harmony)
        {
            // spawnMethod 是专服排入原版生成事件的方法。
            var spawnMethod = AccessTools.DeclaredMethod(typeof(EntitySpawner), "CreateNetworkEventProjSpecific", new[] { typeof(EntitySpawner.SpawnOrRemove) }) ?? throw new InvalidOperationException("找不到专服物品生成事件入口。");
            harmony.Patch(spawnMethod, postfix: new HarmonyMethod(typeof(TLAmmoStackSync), nameof(AfterSpawnEvent)));
            Hooks.RegisterHook<PluginRoundStartedDelegate>(OnRoundStarted);
        }
        internal static void Dispose()
        {
            Hooks.DeregisterHook<PluginRoundStartedDelegate>(OnRoundStarted);
        }
        /// <summary>同步随地图或存档加载的弹药堆。</summary>
        /// <param name="session">刚完成加载的回合。</param>
        private static void OnRoundStarted(GameSession session)
        {
            // 每个回合开始遍历一次，item 是已加载的物品。
            foreach (Item item in Item.ItemList)
            {
                item.GetComponent<TLAmmoStackComponent>()?.QueueRoundCountSync();
            }
        }
        /// <summary>在原版生成事件排入后，为新散弹堆排入数量事件。</summary>
        /// <param name="spawnOrRemove">原版刚处理的生成或移除事件。</param>
        private static void AfterSpawnEvent(EntitySpawner.SpawnOrRemove spawnOrRemove)
        {
            // item 是这次生成的物品。客户端先生成它，再接收发数。
            if (spawnOrRemove is EntitySpawner.SpawnEntity { Entity: Item item })
            {
                item.GetComponent<TLAmmoStackComponent>()?.QueueRoundCountSync();
            }
        }
    }
}

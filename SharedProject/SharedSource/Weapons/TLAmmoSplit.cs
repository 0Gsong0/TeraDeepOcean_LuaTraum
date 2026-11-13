using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;

namespace TeraDeepOcean;

/// <summary>将散弹从来源堆转移到指定空格或同弹种堆的请求。</summary>
/// <param name="SourceItemId">来源散弹物品的实体 ID。</param>
/// <param name="SourceOwnerId">开始拖动时，来源物品栏所有者的 ID。</param>
/// <param name="ExpectedRoundCount">开始拖动时看到的来源发数。</param>
/// <param name="RequestedRoundCount">这次拖动选中的发数。</param>
/// <param name="TargetOwnerId">目标物品栏所有者的实体 ID。</param>
/// <param name="TargetContainerIndex">容器组件索引，角色物品栏用 -1。</param>
/// <param name="TargetSlotIndex">目标物品栏中的格子索引。</param>
/// <param name="TargetItemId">松手时目标物品的 ID，空格使用 NullEntityID。</param>
[NetworkSerialize]
internal readonly record struct TLAmmoSplitRequest(
    ushort SourceItemId,
    ushort SourceOwnerId,
    int ExpectedRoundCount,
    int RequestedRoundCount,
    ushort TargetOwnerId,
    int TargetContainerIndex,
    int TargetSlotIndex,
    ushort TargetItemId) : INetSerializableStruct;

/// <summary>在单机或服务器上验证并提交散弹转移。</summary>
internal static class TLAmmoSplit
{
#if SERVER
    /// <summary>注册客户端散弹转移请求的接收入口。</summary>
    internal static void RegisterServerHandler()
    {
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLAmmoSplitRequest>(
            TLNetworkHeader.AmmoSplitRequest,
            OnServerRequest);
    }

    /// <summary>从连接获取实际操作角色，再交给统一转移入口。</summary>
    /// <param name="request">客户端提供的来源和目标。</param>
    /// <param name="sender">原版网络服务识别出的请求者。</param>
    private static void OnServerRequest(TLAmmoSplitRequest request, Client sender)
    {
        // player 是服务器确认由这个客户端控制的角色。
        if (sender is { InGame: true, NeedsMidRoundSync: false, Character: { } player })
        {
            TryTransfer(player, request);
        }
    }
#endif

    /// <summary>验证来源和目标，再按目标格子的状态拆分或合并。</summary>
    /// <param name="player">实际操作的角色。</param>
    /// <param name="request">客户端预览时记录的转移请求。</param>
    /// <returns>两边状态均已提交时返回 true。</returns>
    internal static bool TryTransfer(Character player, TLAmmoSplitRequest request)
    {
        if (GameMain.NetworkMember is { IsClient: true } ||
            GameMain.GameSession is not { IsRunning: true, RoundEnding: false } ||
            player.Removed ||
            player.IsIncapacitated ||
            player.Stun > 0f)
        {
            return false;
        }

        // spawner 发布原版生成事件，并在来源耗尽时回收物品。
        if (Entity.Spawner is not { Removed: false } spawner)
        {
            return false;
        }

#if SERVER
        if (GameMain.Server is null)
        {
            return false;
        }
#endif

        // source 是当前来源物品，sourceOwner 是它此刻所在物品栏的所有者。
        // sourceStack 提供服务器当前发数，用来拒绝过时或重复的请求。
        if (Entity.FindEntityByID(request.SourceItemId) is not Item source ||
            source.Removed ||
            source.IsInRemoveQueue ||
            source.ParentInventory?.Owner is not { Removed: false } sourceOwner ||
            sourceOwner.ID != request.SourceOwnerId ||
            !player.CanInteractWith(source) ||
            source.GetComponent<TLAmmoStackComponent>() is not { } sourceStack ||
            sourceStack.RoundCount != request.ExpectedRoundCount ||
            request.RequestedRoundCount <= 0 ||
            request.RequestedRoundCount > sourceStack.RoundCount)
        {
            return false;
        }

        // target 是服务端重新解析出的目标库存，slotIndex 是松手的格子。
        Inventory? target = ResolveTargetInventory(request);
        int slotIndex = request.TargetSlotIndex;

        // 原版生成包用一个字节保存槽位，255 表示没有指定槽位。
        if (target is null ||
            target.Owner is not { Removed: false } targetOwner ||
            slotIndex < 0 ||
            slotIndex >= byte.MaxValue ||
            slotIndex >= target.Capacity ||
            !player.CanAccessInventory(target))
        {
            return false;
        }

        // 目标在传输期间被换掉时，拒绝这次操作，保留两个格子的现状。
        Item? targetItem = target.GetItemAt(slotIndex);
        if ((targetItem?.ID ?? Entity.NullEntityID) != request.TargetItemId)
        {
            return false;
        }

        if (targetItem is not null)
        {
            if (targetItem == source ||
                targetItem.Removed ||
                targetItem.IsInRemoveQueue ||
                targetItem.ParentInventory != target ||
                !player.CanInteractWith(targetItem) ||
                targetItem.GetComponent<TLAmmoStackComponent>() is not { } targetStack ||
                !CanMerge(sourceStack, targetStack))
            {
                return false;
            }

            return TryMerge(sourceStack, targetStack, request.RequestedRoundCount, spawner);
        }

        // 整堆移到空格由原版拖放处理。只有部分发数才会新建物品。
        if (request.RequestedRoundCount == sourceStack.RoundCount ||
            !target.IsSlotEmpty(slotIndex) ||
            !target.CanBePutInSlot(source, slotIndex))
        {
            return false;
        }

        // 容器的拖放限制和访问规则也在提交时检查。
        if (target is ItemInventory itemInventory &&
            (!itemInventory.Container.AllowDragAndDrop ||
             !itemInventory.Container.IsAccessible()))
        {
            return false;
        }

        // splitItem 是暂未发布给客户端的新散弹堆。
        // 复制原堆的品质、耐久和前哨归属，拆分后仍按同样规则处理。
        var splitItem = new Item(source.Prefab, targetOwner.Position, targetOwner.Submarine)
        {
            Quality = source.Quality,
            Condition = source.Condition,
            AllowStealing = source.AllowStealing,
            SpawnedInNonPlayerOutpostOrSubmarine = source.SpawnedInNonPlayerOutpostOrSubmarine,
            OriginalLocationSeed = source.OriginalLocationSeed,
            StolenDuringRound = source.StolenDuringRound
        };

        // 先将新堆初始化为本次选中的发数，再放入指定格子。
        // 生成事件稍后统一发布，因此此时关闭物品栏网络事件。
        if (splitItem.GetComponent<TLAmmoStackComponent>() is not { } splitStack ||
            !splitStack.TrySetRoundCount(request.RequestedRoundCount, synchronize: false) ||
            !target.TryPutItem(
                splitItem,
                slotIndex,
                allowSwapping: false,
                allowCombine: false,
                user: player,
                createNetworkEvent: false,
                triggerOnInsertedEffects: false) ||
            splitItem.ParentInventory != target ||
            target.GetItemAt(slotIndex) != splitItem ||
            sourceStack.RoundCount != request.ExpectedRoundCount ||
            !sourceStack.TrySetRoundCount(
                request.ExpectedRoundCount - request.RequestedRoundCount, synchronize: false))
        {
            // 提交失败时移除尚未发布的新物品，来源发数保留。
            if (!splitItem.Removed)
            {
                splitItem.Remove();
            }

            return false;
        }

#if SERVER
        // 生成事件携带目标容器和槽位。TLAmmoStackSync 会紧接着同步新堆的发数。
        spawner.CreateNetworkEvent(new EntitySpawner.SpawnEntity(splitItem));

        // 最后同步来源剩余发数。
        sourceStack.QueueRoundCountSync();
#endif

        return true;
    }

    /// <summary>确认两堆弹种及物品状态一致，合并后可以保留原状态。</summary>
    private static bool CanMerge(TLAmmoStackComponent sourceStack, TLAmmoStackComponent targetStack)
    {
        Item source = sourceStack.Item;
        Item target = targetStack.Item;

        return sourceStack.AmmoType == targetStack.AmmoType &&
               source.Quality == target.Quality &&
               source.Condition == target.Condition &&
               source.AllowStealing == target.AllowStealing &&
               source.SpawnedInNonPlayerOutpostOrSubmarine == target.SpawnedInNonPlayerOutpostOrSubmarine &&
               (source.OriginalLocationSeed ?? string.Empty) == (target.OriginalLocationSeed ?? string.Empty) &&
               source.StolenDuringRound == target.StolenDuringRound;
    }

    /// <summary>按目标剩余容量合并，并回收耗尽的来源物品。</summary>
    private static bool TryMerge(
        TLAmmoStackComponent sourceStack,
        TLAmmoStackComponent targetStack,
        int requestedRoundCount,
        EntitySpawner spawner)
    {
        int targetRoundCount = targetStack.RoundCount;
        int transferredRoundCount = Math.Min(requestedRoundCount, targetStack.Capacity - targetRoundCount);
        if (transferredRoundCount <= 0)
        {
            return false;
        }

        // 两边均更新成功后才同步，避免客户端收到一笔只完成一半的转移。
        int remainingRoundCount = sourceStack.RoundCount - transferredRoundCount;
        if (!targetStack.TrySetRoundCount(targetRoundCount + transferredRoundCount, synchronize: false))
        {
            return false;
        }

        if (!sourceStack.TrySetRoundCount(remainingRoundCount, synchronize: false))
        {
            targetStack.TrySetRoundCount(targetRoundCount, synchronize: false);
            return false;
        }

        if (remainingRoundCount == 0)
        {
            spawner.AddItemToRemoveQueue(sourceStack.Item);
        }

#if SERVER
        targetStack.QueueRoundCountSync();
        if (remainingRoundCount > 0)
        {
            sourceStack.QueueRoundCountSync();
        }
#endif

        return true;
    }

    /// <summary>根据实体 ID 和组件索引定位目标物品栏。</summary>
    /// <param name="request">包含目标物品栏地址的请求。</param>
    /// <returns>找到的物品栏，无效时返回 null。</returns>
    private static Inventory? ResolveTargetInventory(TLAmmoSplitRequest request)
    {
        // componentIndex 使用原版物品的组件索引。
        int componentIndex = request.TargetContainerIndex;

        switch (Entity.FindEntityByID(request.TargetOwnerId))
        {
            case Character { Removed: false } character when componentIndex == -1:
                return character.Inventory;

            case Item { Removed: false, IsInRemoveQueue: false } item
                when componentIndex >= 0 &&
                     componentIndex <= byte.MaxValue &&
                     componentIndex < item.Components.Count &&
                     item.Components[componentIndex] is ItemContainer container:
                return container.Inventory;

            default:
                return null;
        }
    }
}

using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace TeraDeepOcean;

/// <summary>沿用原版拖放，支持选取散弹发数和合并同弹种堆。</summary>
internal static class TLAmmoSplitDrag
{
    /// <summary>记录本次散弹拖动的来源和按下左键时选中的发数。</summary>
    private sealed record AmmoDrag(
        Item Source, Inventory SourceInventory, int ExpectedRoundCount, int RequestedRoundCount)
    {
        public bool IsPartial => RequestedRoundCount < ExpectedRoundCount;
        public LocalizedString CountLabel { get; } = $"x{RequestedRoundCount}";
    }

    // 普通物品的拖动不使用散弹预览。
    private static AmmoDrag? activeDrag;

    /// <summary>接入原版格子更新、松手处理和绘制。</summary>
    internal static void Init(Harmony harmony)
    {
        // UpdateSlot 在本机客户端中是受保护的方法。
        var updateSlot = AccessTools.DeclaredMethod(
            typeof(Inventory), "UpdateSlot",
            new[] { typeof(VisualSlot), typeof(int), typeof(Item), typeof(bool) })
            ?? throw new InvalidOperationException("找不到 Inventory.UpdateSlot。");

        harmony.Patch(updateSlot,
            postfix: new HarmonyMethod(typeof(TLAmmoSplitDrag), nameof(AfterUpdateSlot)));
        harmony.Patch(AccessTools.Method(typeof(Inventory), nameof(Inventory.UpdateDragging)),
            prefix: new HarmonyMethod(typeof(TLAmmoSplitDrag), nameof(BeforeUpdateDragging)));
        harmony.Patch(AccessTools.Method(typeof(Inventory), nameof(Inventory.DrawSlot)),
            prefix: new HarmonyMethod(typeof(TLAmmoSplitDrag), nameof(BeforeDrawSlot)));
        harmony.Patch(AccessTools.Method(typeof(Inventory), nameof(Inventory.DrawFront)),
            postfix: new HarmonyMethod(typeof(TLAmmoSplitDrag), nameof(AfterDrawFront)));
    }

    /// <summary>卸载时释放正在拖动的预览。</summary>
    internal static void Dispose()
    {
        if (activeDrag is { } drag)
        {
            EndPreview(drag);
        }
    }

    /// <summary>原版开始拖动后读取散弹的选取数量。</summary>
    private static void AfterUpdateSlot(VisualSlot slot)
    {
        if (activeDrag is { } drag)
        {
            if (!HasNativeDrag(drag.Source))
            {
                EndPreview(drag);
            }
            else
            {
                // 拆分和合并接管松手，整堆移到其他位置时保留原版双击行为。
                if (PlayerInput.PrimaryMouseButtonClicked() &&
                    (drag.IsPartial || GetMergeTarget(drag.Source, GetHoveredSlot()) is not null))
                {
                    Inventory.doubleClickedItems.Clear();
                }

                return;
            }
        }

        if (Screen.Selected != GameMain.GameScreen ||
            !PlayerInput.PrimaryMouseButtonDown() ||
            Inventory.DraggingSlot != slot ||
            Inventory.DraggingItems.Count != 1)
        {
            return;
        }

        // 使用原版已选中的物品提供拖动图标。
        Item source = Inventory.DraggingItems[0];
        if (source.ParentInventory is { } sourceInventory &&
            source.GetComponent<TLAmmoStackComponent>() is { RoundCount: > 0 } stack)
        {
            int selectedRoundCount = stack.RoundCount;

            // 原版优先处理「取一半」，奇数时拖动的一半向上取整。
            if (PlayerInput.KeyDown(InputType.TakeHalfFromInventorySlot))
            {
                selectedRoundCount = (stack.RoundCount + 1) / 2;
            }
            else if (PlayerInput.KeyDown(InputType.TakeOneFromInventorySlot))
            {
                selectedRoundCount = 1;
            }

            activeDrag = new AmmoDrag(source, sourceInventory, stack.RoundCount, selectedRoundCount);
        }
    }

    /// <summary>接管拆分或合并的松手，其他整堆拖放继续由原版处理。</summary>
    private static void BeforeUpdateDragging()
    {
        if (activeDrag is not { } drag)
        {
            return;
        }

        if (Screen.Selected != GameMain.GameScreen ||
            GUI.PauseMenuOpen ||
            PlayerInput.KeyHit(Keys.Escape) ||
            PlayerInput.SecondaryMouseButtonDown() ||
            Character.Controlled is not { } player ||
            !HasNativeDrag(drag.Source) ||
            drag.Source.ParentInventory != drag.SourceInventory ||
            !player.CanInteractWith(drag.Source))
        {
            if (drag.IsPartial)
            {
                EndPreview(drag);
            }
            else
            {
                activeDrag = null;
            }

            return;
        }

        if (PlayerInput.PrimaryMouseButtonHeld())
        {
            return;
        }

        // 只接受本帧发生的松手，以免失去输入焦点后提交残留的预览。
        Inventory.SlotReference? dropSlot = PlayerInput.PrimaryMouseButtonClicked()
            ? GetHoveredSlot()
            : null;
        Item? mergeTarget = GetMergeTarget(drag.Source, dropSlot);

        if (!drag.IsPartial && mergeTarget is null)
        {
            activeDrag = null;
            return;
        }

        EndPreview(drag);
        if (dropSlot is null)
        {
            return;
        }

        player.ClearInputs();
        SubmitDrop(player, drag, dropSlot, mergeTarget);
    }

    /// <summary>取得当前鼠标实际命中的原版可用格子。</summary>
    private static Inventory.SlotReference? GetHoveredSlot()
    {
        Inventory.SlotReference? selectedSlot = Inventory.SelectedSlot;
        if (selectedSlot is null ||
            selectedSlot.Slot.Disabled ||
            !selectedSlot.Slot.MouseOn() ||
            !Inventory.IsMouseOnInventory)
        {
            return null;
        }

        return selectedSlot;
    }

    /// <summary>识别需要接管拖放的同弹种目标。具体合并条件由服务端校验。</summary>
    private static Item? GetMergeTarget(Item source, Inventory.SlotReference? dropSlot)
    {
        Item? target = dropSlot?.ParentInventory.GetItemAt(dropSlot.SlotIndex);
        if (target is null ||
            target == source ||
            target.Prefab.Identifier != source.Prefab.Identifier ||
            target.GetComponent<TLAmmoStackComponent>() is null)
        {
            return null;
        }

        return target;
    }

    /// <summary>用原版命中的格子构造散弹转移请求。</summary>
    private static void SubmitDrop(
        Character player, AmmoDrag drag, Inventory.SlotReference dropSlot, Item? mergeTarget)
    {
        Inventory target = dropSlot.ParentInventory;
        int slotIndex = dropSlot.SlotIndex;
        if (drag.SourceInventory.Owner is not { Removed: false } sourceOwner ||
            target.Owner is not { Removed: false } targetOwner)
        {
            return;
        }

        if (mergeTarget is null &&
            (!target.IsSlotEmpty(slotIndex) || !target.CanBePutInSlot(drag.Source, slotIndex)))
        {
            return;
        }

        // 容器用原版组件索引定位，角色物品栏用 -1。
        int containerIndex = target is ItemInventory itemInventory
            ? itemInventory.Container.Item.GetComponentIndex(itemInventory.Container)
            : -1;

        var request = new TLAmmoSplitRequest(
            drag.Source.ID, sourceOwner.ID, drag.ExpectedRoundCount, drag.RequestedRoundCount,
            targetOwner.ID, containerIndex, slotIndex, mergeTarget?.ID ?? Entity.NullEntityID);

        if (GameMain.Client is { connected: true })
        {
            Plugin.Network.Send(TLNetworkHeader.AmmoSplitRequest, request);
        }
        else if (GameMain.NetworkMember is null)
        {
            TLAmmoSplit.TryTransfer(player, request);
        }
    }

    /// <summary>确认原版拖动列表仍在为同一件散弹物品提供预览。</summary>
    private static bool HasNativeDrag(Item source) =>
        Inventory.DraggingItems.Count == 1 && Inventory.DraggingItems[0] == source;

    /// <summary>结束拆分预览，只清理该预览占用的原版拖动记录。</summary>
    private static void EndPreview(AmmoDrag drag)
    {
        activeDrag = null;
        if (HasNativeDrag(drag.Source))
        {
            Inventory.DraggingItems.Clear();
            Inventory.DraggingSlot = null;
        }
    }

    /// <summary>拆分期间仍在来源格显示实际库存。</summary>
    private static void BeforeDrawSlot(Item? item, ref bool drawItem)
    {
        if (activeDrag is { IsPartial: true } drag &&
            item == drag.Source &&
            HasNativeDrag(drag.Source))
        {
            drawItem = true;
        }
    }

    /// <summary>在原版鼠标拖动图标旁绘制已选发数。</summary>
    private static void AfterDrawFront(SpriteBatch spriteBatch)
    {
        if (Screen.Selected != GameMain.GameScreen ||
            GUI.PauseMenuOpen ||
            activeDrag is not { } drag ||
            !HasNativeDrag(drag.Source) ||
            Inventory.DraggingSlot?.MouseOn() == true)
        {
            return;
        }

        // position 沿用原版拖动角标相对鼠标的偏移。
        Vector2 position = PlayerInput.MousePosition + Vector2.One * (16f * GUI.Scale);
        GUIStyle.SmallFont.DrawString(spriteBatch, drag.CountLabel, position + Vector2.One, Color.Black);
        GUIStyle.SmallFont.DrawString(spriteBatch, drag.CountLabel, position, Color.White);
    }
}

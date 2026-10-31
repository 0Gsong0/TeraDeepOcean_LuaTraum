using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 根据主控制器的直接 link 建立电梯布局
    /// 只读取和校验配置
    /// </summary>
    public static class TLElevatorLayoutBuilder
    {
        /// <summary>
        /// 同楼层各面板允许的最大停靠高度差。
        /// </summary>
        public const float FloorHeightTolerance = 5f;
        /// <summary>
        /// 一个楼层的登记结果。
        /// 同楼层的多个外呼面板共用同一个停靠高度。
        /// </summary>
        public sealed class FloorEntry
        {
            public int Number { get; }
            public float StopLocalY { get; }
            /// <summary>
            /// 归属于此楼层的全部外呼面板。
            /// </summary>
            public IReadOnlyList<TLElevatorCallPanelComponent> Panels { get; }
            internal FloorEntry(int number, float stopLocalY,List<TLElevatorCallPanelComponent> panels)
            {
                Number = number;
                StopLocalY = stopLocalY;
                Panels = new List<TLElevatorCallPanelComponent>(panels).AsReadOnly();
            }
        }
        /// <summary>
        /// 一台电梯建立成功后的布局。
        /// </summary>
        public sealed class ElevatorLayout
        {
            /// <summary>
            /// 按楼层号快速查找楼层。
            /// </summary>
            private readonly Dictionary<int, FloorEntry> floorsByNumber;
            /// <summary>
            /// 按实际停靠高度从低到高排列的楼层。
            /// </summary>
            public IReadOnlyList<FloorEntry> Floors { get; }
            /// <summary>
            /// 此布局所属的主控制器。
            /// </summary>
            public TLElevatorControllerComponent Controller { get; }
            /// <summary>
            /// 建立布局时的潜艇坐标空间。
            /// null 表示世界坐标空间。
            /// </summary>
            public Submarine? CoordinateSubmarine { get; }
            /// <summary>
            /// 需要随轿厢移动的实体，包含主控制器。不包含外呼面板。
            /// </summary>
            public IReadOnlyList<MapEntity> CabinTargets { get; }
            internal ElevatorLayout(TLElevatorControllerComponent controller, List<FloorEntry> floors, List<MapEntity> cabinTargets)
            {
                Controller = controller;
                CoordinateSubmarine = controller.ControllerItem.Submarine;
                Floors = new List<FloorEntry>(floors).AsReadOnly();
                CabinTargets = new List<MapEntity>(cabinTargets).AsReadOnly();
                floorsByNumber = new Dictionary<int, FloorEntry>();
                foreach (FloorEntry floor in floors)
                {
                    floorsByNumber.Add(floor.Number, floor);
                }
            }
            public bool TryGetFloor(int number,out FloorEntry? floor)
            {
                return floorsByNumber.TryGetValue(number, out floor);
            }
        }
        /// <summary>
        /// 尝试建立一台电梯的布局。
        /// </summary>
        /// <param name="controller"></param>
        /// <param name="layout"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        public static bool TryBuild(TLElevatorControllerComponent controller, out ElevatorLayout? layout, out string error)
        {
            layout = null;
            error = string.Empty;
            if (controller == null)
            {
                error = "没有提供电梯主控制器";
                return false;
            }
            Item controllerItem = controller.ControllerItem;
            if(controllerItem==null||controllerItem.Removed||controllerItem.ParentInventory is not null)
            {
                error = "电梯主控制器被移除或位于库存中";
                return false;
            }
            if (!float.IsFinite(controller.GetCabinLocalY())|| !IsFinitePosition(controllerItem.Position))
            {
                error = "电梯主控制器的位置或轿厢基准高度无效。";
                return false;
            }
            List<MapEntity> cabinTargets = new()
            {
                controllerItem
            };
            HashSet<ushort> seen = new()
            {
                controllerItem.ID
            };
            Dictionary<int, List<TLElevatorCallPanelComponent>> panelsByFloor = new();
            foreach (MapEntity? entity in controllerItem.linkedTo)
            {
                if (entity is null || !seen.Add(entity.ID)) continue;
                if (entity.Removed)
                {
                    error = $"关联目标 {entity.ID} 已被移除。";
                    return false;
                }
                if (!ReferenceEquals(entity.Submarine, controllerItem.Submarine))
                {
                    error = $"关联目标 {entity.ID} 与主控制器不属于同一潜艇。";
                    return false;
                }
                if (!IsFinitePosition(entity.Position))
                {
                    error = $"关联目标 {entity.ID} 的位置无效。";
                    return false;
                }
                if (entity is Item linkedItem)
                {
                    if (linkedItem.ParentInventory is not null)
                    {
                        error = $"关联物品 {linkedItem.ID} 位于库存中。";
                        return false;
                    }
                    // 避免把另一台电梯的主控制器当作轿厢设备移动。
                    if (linkedItem.GetComponent<TLElevatorControllerComponent>() is not null)
                    {
                        error = $"关联物品 {linkedItem.ID} 是另一个电梯主控制器。";
                        return false;
                    }
                    var callPanel = linkedItem.GetComponent<TLElevatorCallPanelComponent>();
                    if (callPanel is not null)
                    {
                        if (!panelsByFloor.TryGetValue(callPanel.Floor, out var floorPanels))
                        {
                            floorPanels = new List<TLElevatorCallPanelComponent>();
                            panelsByFloor.Add(callPanel.Floor, floorPanels);
                        }
                        floorPanels.Add(callPanel);
                        continue;
                    }
                    cabinTargets.Add(linkedItem);
                    continue;
                }
                if(entity is Structure)
                {
                    cabinTargets.Add(entity);
                    continue;
                }
                // Hull、Gap 等需要独立适配。
                // 这里明确拒绝，避免搭建者误以为它们会随轿厢移动。
                error =
                    $"关联目标 {entity.ID} 的类型 {entity.GetType().Name} " +
                    "暂不支持作为电梯直接关联目标。";

                return false;
            }
            if (panelsByFloor.Count == 0)
            {
                error = "主控制器没有关联任何电梯外呼面板。";
                return false;
            }
            // 只有自动加入的主控制器，表示还没有配置实际轿厢实体。
            if (cabinTargets.Count == 1)
            {
                error = "主控制器没有关联轿厢结构或随行物品。";
                return false;
            }
            List<FloorEntry> floors = new();
            foreach (var pair in panelsByFloor)
            {
                if (!TryBuildFloor(pair.Key,pair.Value,out FloorEntry? floor,out error))
                {
                    return false;
                }

                floors.Add(floor!);
            }
            // 楼层号用于识别；上下行顺序使用实际高度。
            floors.Sort((left, right) =>
            {
                int heightOrder = left.StopLocalY.CompareTo(right.StopLocalY);

                return heightOrder != 0
                    ? heightOrder
                    : left.Number.CompareTo(right.Number);
            });
            // 不同楼层号不应该指向几乎相同的停靠位置。
            // 同高度的多个按钮应使用同一个 Floor 编号。
            for (int i = 1; i < floors.Count; i++)
            {
                FloorEntry lower = floors[i - 1];
                FloorEntry upper = floors[i];

                if (upper.StopLocalY - lower.StopLocalY
                    <= FloorHeightTolerance)
                {
                    error =
                        $"楼层 {lower.Number} 与楼层 {upper.Number} " +
                        "的停靠高度过于接近，请检查楼层号和偏移。";

                    return false;
                }
            }
            layout = new ElevatorLayout(
                controller,
                floors,
                cabinTargets);
            return true;
        }
        /// <summary>
        /// 合并同楼层面板，并检查全部停靠高度是否一致。
        /// 使用最高与最低值的差，避免多个小误差逐步累积。
        /// </summary>
        private static bool TryBuildFloor(int number, List<TLElevatorCallPanelComponent> panels, out FloorEntry? floor, out string error)
        {
            floor = null;
            error = string.Empty;
            // 固定代表面板的选择规则，不依赖 link 的遍历顺序。
            panels.Sort((left, right) => left.PanelItem.ID.CompareTo(right.PanelItem.ID));
            float minimumY = float.PositiveInfinity;
            float maximumY = float.NegativeInfinity;
            foreach (TLElevatorCallPanelComponent panel in panels)
            {
                float stopY = panel.GetStopLocalY();
                if (!float.IsFinite(stopY))
                {
                    error =
                        $"楼层 {number} 的外呼面板 " +
                        $"{panel.PanelItem.ID} 停靠高度无效。";

                    return false;
                }
                minimumY = MathF.Min(minimumY, stopY);
                maximumY = MathF.Max(maximumY, stopY);
            }
            if (maximumY - minimumY > FloorHeightTolerance)
            {
                error =
                    $"楼层 {number} 的外呼面板停靠高度不一致：" +
                    $"最低 {minimumY:0.0}，最高 {maximumY:0.0}。" +
                    "请调整面板的停靠偏移，使青色基准线重合。";

                return false;
            }
            // 不取平均值，避免添加一个面板就轻微改变停靠位置。
            // 在允许误差内，使用 ID 最小面板的停靠高度。
            float representativeY = panels[0].GetStopLocalY();

            floor = new FloorEntry(number, representativeY, panels);
            return true;
        }
        /// <summary>
        /// 检查显示坐标是否包含 NaN 或无穷大。
        /// </summary>
        private static bool IsFinitePosition(Vector2 position)
        {
            return float.IsFinite(position.X) &&
                   float.IsFinite(position.Y);
        }
    }
}

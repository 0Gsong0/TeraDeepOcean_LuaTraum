using System;
using System.Collections.Generic;
using Barotrauma.Items.Components;

namespace TeraDeepOcean
{
    /// <summary>电梯专用平移：复用实体适配及快照，停靠期间也保留独占。</summary>
    public static partial class TLLinkedMotionSystem
    {
        /// <summary>建立整套轿厢的保持任务。任何目标无效或被运动任务占用时整组拒绝。</summary>
        public static bool TryCreateElevatorMotion(Item controller, IReadOnlyList<MapEntity> targets, float speed, out string error)
        {
            error = string.Empty;
            if (!IsAuthority || !EnsureRound() || controller.Removed || !float.IsFinite(speed) || speed <= 0f)
            {
                error = "无法建立电梯运动任务：回合、控制器或速度无效。";
                return false;
            }
            if (groups.ContainsKey(controller.ID) || targets.Count == 0 || targets.Count > MaxTargetsPerController)
            {
                error = "主控制器已有任务，或轿厢实体数量无效。";
                return false;
            }
            var group = new MotionGroup
            {
                Controller = controller, CoordinateSubmarine = controller.Submarine,
                Offset = Vector2.Zero, Speed = speed, IsElevator = true
            };
            HashSet<ushort> seen = new();
            foreach (var target in targets)
            {
                if (target.Removed || !seen.Add(target.ID) || !IsValidVector(target.Position) ||
                    !ReferenceEquals(target.Submarine, controller.Submarine) ||
                    !ReferenceEquals(Entity.FindEntityByID(target.ID), target))
                {
                    error = "轿厢包含失效、重复或跨坐标空间的目标。";
                    return false;
                }
                if (target is Item item)
                {
                    if (item.ParentInventory is not null)
                    {
                        error = $"轿厢物品 {item.ID} 位于库存中。";
                        return false;
                    }
                    foreach (var component in item.Components)
                    {
                        // 明确白名单，避免未知组件的附属物理体留在原地。
                        Type type = component.GetType();
                        if (type != typeof(Holdable) && type != typeof(Pickable) &&
                            type != typeof(ItemContainer) && type != typeof(Door) &&
                            type != typeof(LightComponent) && type != typeof(ConnectionPanel) &&
                            !(ReferenceEquals(item, controller) && type == typeof(TLElevatorControllerComponent)))
                        {
                            error = $"轿厢物品 {item.ID} 的组件 {type.Name} 尚未适配电梯。";
                            return false;
                        }
                    }
                }
                else if (target is not Structure)
                {
                    error = "轿厢只支持结构和已适配的物品。";
                    return false;
                }
                group.Targets.Add(new MotionTarget { Entity = target, Home = target.Position });
            }
            if (!seen.Contains(controller.ID))
            {
                error = "轿厢列表必须包含主控制器。";
                return false;
            }
            if (!TryClaimGroupTargets(group))
            {
                error = "轿厢目标正被其它运动任务或电梯占用。";
                return false;
            }
            groups.Add(controller.ID, group);
            SendSnapshot(group, deleted: false);
            group.SyncTimer = IdleSyncInterval;
            return true;
        }

        /// <summary>为已停靠的轿厢设置下一段相对位移。统一取整，让结构和物品保持同一位移。</summary>
        public static bool TryStartElevatorTrip(Item controller, float deltaY, float speed)
        {
            if (!IsAuthority || !float.IsFinite(deltaY) || !float.IsFinite(speed) || speed <= 0f ||
                !TryGetElevatorMotion(controller, out bool moving) || moving) return false;
            MotionGroup group = groups[controller.ID];
            for (int i = 0; i < group.Targets.Count; i++)
            {
                var target = group.Targets[i];
                group.Targets[i] = new MotionTarget { Entity = target.Entity, Home = target.Entity.Position };
            }
            group.Offset = new Vector2(0f, MathF.Round(deltaY));
            group.Progress = 0f;
            group.Goal = group.Offset.Y == 0f ? 0f : 1f;
            group.Speed = speed;
            SendSnapshot(group, deleted: false);
            group.SyncTimer = GetSyncInterval(group);
            return true;
        }

        /// <summary>检查专用任务是否仍完整，并返回是否尚未完成本段运动。</summary>
        public static bool TryGetElevatorMotion(Item controller, out bool moving)
        {
            moving = false;
            if (!groups.TryGetValue(controller.ID, out var group) || !group.IsElevator ||
                !ReferenceEquals(group.Controller, controller) || !IsGroupValid(group)) return false;
            moving = group.Progress != group.Goal;
            return true;
        }

        /// <summary>明确停用、删除或故障时释放，允许已标记 Removed 的原控制器执行清理。</summary>
        public static void ReleaseElevatorMotion(Item controller)
        {
            if (IsAuthority && groups.TryGetValue(controller.ID, out var group) && group.IsElevator &&
                ReferenceEquals(group.Controller, controller)) RemoveGroup(controller.ID, notifyClients: true);
        }
    }
}

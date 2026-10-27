using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace TeraDeepOcean
{
    /// <summary>
    /// 平移和旋转共用的控制权交接。
    /// 旧任务停靠时仍可保持实体，只有新任务成功接管后才撤销旧任务。
    /// </summary>
    public static partial class TLLinkedMotionSystem
    {
        /// <summary>
        /// 控制器最后成功接受的逻辑目标（0 或 1）。
        /// 交接后仍保留，防止相同的持续 set_in 抢回控制权。
        /// 同时保存物品引用，避免实体 ID 重用时沿用旧记录。
        /// </summary>
        private static readonly Dictionary<ushort, (Item Controller, float Goal)>
            acceptedControllerGoals = new();

        /// <summary>
        /// 客户端每个目标已经应用的最新全局快照序号。
        /// 跨控制器比较，防止旧控制器的迟到消息覆盖新控制器的姿态。
        /// 删除任务后仍保留，换回合时由 Clear 清空。
        /// </summary>
        private static readonly Dictionary<ushort, long> clientTargetRevisions = new();

        /// <summary>
        /// 已交接的控制器重复收到相同 set_in 时，仅认为命令已满足。
        /// 改变 set_in 或新的 trigger_in 才能申请重新接管。
        /// 有存活任务时继续使用原有命令处理，包括改速和中途反向。
        /// </summary>
        private static bool IsRepeatedHandedOffCommand(Item controller, bool? goal)
        {
            return !groups.ContainsKey(controller.ID) &&
                goal.HasValue &&
                acceptedControllerGoals.TryGetValue(controller.ID, out var previous) &&
                ReferenceEquals(previous.Controller, controller) &&
                previous.Goal == (goal.Value ? 1f : 0f);
        }

        /// <summary>
        /// 只记录成功接受的目标。运动冲突导致的拒绝不改变记录，
        /// 持续输入因此可以在占用者到达后再次尝试；这里没有命令队列。
        /// </summary>
        private static void RememberAcceptedGoal(MotionGroup group)
        {
            acceptedControllerGoals[group.Controller.ID] = (group.Controller, group.Goal);
        }

        /// <summary>
        /// 交接后再次创建任务时，以实体当前姿态重建初始姿态。
        /// 保留控制器上次的逻辑进度，避免原来的终点突然变成新原点。
        ///
        /// 例如平移控制器先完成 +200，再让旋转器接管；
        /// 平移控制器随后收到 0 时，从新位置移动 -200，而不传送回旧轨迹。
        /// 未发生交接的存活任务不经过此方法，仍按原记录精确往返。
        /// </summary>
        private static void PrepareReacquiredGroupPose(MotionGroup group)
        {
            if (!acceptedControllerGoals.TryGetValue(group.Controller.ID, out var previous) ||
                !ReferenceEquals(previous.Controller, group.Controller))
            {
                return;
            }

            float startProgress = previous.Goal;
            float angle = MathHelper.ToRadians(group.AngleDegrees * startProgress);
            float cosine = MathF.Cos(angle);
            float sine = MathF.Sin(angle);

            for (int i = 0; i < group.Targets.Count; i++)
            {
                MotionTarget target = group.Targets[i];
                Vector2 home;
                float homeRotation = target.HomeRotation;

                if (group.IsRotation)
                {
                    // 当前位置是旋转后的点，应用反向矩阵求出新的 Home。
                    Vector2 relative = target.Entity.Position - group.Pivot;
                    home = group.Pivot + new Vector2(
                        relative.X * cosine + relative.Y * sine,
                        -relative.X * sine + relative.Y * cosine);
                    homeRotation = GetInitialDisplayRotation(target.Entity) + angle;
                }
                else
                {
                    home = target.Entity.Position - group.Offset * startProgress;
                }

                group.Targets[i] = new MotionTarget
                {
                    Entity = target.Entity,
                    Home = home,
                    HomeRotation = homeRotation
                };
            }

            group.Progress = startProgress;
            group.Goal = startProgress;
        }

        /// <summary>
        /// 一次性申请整组目标。
        /// 先检查全部占用者，全部允许交接后才移除旧任务，避免部分成功。
        /// 只要旧组仍在运动，即使冲突的那个目标恰好位于旋转中心，也拒绝。
        /// </summary>
        private static bool TryClaimGroupTargets(MotionGroup incoming)
        {
            HashSet<ushort> displacedControllers = new();

            foreach (MotionTarget target in incoming.Targets)
            {
                if (!targetOwners.TryGetValue(target.Entity.ID, out ushort owner) ||
                    owner == incoming.Controller.ID)
                {
                    continue;
                }

                if (groups.TryGetValue(owner, out MotionGroup? occupied) &&
                    occupied.Progress != occupied.Goal)
                {
                    return false;
                }

                displacedControllers.Add(owner);
            }

            foreach (ushort owner in displacedControllers)
            {
                // 撤销旧组的整体保持和周期同步，保留信号目标记忆。
                // 即使仅部分目标重叠，也整体释放旧组，保持原有整组语义。
                RemoveGroup(owner, notifyClients: true, preserveCommandHistory: true);
            }

            foreach (MotionTarget target in incoming.Targets)
            {
                targetOwners[target.Entity.ID] = incoming.Controller.ID;
            }

            return true;
        }

        /// <summary>
        /// 判断快照中的任何目标是否已经被更新的消息接管。
        /// 即使旧控制器的删除消息尚未到达，也不会让旧快照重新控制它。
        /// </summary>
        private static bool IsSupersededTargetSnapshot(MotionSnapshot snapshot)
        {
            foreach (TargetSnapshot target in snapshot.Targets)
            {
                if (clientTargetRevisions.TryGetValue(target.Id, out long revision) &&
                    revision > snapshot.Revision)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 删除消息到达时，保留本地旧组各目标的最新消息边界。
        /// 取较大序号，避免旧控制器的删除消息覆盖新控制器的记录。
        /// </summary>
        private static void RecordDeletedTargetRevisions(MotionSnapshot snapshot)
        {
            if (!groups.TryGetValue(snapshot.ControllerId, out MotionGroup? group)) return;

            foreach (MotionTarget target in group.Targets)
            {
                clientTargetRevisions.TryGetValue(target.Entity.ID, out long previous);
                clientTargetRevisions[target.Entity.ID] = Math.Max(previous, snapshot.Revision);
            }
        }

        /// <summary>
        /// 按服务器已确认的快照替换客户端控制权。
        /// 客户端不自行判断旧组是否到达：其预测进度可能与服务器有差异。
        /// 在调用前必须先通过 IsSupersededTargetSnapshot 检查。
        /// </summary>
        private static void ReplaceClientTargetOwners(MotionGroup incoming, long revision)
        {
            HashSet<ushort> displacedControllers = new();

            // 替换同一控制器的旧快照时，也先清理旧列表的占用。
            if (groups.ContainsKey(incoming.Controller.ID))
            {
                displacedControllers.Add(incoming.Controller.ID);
            }

            foreach (MotionTarget target in incoming.Targets)
            {
                if (targetOwners.TryGetValue(target.Entity.ID, out ushort owner))
                {
                    displacedControllers.Add(owner);
                }
            }

            foreach (ushort owner in displacedControllers)
            {
                RemoveGroup(owner, notifyClients: false);
            }

            foreach (MotionTarget target in incoming.Targets)
            {
                targetOwners[target.Entity.ID] = incoming.Controller.ID;
                clientTargetRevisions[target.Entity.ID] = revision;
            }
        }
    }
}

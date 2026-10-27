using Barotrauma.Items.Components;
using FarseerPhysics;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 统一运动系统的旋转实现。
    /// 与平移共用任务容器、占用检查、更新循环和网络快照。
    /// </summary>
    public static partial class TLLinkedMotionSystem
    {
        /// <summary>
        /// 接收旋转命令。
        ///
        /// 首次调用记录原位、初始角度、旋转中心及目标列表。
        /// 后续命令只改变目标方向和角速度。
        ///
        /// goal：
        /// true 前往配置角度；
        /// false 返回初始姿态；
        /// null 切换当前目标。
        /// </summary>
        public static bool CommandRotation(Item controller, Vector2 pivot, float angleDegrees, float angularSpeed, bool rotateWalls, bool rotateItems, bool? goal)
        {
            if (!IsAuthority ||
                !EnsureRound() ||
                controller is null ||
                controller.Removed ||
                !IsValidVector(pivot) ||
                !float.IsFinite(angleDegrees) ||
                MathF.Abs(angleDegrees) < 0.001f ||
                MathF.Abs(angleDegrees) > 360f ||
                !float.IsFinite(angularSpeed) ||
                angularSpeed <= 0f)
            {
                return false;
            }
            // 已完成并被交接的任务，不因相同持续 set_in 再次抢回目标。
            if (IsRepeatedHandedOffCommand(controller, goal)) return true;
            bool created = false;
            if (!groups.TryGetValue(controller.ID, out MotionGroup? group))
            {
                group = BuildRotationGroup(
                    controller,
                    pivot,
                    angleDegrees,
                    angularSpeed,
                    rotateWalls,
                    rotateItems);

                if (group is null)
                {
                    return false;
                }

                groups.Add(controller.ID, group);
                created = true;
            }
            // 已有平移任务不能被旋转命令修改。
            if (!group.IsRotation)
            {
                return false;
            }
            // 沿用原有的实体有效性检查和释放流程。
            if (!ReferenceEquals(group.Controller, controller) ||
                !IsGroupValid(group))
            {
                RemoveGroup(controller.ID, notifyClients: true);
                return false;
            }
            float newGoal = goal.HasValue
                ? (goal.Value ? 1f : 0f)
                : (group.Goal == 0f ? 1f : 0f);
            // 持续 set_in 不重复重建任务或广播同一命令。
            if (!created &&
                group.Goal == newGoal &&
                group.Speed == angularSpeed)
            {
                return true;
            }
            group.Goal = newGoal;
            group.Speed = angularSpeed;
            RememberAcceptedGoal(group);

            // 保留当前进度，实现旋转中途反向。
            SendSnapshot(group, deleted: false);
            group.SyncTimer = GetSyncInterval(group);
            return true;
        }
        /// <summary>
        /// 创建旋转组，并记录所有目标的初始姿态。
        /// 任一选中目标无效时拒绝整组，避免只旋转部分结构。
        /// </summary>
        private static MotionGroup? BuildRotationGroup(Item controller, Vector2 pivot, float angleDegrees, float angularSpeed, bool rotateWalls, bool rotateItems)
        {
            MotionGroup group = new()
            {
                Controller = controller,
                CoordinateSubmarine = controller.Submarine,

                // Offset 是现有类的 required 属性。
                // 旋转任务不使用平移 Offset，因此设为零。
                Offset = Vector2.Zero,

                Speed = angularSpeed,
                IsRotation = true,
                Pivot = pivot,
                AngleDegrees = angleDegrees,
                Progress = 0f,
                Goal = 0f
            };
            HashSet<ushort> seen = new();
            foreach (MapEntity? entity in controller.linkedTo)
            {
                if (entity is null ||
                    entity.Removed ||
                    ReferenceEquals(entity, controller) ||
                    !seen.Add(entity.ID))
                {
                    continue;
                }

                bool selected =
                    (rotateWalls && entity is Structure) ||
                    (rotateItems && entity is Item);

                if (!selected)
                {
                    continue;
                }

                if (!ReferenceEquals(entity.Submarine, controller.Submarine) ||
                    !IsValidVector(entity.Position) ||
                    !CanRotateTarget(entity) ||
                    group.Targets.Count >= MaxTargetsPerController)
                {
                    return null;
                }

                float homeRotation = GetInitialDisplayRotation(entity);

                if (!float.IsFinite(homeRotation))
                {
                    return null;
                }

                group.Targets.Add(new MotionTarget
                {
                    Entity = entity,
                    Home = entity.Position,
                    HomeRotation = homeRotation
                });
            }

            if (group.Targets.Count == 0)
            {
                return null;
            }

            // 从其他任务改变后的当前姿态接续，避免恢复旧轨迹时瞬移。
            PrepareReacquiredGroupPose(group);
            return TryClaimGroupTargets(group) ? group : null;
        }
        /// <summary>
        /// 判断当前版本是否支持旋转此目标。
        /// 平移适配成功，不代表附属物理体也支持任意角度旋转。
        /// </summary>
        private static bool CanRotateTarget(MapEntity entity)
        {
            if (entity is Structure)
            {
                return true;
            }

            if (entity is not Item rotatingItem ||
                rotatingItem.ParentInventory is not null ||
                !rotatingItem.Prefab.AllowRotatingInEditor)
            {
                return false;
            }

            // 这些组件需要额外的旋转适配，当前整组拒绝。
            if (rotatingItem.GetComponent<Door>() is not null ||
                rotatingItem.GetComponent<Turret>() is not null ||
                rotatingItem.GetComponent<TriggerComponent>() is not null)
            {
                return false;
            }

            return true;
        }
        /// <summary>
        /// 记录统一的“显示角度”，单位为弧度。
        ///
        /// 有主物理体的物品实际朝向以物理体为准。
        /// 物理角度与屏幕显示角度方向相反，因此取负号。
        /// </summary>
        private static float GetInitialDisplayRotation(MapEntity entity)
        {
            if (entity is Item rotatingItem &&
                rotatingItem.body is not null)
            {
                return -rotatingItem.body.Rotation;
            }

            return entity.RotationRad;
        }
        /// <summary>
        /// 根据共享进度计算整组目标的位置和朝向。
        /// 每次从 Home 和 HomeRotation 计算，不累计旋转差值。
        /// </summary>
        private static void ApplyRotationPositions(MotionGroup group)
        {
            float angle = MathHelper.ToRadians(
                group.AngleDegrees * group.Progress);

            float cosine = MathF.Cos(angle);
            float sine = MathF.Sin(angle);

            foreach (MotionTarget target in group.Targets)
            {
                Vector2 relative = target.Home - group.Pivot;

                // 局部坐标正 Y 朝上，标准旋转矩阵的正角度为逆时针。
                Vector2 rotated = new(
                    relative.X * cosine - relative.Y * sine,
                    relative.X * sine + relative.Y * cosine);

                Vector2 desiredPosition = group.Pivot + rotated;

                // 游戏显示角度采用相反方向：
                // 场景中逆时针旋转时，显示角度需要减小。
                float desiredDisplayRotation =
                    target.HomeRotation - angle;

                if (target.Entity is Structure wall)
                {
                    ApplyRotatingWall(
                        wall,
                        desiredPosition,
                        desiredDisplayRotation);
                }
                else if (target.Entity is Item rotatingItem)
                {
                    ApplyRotatingItem(
                        rotatingItem,
                        desiredPosition,
                        desiredDisplayRotation);
                }
            }
        }
        /// <summary>
        /// 更新结构的中心位置和朝向。
        ///
        /// 使用原版 Rotation 属性，让结构更新分段及碰撞几何。
        /// 该属性可能重建结构物理体，开销高于单纯平移。
        /// </summary>
        private static void ApplyRotatingWall(Structure wall,Vector2 desiredPosition,float desiredDisplayRotation)
        {
            bool rotationChanged = MathF.Abs(
                MathHelper.WrapAngle(
                    desiredDisplayRotation - wall.RotationRad)) > 0.00001f;

            // 即使结构中心不动，旋转也可能移走其支撑面。
            if (rotationChanged)
            {
                WakeBodiesTouchingWall(wall);
            }

            // 复用已验证过的整数位移和角色地面刷新。
            ApplyWallPosition(wall, desiredPosition);

            if (rotationChanged)
            {
                wall.Rotation = MathHelper.ToDegrees(
                    desiredDisplayRotation);
            }
        }
        /// <summary>
        /// 更新普通物品的中心位置、显示角度和主物理体角度。
        /// 灯的位置偏移和光源朝向也通过原版接口刷新。
        /// </summary>
        private static void ApplyRotatingItem(Item rotatingItem,Vector2 desiredPosition,float desiredDisplayRotation)
        {
            // 复用平移适配，通知现有位置相关组件。
            ApplyItemPosition(rotatingItem, desiredPosition);

            // Rotation 属性使用度，主要维护物品显示角度。
            rotatingItem.Rotation = MathHelper.ToDegrees(
                desiredDisplayRotation);

            if (rotatingItem.body is not null)
            {
                // SetTransform 使用物理弧度，方向与显示角度相反。
                rotatingItem.SetTransform(
                    ConvertUnits.ToSimUnits(desiredPosition),
                    -desiredDisplayRotation,
                    findNewHull: true,
                    setPrevTransform: false,
                    forceSubmarine: rotatingItem.Submarine);

                rotatingItem.body.Submarine = rotatingItem.Submarine;
                rotatingItem.body.LinearVelocity = Vector2.Zero;
                rotatingItem.body.AngularVelocity = 0f;
            }

            // 不仅平移光源，还需要重新计算旋转后的 LightOffset。
            foreach (LightComponent light in
                     rotatingItem.GetComponents<LightComponent>())
            {
                light.SetLightSourceTransform();
            }
        }
    }
}

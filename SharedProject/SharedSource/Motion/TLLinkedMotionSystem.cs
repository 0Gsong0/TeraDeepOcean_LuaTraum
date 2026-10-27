using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;

namespace TeraDeepOcean
{
    /// <summary>
    /// 被 link 的墙体和物品的统一运动系统。
    /// </summary>
    public static partial class TLLinkedMotionSystem
    {
        // 一个控制器最多控制的物品和墙体数量。
        private const int MaxTargetsPerController = 128;
        // 运动中向客户端发送快照的间隔，单位为秒。
        private const float MovingSyncInterval = 0.25f;
        // 空闲时向客户端发送快照的间隔，单位为秒。
        private const float IdleSyncInterval = 2f;
        /// <summary>
        /// 服务器发送消息时使用的递增序号。
        /// 使用统一序号，避免同一控制器释放后重建任务时序号倒退。
        /// </summary>
#if SERVER
        private static long nextRevision;
#endif
        /// <summary>
        /// 以控制器物品 ID 为键，保存它对应的运动组。
        /// </summary>
        private static readonly Dictionary<ushort, MotionGroup> groups = new();
        /// <summary>
        /// 以目标实体 ID 为键，记录哪个控制器占用了该目标。
        /// 一个目标在同一回合内只接受一个控制器。
        /// </summary>
        private static readonly Dictionary<ushort, ushort> targetOwners = new();
        /// <summary>
        /// 客户端已收到的最新快照序号。
        /// 删除任务后仍保留序号，以免旧消息重新创建已删除的任务。
        /// </summary>
        private static readonly Dictionary<ushort, long> clientRevisions = new();
        /// <summary>
        /// 客户端等待应用的快照。
        /// 网络回调只保存数据，真正的位置修改放到 Update 中执行。
        /// 目标尚未加载完成时，也可以暂存在这里等待。
        /// </summary>
        private static readonly Dictionary<ushort, MotionSnapshot> pendingSnapshots = new();
        private static GameSession? currentSession;
        private static Level? currentLevel;
        /// <summary>游戏更新次数。它是所有控制器共用的时钟；</summary>
        public static long Tick { get; private set; }
        /// <summary>
        /// 本次检查中，由平台直接支撑的动态物品。
        /// 同一个物品可能接触多个平台碰撞体，使用集合避免重复更新。
        /// </summary>
        private static readonly HashSet<Item> platformSupportedItems = new();

        /// <summary>
        /// 本次检查中，被控制器直接移动的物品。
        /// 这些物品由 ApplyItemPosition 维护，不参与自由物品校正。
        /// 在客户端和服务器都根据运动组重新收集。
        /// </summary>
        private static readonly HashSet<Item> directlyControlledItems = new();

        /// <summary>
        /// 支撑接触法线的最低向上分量，用于排除侧面和下方接触。
        /// </summary>
        private const float MinimumSupportNormalY = 0.3f;

        /// <summary>
        /// 允许的坐标误差平方：4 表示 2 个显示单位。
        /// 避免整数矩形的正常取整误差导致反复更新。
        /// </summary>
        private const float ItemRectErrorToleranceSquared = 4f;
        /// <summary>单人游戏或多人服务器具有运动决策权。</summary>
        public static bool IsAuthority => !GameMain.IsMultiplayer || GameMain.NetworkMember is { IsServer: true };
        /// <summary>
        /// 一个被控制的目标及首次启动时记录的原位。
        /// 坐标采用目标所属潜艇的局部显示坐标。
        /// </summary>
        private sealed class MotionTarget
        {
            public required MapEntity Entity { get; init; }
            public required Vector2 Home { get; init; }
        }
        /// <summary>
        /// 一个控制器所管理的整组目标。
        /// 所有目标共享 Progress，因此保持相同的运动进度。
        /// </summary>
        private sealed class MotionGroup
        {
            /// <summary>
            /// 控制器对象。
            /// 保存对象引用，可以发现控制器被删除或 ID 被其他实体复用。
            /// </summary>
            public required Item Controller { get; init; }

            /// <summary>
            /// 建立任务时使用的潜艇坐标空间。
            /// null 表示实体位于世界坐标空间。
            /// </summary>
            public Submarine? CoordinateSubmarine { get; init; }

            /// <summary>
            /// 从原位到目标位的总位移。
            /// 任务建立后固定，避免运动中修改配置导致轨迹突然变化。
            /// </summary>
            public required Vector2 Offset { get; init; }

            /// <summary>速度，单位为像素/游戏秒。</summary>
            public required float Speed { get; set; }

            /// <summary>
            /// 当前进度。
            /// 0 表示原位，1 表示目标位，0.5 表示走了一半。
            /// </summary>
            public float Progress { get; set; }

            /// <summary>
            /// 希望到达的进度。
            /// 本版只使用 0 和 1，分别表示返回和前往。
            /// </summary>
            public float Goal { get; set; }

            /// <summary>距离下一次发送快照还有多少游戏秒。</summary>
            public float SyncTimer { get; set; }

            /// <summary>组内全部受控目标。</summary>
            public List<MotionTarget> Targets { get; } = new();
        }
        /// <summary>
        /// 网络快照中一个目标的 ID 和原位。
        /// 使用普通数值属性，便于 JSON 序列化。
        /// </summary>
        public sealed class TargetSnapshot
        {
            public ushort Id { get; set; }
            public float HomeX { get; set; }
            public float HomeY { get; set; }
        }
        /// <summary>
        /// 服务器发给客户端的完整运动状态。
        /// 客户端利用这些数据重建或校正运动组。
        /// </summary>
        public sealed class MotionSnapshot
        {
            /// <summary>控制器物品 ID。</summary>
            public ushort ControllerId { get; set; }

            /// <summary>消息序号，较大的序号表示较新的状态。</summary>
            public long Revision { get; set; }

            /// <summary>是否通知客户端删除该运动组。</summary>
            public bool Deleted { get; set; }

            /// <summary>服务器当前的运动进度。</summary>
            public float Progress { get; set; }

            /// <summary>服务器指定的目标进度。</summary>
            public float Goal { get; set; }

            /// <summary>服务器使用的移动速度。</summary>
            public float Speed { get; set; }

            /// <summary>总水平位移。</summary>
            public float OffsetX { get; set; }

            /// <summary>总垂直位移。</summary>
            public float OffsetY { get; set; }

            /// <summary>目标实体及原位列表。</summary>
            public List<TargetSnapshot> Targets { get; set; } = new();
        }
        /// <summary>
        /// 接收控制器命令。
        ///
        /// goal == true：前往目标位。
        /// goal == false：返回原位。
        /// goal == null：切换当前目标。
        ///
        /// 返回 true 表示命令被接受。
        /// 返回 false 表示配置无效、没有目标或发生控制权冲突。
        ///
        /// 既有任务会保留首次记录的 Offset 和目标列表，
        /// 但允许通过下一次命令更新 Speed。
        /// </summary>
        public static bool Command(Item controller, Vector2 offset, float speed, bool moveWalls, bool moveItems, bool? goal)
        {
            if (!IsAuthority || !EnsureRound() || controller is null || controller.Removed || !IsValidVector(offset) || !float.IsFinite(speed) || speed <= 0f || !float.IsFinite(offset.LengthSquared()) || offset.LengthSquared() < 0.0001f) return false;
            bool created = false;
            if (!groups.TryGetValue(controller.ID, out MotionGroup? group))
            {
                group = BuildGroup(controller, offset, speed, moveWalls, moveItems);
                if (group is null) return false;
                groups.Add(controller.ID, group);
                created = true;
            }
            // 原任务引用的控制器或目标失效时，先释放任务。
            // 本次不自动重建，防止误把当前位置记录成新的原位。
            if (!ReferenceEquals(group.Controller, controller) || !IsGroupValid(group))
            {
                RemoveGroup(controller.ID, notifyClients: true);
                return false;
            }
            float newGoal = goal.HasValue ? (goal.Value ? 1f : 0f) : (group.Goal == 0f ? 1f : 0f);
            // 持续 set_in 不需要每次都发送网络消息。
            if (!created && group.Goal == newGoal && group.Speed == speed) return true;
            group.Goal = newGoal;
            group.Speed = speed;
            // 不改变 Progress，因此运动中反向时会从当前位置返回。
            SendSnapshot(group, deleted: false);
            group.SyncTimer = GetSyncInterval(group);
            return true;
        }
        /// <summary>
        /// 读取 link 列表并创建运动组。
        ///
        /// 只处理直接 link 的目标，不递归寻找它们继续 link 的实体。
        /// 目标必须与控制器处于相同潜艇坐标空间。
        ///
        /// 若选中的目标已被其他控制器占用，拒绝创建整个运动组，
        /// </summary>
        private static MotionGroup? BuildGroup(Item controller, Vector2 offset, float speed, bool moveWalls, bool MoveItems)
        {
            MotionGroup group = new()
            {
                Controller = controller,
                CoordinateSubmarine = controller.Submarine,
                Offset = offset,
                Speed = speed,
                Progress = 0f,
                Goal = 0f,
            };
            HashSet<ushort> seen = new();
            foreach (MapEntity? entity in controller.linkedTo)
            {
                if (entity is null || entity.Removed || ReferenceEquals(entity, controller) || !seen.Add(entity.ID)) continue;
                bool Accepted = (moveWalls && entity is Structure) || (MoveItems && entity is Item);
                if (!Accepted) continue;
                if (!ReferenceEquals(entity.Submarine, controller.Submarine) || entity is Item { ParentInventory: not null } || targetOwners.ContainsKey(entity.ID) || !IsValidVector(entity.Position)) return null;
                if (group.Targets.Count >= MaxTargetsPerController) return null;
                group.Targets.Add(new MotionTarget
                {
                    Entity = entity,
                    Home = entity.Position
                });
            }
            if (group.Targets.Count == 0) return null;
            // 所有目标都通过检查后才登记占用，避免创建失败后留下残留记录。
            foreach (MotionTarget target in group.Targets)
            {
                targetOwners.Add(target.Entity.ID, controller.ID);
            }
            return group;
        }
        /// <summary>
        /// 每次 GameSession.Update 后调用一次。
        ///
        /// deltaTime 是本次经过的游戏时间，而不是固定写死的 1/60。
        /// 如果其他模组正确调整了 deltaTime，移动仍按相同游戏速度推进。
        /// 如果其他模组让整个游戏时间变慢，这套系统会随游戏一起变慢。
        /// </summary>
        public static void Update(float deltaTime)
        {
            if (!EnsureRound() || !float.IsFinite(deltaTime) || deltaTime <= 0f) return;
            Tick++;
            // 在游戏更新位置统一应用收到的网络数据。
            ApplyPendingSnapshots();
            List<ushort> invalidGroups = new();
            foreach (KeyValuePair<ushort, MotionGroup> pair in groups)
            {
                MotionGroup group = pair.Value;
                if (!IsGroupValid(group))
                {
                    invalidGroups.Add(pair.Key);
                    continue;
                }
                bool wasMoving = group.Progress != group.Goal;
                Advance(group, deltaTime);
                ApplyPositions(group);
                if (!IsAuthority) continue;
                bool justArrived = wasMoving && group.Progress == group.Goal;
                group.SyncTimer -= deltaTime;
                // 到达时立即同步终点，平时按间隔同步。
                if (justArrived || group.SyncTimer <= 0f)
                {
                    SendSnapshot(group, deleted: false);
                    group.SyncTimer = GetSyncInterval(group);
                }
            }
            // 不在遍历 groups 的过程中删除它的元素。
            foreach (ushort id in invalidGroups)
            {
                RemoveGroup(id, notifyClients: IsAuthority);
            }
            // 临时诊断：检查自由物品的交互矩形。
            RefreshPlatformItemRects();
        }
        /// <summary>
        /// 检查控制器和全部目标是否仍适合继续受控。
        /// 任一目标被删除、进入库存或改变所属潜艇时，整组停止接管。
        /// </summary>
        private static bool IsGroupValid(MotionGroup group)
        {
            if (group.Controller.Removed || !ReferenceEquals(group.Controller.Submarine, group.CoordinateSubmarine) || !ReferenceEquals(Entity.FindEntityByID(group.Controller.ID), group.Controller)) return false;
            foreach (MotionTarget target in group.Targets)
            {
                if (target.Entity.Removed || !ReferenceEquals(target.Entity.Submarine, group.CoordinateSubmarine) || !ReferenceEquals(Entity.FindEntityByID(target.Entity.ID), target.Entity) || target.Entity is Item { ParentInventory: not null }) return false;
            }
            return group.Targets.Count > 0;
        }
        /// <summary>
        /// 根据速度推进进度。
        ///
        /// 例如总路程 200 像素，速度 100 像素/秒：
        /// 每经过 1 秒，Progress 增加 100 / 200，也就是 0.5。
        /// </summary>
        private static void Advance(MotionGroup group, float deltaTime)
        {
            if (group.Progress == group.Goal) return;

            float distance = group.Offset.Length();
            float step = group.Speed * deltaTime / distance;

            // Min/Max 将最后一步限制在终点，避免越过后再反复修正。
            group.Progress = group.Goal > group.Progress
                ? MathF.Min(group.Goal, group.Progress + step)
                : MathF.Max(group.Goal, group.Progress - step);
        }
        /// <summary>
        /// 由原位、总位移和共享进度计算各实体的位置。
        ///
        /// 使用绝对计算结果，避免反复累加小位移造成误差。
        /// 停靠后仍执行校准，因此受控物品不会主动恢复自由运动。
        /// </summary>
        private static void ApplyPositions(MotionGroup group)
        {
            Vector2 offsetNow = group.Offset * group.Progress;
            foreach (MotionTarget target in group.Targets)
            {
                Vector2 desired = target.Home + offsetNow;

                if (target.Entity is Structure wall)
                {
                    ApplyWallPosition(wall, desired);
                }
                else if (target.Entity is Item movingItem)
                {
                    ApplyItemPosition(movingItem, desired);
                }
            }
        }
        /// <summary>
        /// 移动墙体。
        ///
        /// 墙体矩形只能记录整数坐标，因此提交整数位移。
        /// 小数进度保存在运动组里，不会因本次不足一像素而丢失。
        /// 非整数终点会落到最近的、墙体能够表示的位置。
        ///
        /// Move(false) 不等于自动处理沿途碰撞，也不会自动移动房间 Hull。
        /// </summary>
        private static void ApplyWallPosition(Structure wall, Vector2 desired)
        {
            Vector2 difference = desired - wall.Position;
            Vector2 amount = new(MathF.Round(difference.X), MathF.Round(difference.Y));
            if (amount != Vector2.Zero)
            {
                WakeBodiesTouchingWall(wall);
                wall.Move(amount, ignoreContacts: false);
            }
            // 再通知角色重新检测地面。
            // 这里不会直接移动角色，也不会把角色绑定到平台上。
            foreach (Character character in Character.CharacterList)
            {
                if (character.Removed) continue;
                // 只处理同一潜艇坐标空间中的角色。
                // 双方 Submarine 都为 null 时，表示处于世界坐标空间。
                if (!ReferenceEquals(character.Submarine, wall.Submarine)) continue;
                character.AnimController?.ForceRefreshFloorY();
            }
        }
        /// <summary>
        /// 移动物品本身和它的物理体。
        ///
        /// desired 使用显示坐标；SetTransform 接受物理模拟坐标，
        /// 因此必须先通过 ConvertUnits 转换。
        ///
        /// setPrevTransform 为 false，允许游戏沿用正常的位置插值。
        /// 这仍然是强制位置控制，不能保证高速移动的连续碰撞。
        ///
        /// 此方法只适配门和灯，不代表其他复杂组件都已经支持。
        /// </summary>
        private static void ApplyItemPosition(Item movingItem, Vector2 desired)
        {
            Submarine? coordinateSubmarine = movingItem.Submarine;
            // 已经处于目标位置时不重复刷新位置和所在房间。
            if (Vector2.DistanceSquared(movingItem.Position, desired) > 0.000001f)
            {
                // 保存移动前的实际位置和矩形位置。
                // 对有主物理体的物品，Position 可以包含小数；
                // 门和 Gap 的位置则需要与物品整数矩形保持一致。
                Vector2 oldPosition = movingItem.Position;
                var oldRect = movingItem.Rect;

                Door? door = movingItem.GetComponent<Door>();
                // 必须在物品移动前取得 Gap。
                // LinkedGap 在尚不存在时，可能按物品当前位置创建缺口。
                Gap? doorGap = door?.LinkedGap;

                movingItem.SetTransform(ConvertUnits.ToSimUnits(desired), movingItem.RotationRad, findNewHull: true, setPrevTransform: false, forceSubmarine: coordinateSubmarine);
                // FindHull 可能更新物品和物理体的潜艇引用。
                // 这里保留本次运动采用的坐标空间，避免下一步误用世界坐标。
                movingItem.Submarine = coordinateSubmarine;

                if (movingItem.body is not null)
                {
                    movingItem.body.Submarine = coordinateSubmarine;
                }
                // 使用移动后的实际位置计算光源位移，
                // 避免无主物理体物品的整数取整造成累计偏差。
                Vector2 actualMovement = movingItem.Position - oldPosition;
                var newRect = movingItem.Rect;
                // 门碰撞体和 Gap 跟随物品矩形的实际整数位移。
                Vector2 rectangleMovement = new(newRect.X - oldRect.X, newRect.Y - oldRect.Y);
                if (door is not null && rectangleMovement != Vector2.Zero)
                {
                    // Gap.Move 会重新检查连接的 Hull。
                    // 不直接设置 Gap.Rect，避免漏掉房间连接更新。
                    if (doorGap is not null && !doorGap.Removed)
                    {
                        doorGap.Move(rectangleMovement, ignoreContacts: false);
                        // 保持门状态及缺口方向等关联信息一致。
                        door.RefreshLinkedGap();
                    }
                    // 只调用门组件的移动接口，不再次移动 Item 本体。
                    // 在 Gap 更新后执行，让遮光轮廓使用更新后的关联信息。
                    door.Move(rectangleMovement, ignoreContacts: false);
                }
                if (actualMovement != Vector2.Zero)
                {
                    // 一个物品可能包含多个灯光组件，因此全部处理。
                    // LightComponent.Move 内部有 CLIENT 条件编译，
                    // 这里可以放在客户端和服务器共用的代码中。
                    foreach (LightComponent light in movingItem.GetComponents<LightComponent>())
                    {
                        light.Move(actualMovement, ignoreContacts: false);
                    }
                }
                // 保留原有的受控物品位置控制行为。
                if (movingItem.body is not null)
                {
                    movingItem.body.Submarine = coordinateSubmarine;
                    movingItem.body.LinearVelocity = Vector2.Zero;
                    movingItem.body.AngularVelocity = 0f;
                }
            }
        }
        /// <summary>
        /// 唤醒与墙体存在接触记录的动态刚体。
        /// 此方法只唤醒动态刚体，不改变刚体类型、位置或速度。
        /// </summary>
        private static void WakeBodiesTouchingWall(Structure wall)
        {
            if (wall.Bodies is null) return;
            foreach (var wallBody in wall.Bodies)
            {
                // ContactList 是链表：
                // Other 表示接触另一端的刚体，Next 表示下一条记录。
                for (var edge = wallBody.ContactList; edge is not null; edge = edge.Next)
                {
                    var otherBody = edge.Other;
                    if (otherBody.BodyType != BodyType.Dynamic) continue;
                    // 恢复物理模拟，让物体能够响应重力和支撑面的变化。
                    otherBody.Awake = true;
                }
            }
        }
        /// <summary>
        /// 校正受控平台直接支撑的动态物品的交互矩形。
        ///
        /// 不扫描全部物品，也不查找物品之间的支撑关系。
        /// 停靠后仍调用，确保物品最终落稳后交互位置正确。
        /// </summary>
        private static void RefreshPlatformItemRects()
        {
            platformSupportedItems.Clear();
            directlyControlledItems.Clear();

            // 收集受控物品，以及平台直接支撑的自由物品。
            foreach (MotionGroup group in groups.Values)
            {
                foreach (MotionTarget target in group.Targets)
                {
                    // 直接受控物品需要排除，由原有移动代码维护。
                    if (target.Entity is Item controlledItem)
                    {
                        directlyControlledItems.Add(controlledItem);
                        continue;
                    }

                    if (target.Entity is not Structure wall || wall.Removed || wall.Bodies is null)
                    {
                        continue;
                    }

                    foreach (var wallBody in wall.Bodies)
                    {
                        if (!wallBody.Enabled)
                        {
                            continue;
                        }

                        // 只遍历这个平台碰撞体的接触列表。
                        for (var edge = wallBody.ContactList;
                             edge is not null;
                             edge = edge.Next)
                        {
                            var contact = edge.Contact;
                            var otherBody = edge.Other;

                            // 包围盒接近不代表真实接触；
                            // 禁用接触和传感器也不能作为支撑依据。
                            if (!contact.Enabled ||
                                !contact.IsTouching ||
                                contact.FixtureA.IsSensor ||
                                contact.FixtureB.IsSensor)
                            {
                                continue;
                            }

                            // 不检查 Awake：休眠物品同样需要维护交互位置。
                            if (!otherBody.Enabled ||
                                otherBody.BodyType != BodyType.Dynamic ||
                                otherBody.UserData is not Item looseItem)
                            {
                                continue;
                            }

                            if (looseItem.Removed ||
                                looseItem.ParentInventory is not null ||
                                looseItem.body is null ||
                                !looseItem.body.Enabled ||
                                !ReferenceEquals(looseItem.body.FarseerBody, otherBody))
                            {
                                continue;
                            }

                            // 法线原本从 FixtureA 指向 FixtureB。
                            // 统一为“从平台指向物品”，再判断是否向上支撑。
                            contact.GetWorldManifold(out Vector2 normal, out _);

                            if (!ReferenceEquals(contact.FixtureA.Body, wallBody))
                            {
                                normal = -normal;
                            }

                            if (normal.Y < MinimumSupportNormalY)
                            {
                                continue;
                            }

                            platformSupportedItems.Add(looseItem);
                        }
                    }
                }
            }

            // 接触列表全部遍历完后再校正。
            // 避免 UpdateTransform 的坐标空间处理影响接触遍历。
            foreach (Item looseItem in platformSupportedItems)
            {
                if (directlyControlledItems.Contains(looseItem))
                {
                    continue;
                }

                Vector2 physicalPosition =
                    ConvertUnits.ToDisplayUnits(looseItem.body.SimPosition);

                var itemRect = looseItem.Rect;

                // 矩形 Y 为上边缘，所以中心 Y 要减去半高。
                Vector2 rectangleCenter = new(
                    itemRect.X + itemRect.Width * 0.5f,
                    itemRect.Y - itemRect.Height * 0.5f);

                if (Vector2.DistanceSquared(physicalPosition, rectangleCenter)
                    <= ItemRectErrorToleranceSquared)
                {
                    continue;
                }

                // 使用原版方法更新矩形和所在房间。
                // 不修改速度，也不主动唤醒物品。
                looseItem.UpdateTransform();
            }

            // 清除实体引用，保留集合容量以便复用。
            platformSupportedItems.Clear();
            directlyControlledItems.Clear();
        }
        /// <summary>取得当前运动状态所使用的同步间隔。</summary>
        private static float GetSyncInterval(MotionGroup group)
        {
            return group.Progress == group.Goal ? IdleSyncInterval : MovingSyncInterval;
        }
        /// <summary>
        /// 主动释放一个控制器管理的目标。
        /// 保留实体的当前位置，不自动把它们传送回原位。
        ///
        /// 本版没有把它接到电路输入；以后可以由其他 C# 功能调用。
        /// 释放后再次 Command，会把当时的位置作为新的原位。
        /// </summary>
        public static void Release(Item controller)
        {
            if (IsAuthority && controller is not null && !controller.Removed)
            {
                RemoveGroup(controller.ID, notifyClients: true);
            }
        }
        /// <summary>删除任务并释放目标占用记录。</summary>
        private static void RemoveGroup(ushort controllerId, bool notifyClients)
        {
            if (!groups.Remove(controllerId, out MotionGroup? group)) return;
            foreach (MotionTarget target in group.Targets)
            {
                if (targetOwners.TryGetValue(target.Entity.ID, out ushort owner) && owner == controllerId)
                {
                    targetOwners.Remove(target.Entity.ID);
                }
            }
            if (notifyClients)
            {
                SendSnapshot(group, deleted: true);
            }
        }
        /// <summary>
        /// 检查当前是否有运行中的关卡，并发现会话或关卡切换。
        /// 编辑器中尚未启动测试回合时，不创建运动任务。
        /// </summary>
        private static bool EnsureRound()
        {
            GameSession? session = GameMain.GameSession;
            Level? level = session?.Level;
            if (session is null || !session.IsRunning)
            {
                Clear();
                return false;
            }
            if (!ReferenceEquals(currentLevel, level) || !ReferenceEquals(currentSession, session))
            {
                Clear();
                currentSession = session;
                currentLevel = level;
            }
            return true;
        }
        /// <summary>
        /// 清空运行状态，供换回合或插件卸载时调用。
        ///
        /// 不重置 Tick 和 nextRevision，让计数保持递增，
        /// 避免仍存活的控制器把新计数误认成之前的某一次更新。
        /// </summary>
        public static void Clear()
        {
            groups.Clear();
            targetOwners.Clear();
            clientRevisions.Clear();
            pendingSnapshots.Clear();
            currentSession = null;
            currentLevel = null;
            platformSupportedItems.Clear();
            directlyControlledItems.Clear();
        }
        /// <summary>检查二维坐标是否含有 NaN 或无穷大。</summary>
        private static bool IsValidVector(Vector2 value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y);
        }
        /// <summary>
        /// 将运动组转换成 JSON，通过官方 C# 网络服务广播。
        /// 运动中和静止时都定期发送，迟加入的客户端也能恢复状态。
        /// </summary>
        private static void SendSnapshot(MotionGroup group, bool deleted)
        {
#if SERVER
            if (!GameMain.IsMultiplayer || !IsAuthority) return;
            MotionSnapshot snapshot = new()
            {
                ControllerId = group.Controller.ID,
                Revision = ++nextRevision,
                Deleted = deleted,
                Progress = group.Progress,
                Goal = group.Goal,
                Speed = group.Speed,
                OffsetX = group.Offset.X,
                OffsetY = group.Offset.Y
            };
            if (!deleted)
            {
                foreach (MotionTarget target in group.Targets)
                {
                    snapshot.Targets.Add(new TargetSnapshot
                    {
                        Id = target.Entity.ID,
                        HomeX = target.Home.X,
                        HomeY = target.Home.Y
                    });
                }
            }
            SendSnapShotToClient(null, TLNetworkHeader.MotionServerSendSnapshot, JsonSerializer.Serialize(snapshot));
#endif
        }
        /// <summary>
        /// C# 网络处理函数在客户端收到 JSON 后调用此方法。
        ///
        /// 这里只校验、排序和暂存消息。
        /// 真正修改游戏实体的位置在下一次 Update 中进行。
        /// </summary>
        public static void ClientReceiveSnapshot(string json)
        {
            if (!GameMain.IsMultiplayer || IsAuthority || !EnsureRound() || string.IsNullOrWhiteSpace(json)) return;
            MotionSnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<MotionSnapshot>(json);
            }
            catch (JsonException)
            {
                return;
            }
            if (snapshot is null || !IsSnapshotValid(snapshot)) return;
            if (clientRevisions.TryGetValue(snapshot.ControllerId, out long oldRevision) && snapshot.Revision <= oldRevision) return;
            // 相同控制器只保留最新的一份待处理快照。
            clientRevisions[snapshot.ControllerId] = snapshot.Revision;
            pendingSnapshots[snapshot.ControllerId] = snapshot;
        }
        /// <summary>
        /// 检查网络数据的基本有效性。
        /// 删除消息不需要有效的运动配置或目标列表。
        /// </summary>
        private static bool IsSnapshotValid(MotionSnapshot snapshot)
        {
            if (snapshot.Revision <= 0) return false;
            if (snapshot.Deleted) return true;
            Vector2 offset = new(snapshot.OffsetX, snapshot.OffsetY);
            if (!IsValidVector(offset) ||
                !float.IsFinite(offset.LengthSquared()) ||
                offset.LengthSquared() < 0.0001f ||
                !float.IsFinite(snapshot.Speed) ||
                snapshot.Speed <= 0f ||
                !float.IsFinite(snapshot.Progress) ||
                snapshot.Progress < 0f ||
                snapshot.Progress > 1f ||
                (snapshot.Goal != 0f && snapshot.Goal != 1f) ||
                snapshot.Targets is null ||
                snapshot.Targets.Count == 0 ||
                snapshot.Targets.Count > MaxTargetsPerController)
            {
                return false;
            }
            HashSet<ushort> ids = new();
            foreach (TargetSnapshot? target in snapshot.Targets)
            {
                if (target is null ||
                    target.Id == snapshot.ControllerId ||
                    !ids.Add(target.Id) ||
                    !float.IsFinite(target.HomeX) ||
                    !float.IsFinite(target.HomeY))
                {
                    return false;
                }
            }
            return true;
        }
        /// <summary>
        /// 在客户端游戏更新中应用最新快照。
        /// 尚未找到全部实体的快照继续保留，等待实体加载。
        /// </summary>
        private static void ApplyPendingSnapshots()
        {
            if (IsAuthority || pendingSnapshots.Count == 0) return;
            List<ushort> applied = new();
            foreach (KeyValuePair<ushort, MotionSnapshot> pair in pendingSnapshots)
            {
                MotionSnapshot snapshot = pair.Value;
                if (snapshot.Deleted)
                {
                    RemoveGroup(snapshot.ControllerId, notifyClients: false);
                    applied.Add(pair.Key);
                    continue;
                }
                MotionGroup? group = RestoreGroup(snapshot);
                if (group is null) continue;
                groups[snapshot.ControllerId] = group;
                ApplyPositions(group);
                applied.Add(pair.Key);
            }
            foreach (ushort id in applied)
            {
                pendingSnapshots.Remove(id);
            }
        }
        /// <summary>
        /// 根据服务器快照寻找本地实体，重建运动组。
        /// 任一实体还没有加载时返回 null，避免只移动半组目标。
        /// </summary>
        private static MotionGroup? RestoreGroup(MotionSnapshot snapshot)
        {
            if (Entity.FindEntityByID(snapshot.ControllerId) is not Item { Removed: false } controller) return null;
            MotionGroup group = new()
            {
                Controller = controller,
                CoordinateSubmarine = controller.Submarine,
                Offset = new Vector2(snapshot.OffsetX, snapshot.OffsetY),
                Speed = snapshot.Speed,
                Progress = snapshot.Progress,
                Goal = snapshot.Goal
            };
            foreach (TargetSnapshot target in snapshot.Targets)
            {
                if (Entity.FindEntityByID(target.Id) is not MapEntity { Removed: false } entity) return null;
                if ((entity is not Item && entity is not Structure) || !ReferenceEquals(entity.Submarine, controller.Submarine) || entity is Item { ParentInventory: not null }) return null;
                group.Targets.Add(new MotionTarget
                {
                    Entity = entity,
                    Home = new Vector2(target.HomeX, target.HomeY)
                });
            }
            return group;
        }
        
    }
}

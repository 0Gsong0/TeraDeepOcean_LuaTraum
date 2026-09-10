using Barotrauma.Items.Components;
using FarseerPhysics;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public static class TLRtsSystem
    {
        private const float SingleUnitSpacing = 130.0f;
        private const float HullHorizontalMargin = 45.0f;
        private const float HullVerticalMargin = 40.0f;
        /// <summary>
        /// RTS 移动中的机会射击距离。
        /// 这是 AI 决策距离，不代表子弹真实射程。
        /// </summary>
        private const float OpportunisticFireRange = 1000.0f;
        private static readonly Dictionary<ushort, TLRtsUnitState> states = new();
        public static IReadOnlyDictionary<ushort, TLRtsUnitState> States => states;
        /// <summary>
        /// 记录本系统创建的人类强制目标。
        /// </summary>
        private static readonly Dictionary<ushort, AIObjective> humanObjectives = new();
        private static bool initialized;
        private static float contextRefreshTimer;
        /// <summary>
        /// 暂时单人模式
        /// </summary>
        public static bool IsAuthority => !GameMain.IsMultiplayer;
        public static event Action? RoundStateCleared;
        /// <summary>
        /// 成功下达移动命令后触发。
        /// 第一个参数是鼠标点击的世界坐标；
        /// 第二个参数是经过 Hull 限制和地面修正后的最终槽位。
        /// 阶段 1 只用于单人客户端表现。
        /// </summary>
        public static event Action<Vector2, IReadOnlyList<Vector2>>? MoveOrderIssued;

        public static void Init(Harmony harmony)
        {
            if (initialized) return;
            initialized = true;
            TLRtsUnitRegistryPermission.UnitUnregistered += OnUnitUnregistered;
            var enemyUpdate = AccessTools.Method(typeof(EnemyAIController), nameof(EnemyAIController.Update));
            var characterRemove = AccessTools.Method(typeof(Character), nameof(Character.Remove));
            var addCombatObjective = AccessTools.Method(typeof(HumanAIController), nameof(HumanAIController.AddCombatObjective));
            var startRound = AccessTools.Method(typeof(GameSession), nameof(GameSession.StartRound), new[]
            {
                typeof(LevelData),
                typeof(bool),
                typeof(SubmarineInfo),
                typeof(SubmarineInfo),
            });
            var endRound = AccessTools.Method(typeof(GameSession), nameof(GameSession.EndRound));
            if (enemyUpdate != null && characterRemove != null && startRound != null && endRound != null && addCombatObjective != null)
            {
                harmony.Patch(enemyUpdate, prefix: new HarmonyMethod(typeof(TLRtsSystem), nameof(BeforeEnemyAIUpdate)), postfix: new HarmonyMethod(typeof(TLRtsSystem), nameof(AfterEnemyAIUpdate)));
                harmony.Patch(characterRemove, postfix: new HarmonyMethod(typeof(TLRtsSystem), nameof(OnCharacterRemoved)));
                harmony.Patch(addCombatObjective, prefix: new HarmonyMethod(typeof(TLRtsSystem), nameof(BeforeHumanAddCombatObjective)));
                harmony.Patch(startRound, postfix: new HarmonyMethod(typeof(TLRtsSystem), nameof(OnRoundStarted)));
                harmony.Patch(endRound, prefix: new HarmonyMethod(typeof(TLRtsSystem), nameof(OnRoundEnding)));
            }
        }
        private static bool BeforeEnemyAIUpdate(EnemyAIController __instance, float deltaTime)
        {
            if (!IsAuthority || !TryGetActiveState(__instance.Character, out TLRtsUnitState? state)) return true;
            if (state.Mode == TLRTSUnitAIState.Attack)
            {
                Character? target = state.GetAttackTarget();
                if (IsValidAttackTarget(target) && target != null && IsHostileTarget(__instance.Character, target))
                {
                    // 每帧重新锁定，避免原版怪物 AI 改选其他目标。
                    __instance.SelectTarget(target.AiTarget, 100f);
                    // true：Attack 状态下允许原版 EnemyAIController 执行攻击。
                    return true;
                }
                FinishAttack(
                    __instance.Character.ID,
                    state.AttackTargetId);
                return false;
            }

            if (state.Mode == TLRTSUnitAIState.Guard)
            {
                StopMovement(__instance.Character, __instance);
                return false;
            }
            //EnemyAIController 初始可能尚未换成室内寻路器。允许原版执行，直到它初始化 IndoorsSteeringManager。
            if (__instance.SteeringManager is not IndoorsSteeringManager) return true;
            // 已准备好室内寻路后，禁止原版 AI 抢夺移动控制权。
            return false;
        }
        private static void AfterEnemyAIUpdate(EnemyAIController __instance, float deltaTime)
        {
            if (!IsAuthority || !TryGetActiveState(__instance.Character, out TLRtsUnitState? state)) return;
            // Attack 状态已经让原版 EnemyAIController 完整执行。
            // 这里不能再用 RTS 移动逻辑覆盖它。
            if (state.Mode == TLRTSUnitAIState.Attack)
            {
                return;
            }
            Character character = __instance.Character;
            if (state.Mode == TLRTSUnitAIState.Guard)
            {
                StopMovement(character, __instance);
                return;
            }
            if (__instance.SteeringManager is not IndoorsSteeringManager indoors)
            {
                return;
            }
            Vector2 targetWorldPosition = state.GetTargetWorldPosition();
            if (Vector2.DistanceSquared(
                    character.WorldPosition,
                    targetWorldPosition) <=
                state.ArrivalDistance * state.ArrivalDistance)
            {
                state.Mode = TLRTSUnitAIState.Guard;
                StopMovement(character, __instance);
                return;
            }
            Submarine? targetSubmarine = state.GetTargetSubmarine();
            Vector2 targetRelativeSimPosition =
                Submarine.GetRelativeSimPosition(
                    ConvertUnits.ToSimUnits(state.TargetLocalPosition),
                    character.Submarine,
                    targetSubmarine);
            character.ClearInputs();
            indoors.Reset();
            indoors.SteeringSeek(
                targetRelativeSimPosition,
                weight: 1.0f,
                nodeFilter: node =>
                    node.Waypoint.Submarine != null &&
                    TLRtsRoundSubContext.Contains(
                        node.Waypoint.Submarine));
            indoors.Update(character.AnimController.GetCurrentSpeed(true));
        }
        private static void OnRoundStarted()
        {
            ClearRoundState();
            if (IsAuthority)
            {
                TLRtsRoundSubContext.Refresh();
            }
        }
        private static void OnRoundEnding()
        {
            ClearRoundState();
        }
        private static void OnCharacterRemoved(Character __instance)
        {
            if (__instance == null) { return; }
            TLRtsUnitRegistryPermission.Unregister(__instance.ID);
            RemoveState(__instance.ID);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="__instance"></param>
        /// <returns></returns>
        private static bool BeforeHumanAddCombatObjective(HumanAIController __instance, AIObjectiveCombat.CombatMode mode, Character target, ref Func<AIObjective, bool>? abortCondition)
        {
            if (!IsAuthority) return true;
            Character character = __instance.Character;
            if (!TryGetActiveState(character, out TLRtsUnitState? state) || state == null) return true;
            if (state.Mode != TLRTSUnitAIState.Move) return true;
            // Guard：完全保留原版自动战斗。
            // Attack：完全保留下达的强制攻击。
            // Move 状态不能触发撤退、逮捕等可能改变路线的战斗模式。
            if (mode is not (
                AIObjectiveCombat.CombatMode.Defensive or
                AIObjectiveCombat.CombatMode.Offensive))
            {
                return false;
            }
            // 没有已经装填好的远程武器，就一路继续移动。
            // 不允许 AI 临时跑去寻找武器或弹药。
            if (!HasReadyRangedWeapon(character)) return false;
            ushort characterId = character.ID;
            ushort targetId = target.ID;
            Func<AIObjective, bool>? originalAbortCondition = abortCondition;
            /*
            * 保留原调用者原有的终止条件，再添加 RTS 机会射击条件。
            *
            * 注意：不修改 AIObjectiveGoTo。
            * AIObjectiveCombat 在检测到 CurrentOrder 是 GoTo 时，
            * 会继续 ForceAct 这个移动目标，因此寻路目标不会变成敌人。
            */
            abortCondition = objective =>
            {
                if (originalAbortCondition?.Invoke(objective) == true) return true;
                if (!states.TryGetValue(characterId, out TLRtsUnitState? currentState) || currentState.Mode != TLRTSUnitAIState.Move) return true;
                if (target.Removed || target.IsDead || target.IsIncapacitated || target.ID != targetId) return true;
                // 枪械打空、丢失或损坏后停止射击，不去寻找弹药。
                if (!HasReadyRangedWeapon(character)) return true;
                float distanceSquared = Vector2.DistanceSquared(character.WorldPosition, target.WorldPosition);
                if (distanceSquared > OpportunisticFireRange * OpportunisticFireRange) return true;
                // 看不见就不继续瞄准，也不会脱离路线追过去。
                if (!character.CanSeeTarget(target))
                {
                    return true;
                }
                return false;
            };
            // 允许原版创建 AIObjectiveCombat。
            return true;
        }
        /// <summary>
        /// 注册本回合已经存在的玩家船员 AI。
        /// 只扫描 CrewManager 中的 Bot，因此不会把普通 NPC 自动注册成 RTS 单位。
        /// 已经注册过的单位不会被重复覆盖
        /// 这个方法由客户端在首次开启 RTS 时调用。
        /// </summary>
        /// <returns></returns>
        public static int RegisterCurrentCrewBots()
        {
            if (!IsAuthority || !TLRtsRoundSubContext.IsValid) return 0;
            int count = 0;
            foreach (Character character in GameSession.GetSessionCrewCharacters(CharacterType.Bot))
            {
                if (character == null || character.IsDead || character.Removed || character.Submarine == null || !TLRtsRoundSubContext.Contains(character.Submarine))
                {
                    continue;
                }
                if (TLRtsUnitRegistryPermission.IsRegistered(character)) continue;
                if (TLRtsUnitRegistryPermission.Register(character, unitClass: "crew".ToIdentifier(), false))
                {
                    count++;
                }
            }
            return count;
        }
        public static void Update(float deltaTime)
        {
            if (!IsAuthority) { return; }
            contextRefreshTimer -= deltaTime;
            if (contextRefreshTimer <= 0)
            {
                contextRefreshTimer = 10.0f;
                TLRtsRoundSubContext.Refresh();
            }
            TLRtsUnitRegistryPermission.RemoveInvalidEntries();
            foreach (TLRtsUnitState state in states.Values.ToArray())
            {
                if (Entity.FindEntityByID(state.CharacterId) is not Character character ||
                    character.IsDead || character.Removed || !TLRtsUnitRegistryPermission.IsControllable(character))
                {
                    RemoveState(state.CharacterId);
                    continue;
                }
                if (!TryGetValidTarget(state, out _, out _))
                {
                    RemoveState(state.CharacterId);
                    continue;
                }
                Vector2 targetWorldPosition = state.GetTargetWorldPosition();
                float distanceSquared = Vector2.DistanceSquared(character.WorldPosition, targetWorldPosition);

                //怪物到达最终槽位
                if (state.Mode == TLRTSUnitAIState.Move && distanceSquared < state.ArrivalDistance * state.ArrivalDistance)
                {
                    state.Mode = TLRTSUnitAIState.Guard;
                }
                //怪物被推离 Guard 半径后重新接管移动。
                if (state.Mode == TLRTSUnitAIState.Guard && character.AIController is EnemyAIController && distanceSquared > state.GuardRadius * state.GuardRadius)
                {
                    state.Mode = TLRTSUnitAIState.Move;
                }
            }
        }
        private static void OnUnitUnregistered(ushort characterId)
        {
            RemoveState(characterId);
        }
        /// <summary>
        /// 给一组单位下达移动命令。
        /// 单个单位：尽可能抵达鼠标位置。
        /// 多个单位：围绕鼠标位置水平分散。
        /// </summary>
        /// <returns></returns>
        public static bool IssueMove(IEnumerable<Character> selectedCharacters, Vector2 clickedWorldPosition)
        {
            if (!IsAuthority || !TLRtsRoundSubContext.IsValid) return false;
            Hull? targetHull = Hull.FindHull(clickedWorldPosition);
            if (targetHull?.Submarine == null || !TLRtsRoundSubContext.Contains(targetHull.Submarine)) return false;

            List<Character> units = selectedCharacters.Where(IsCommandable).Distinct().OrderBy(c => c.WorldPosition.X).ToList();
            if (units.Count == 0) return false;

            List<Vector2> slots = CreateFormationSlots(clickedWorldPosition, targetHull, units.Count);

            List<Vector2> resolvedSlots = new(units.Count);

            // 单位和槽位都按 X 排序，可以减少单位互相交叉。
            slots.Sort((a, b) => a.X.CompareTo(b.X));
            for (int i = 0; i < units.Count; i++)
            {
                Character character = units[i];
                Vector2 finalWorldPosition = slots[i];
                WayPoint? routeAnchor = FindNearestRouteAnchor(finalWorldPosition, targetHull);
                //干燥房间中的人类和陆行怪物不能停在半空。
                //游泳环境则保留鼠标/编队槽位的完整二维坐标。
                bool useSwimmingPosition = targetHull.WaterPercentage >= 80f;
                if (!useSwimmingPosition && routeAnchor != null)
                {
                    finalWorldPosition.Y = routeAnchor.WorldPosition.Y;
                }
                finalWorldPosition = ClampInsideHull(finalWorldPosition, targetHull);
                // 保存实际下发给单位的最终位置，供客户端绘制槽位。
                resolvedSlots.Add(finalWorldPosition);
                AssignMoveState(character, targetHull, routeAnchor, finalWorldPosition);
            }
            MoveOrderIssued?.Invoke(
                clickedWorldPosition,
                resolvedSlots.ToArray());
            return true;
        }
        public static void AssignMoveState(Character character, Hull targetHull, WayPoint? routeAnchor, Vector2 finalWorldPosition)
        {
            Submarine targetSub = targetHull.Submarine;
            float arrivalDis = character.AIController is HumanAIController ? 50 : 35;
            TLRtsUnitState state = new()
            {
                CharacterId = character.ID,
                Mode = TLRTSUnitAIState.Move,
                TargetSubId = targetSub.ID,
                TargetHullId = targetHull.ID,
                // 只是寻路参考，不是最终目标。
                TargetWaypointId = routeAnchor?.ID ?? Entity.NullEntityID,
                //真正最终目标，使用目标潜艇局部显示坐标。
                TargetLocalPosition = finalWorldPosition - targetSub.Position,
                ArrivalDistance = arrivalDis,
                GuardRadius = Math.Max(arrivalDis + 40f, 80)
            };
            states[character.ID] = state;
            if (character.AIController is HumanAIController humanAI)
            {
                AssignHumanMoveObjective(
                    character,
                    humanAI,
                    state,
                    targetHull,
                    finalWorldPosition);
            }
        }
        private static void AssignHumanMoveObjective(Character character, HumanAIController humanAI, TLRtsUnitState state, Hull targetHull, Vector2 finalWorldPosition)
        {
            /*
             * 清除下达移动命令前的旧战斗目标。
             * 之后 HumanAIController 如果在路上发现敌人，
             * 会重新建立受约束的“机会射击”目标。
             */
            ClearHumanCombatObjectives(humanAI);
            ClearHumanObjective(character.ID);

            OrderTarget orderTarget =
                new(finalWorldPosition, targetHull);

            Order waitOrder = OrderPrefab.Prefabs["wait"]
                .CreateInstance(OrderPrefab.OrderTargetType.Position)
                .WithTargetPosition(orderTarget);

            AIObjective objective =
                humanAI.SetForcedOrder(waitOrder);

            if (objective is AIObjectiveGoTo goTo)
            {
                goTo.AllowGoingOutside = true;
                goTo.IsWaitOrder = true;
                goTo.SpeakIfFails = false;
                goTo.DebugLogWhenFails = false;
                goTo.CloseEnough = state.ArrivalDistance;
            }

            humanObjectives[character.ID] = objective;
        }
        /// <summary>
        /// 清除该人类当前的战斗目标。
        /// 用于从 Attack/自动战斗切换到严格 Move。
        /// </summary>
        private static void ClearHumanCombatObjectives(HumanAIController humanAI)
        {
            AIObjectiveCombat[] combatObjectives =
                humanAI.ObjectiveManager.Objectives
                    .OfType<AIObjectiveCombat>()
                    .ToArray();

            foreach (AIObjectiveCombat objective in combatObjectives)
            {
                // 先 Abandon，让原版目标有机会清理武器和子目标状态。
                objective.Abandon = true;
                humanAI.ObjectiveManager.Objectives.Remove(objective);
            }
        }
        /// <summary>
        /// 判断目标是否可以被指定单位攻击。
        /// Human 使用 HumanAIController 的友军判定，
        /// 非人类使用 Character 的阵营/物种友军判定。
        /// </summary>
        /// <param name="attacker"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        private static bool IsHostileTarget(Character attacker, Character target)
        {
            if (attacker == target ||
                target.Removed ||
                target.IsDead ||
                target.IsIncapacitated ||
                !target.Enabled)
            {
                return false;
            }
            bool friendly = attacker.AIController is HumanAIController ? HumanAIController.IsFriendly(attacker, target) : attacker.IsFriendly(target);
            return !friendly;
        }
        private static bool IsValidAttackTarget(Character? target)
        {
            return target != null &&
                   !target.Removed &&
                   !target.IsDead &&
                   !target.IsIncapacitated &&
                   target.Enabled &&
                   target.Submarine != null &&
                   TLRtsRoundSubContext.Contains(target.Submarine);
        }
        /// <summary>
        /// 如果单位尚未接收过移动命令，就以当前位置作为攻击结束后的 Guard 点。
        /// </summary>
        /// <param name="character"></param>
        /// <param name="state"></param>
        /// <returns></returns>
        private static bool TryEnsureGuardAnchor(Character character, out TLRtsUnitState? state)
        {
            if (states.TryGetValue(character.ID, out state) && TryGetValidTarget(state, out _, out _)) return true;
            Hull? currentHull = character.CurrentHull ?? Hull.FindHull(character.WorldPosition);
            if (currentHull?.Submarine == null || !TLRtsRoundSubContext.Contains(currentHull.Submarine))
            {
                state = null;
                return false;
            }
            Vector2 guardPosition =
                ClampInsideHull(character.WorldPosition, currentHull);

            WayPoint? routeAnchor =
                FindNearestRouteAnchor(guardPosition, currentHull);
            state = new TLRtsUnitState
            {
                CharacterId = character.ID,
                Mode = TLRTSUnitAIState.Guard,
                TargetSubId = currentHull.Submarine.ID,
                TargetHullId = currentHull.ID,
                TargetWaypointId =
                routeAnchor?.ID ?? Entity.NullEntityID,
                TargetLocalPosition =
                guardPosition - currentHull.Submarine.Position,
                ArrivalDistance =
                character.AIController is HumanAIController ? 50f : 35f,
                GuardRadius = 90f,
                AttackTargetId = Entity.NullEntityID
            };
            states[character.ID] = state;
            return true;
        }
        /// <summary>
        /// 让选中单位强制攻击指定角色。
        /// 目标失效后，单位返回之前保存的 Guard 点。
        /// </summary>
        public static bool IssueAttack(IEnumerable<Character> selectedCharacters, Character target)
        {
            if (!IsAuthority || !TLRtsRoundSubContext.IsValid || !IsValidAttackTarget(target)) return false;
            List<Character> units = selectedCharacters.Where(IsCommandable).Where(unit => IsHostileTarget(unit, target)).Distinct().ToList();
            if (units.Count == 0) return false;
            bool issued = false;
            foreach (Character character in units)
            {
                if (!TryEnsureGuardAnchor(character, out TLRtsUnitState? state) || state == null) continue;
                // 先改变状态，再清除旧战斗目标。
                // 这样旧目标的 Abandoned 回调不会错误地切回 Guard。
                state.Mode = TLRTSUnitAIState.Attack;
                state.AttackTargetId = target.ID;
                if (character.AIController is HumanAIController humanAI)
                {
                    ClearHumanObjective(character.ID);
                    ClearHumanCombatObjectives(humanAI);
                    ushort attackerId = character.ID;
                    ushort targetId = target.ID;
                    humanAI.AddCombatObjective(AIObjectiveCombat.CombatMode.Offensive, target, abortCondition: _ =>
                    {
                        if (!states.TryGetValue(
                            attackerId,
                            out TLRtsUnitState? current))
                        {
                            return true;
                        }
                        return current.Mode != TLRTSUnitAIState.Attack ||
                          current.AttackTargetId != targetId ||
                          !IsValidAttackTarget(target);
                    },
                    onAbort: () =>
                        FinishAttack(attackerId, targetId),
                    onCompleted: () =>
                        FinishAttack(attackerId, targetId));
                }
                else if (character.AIController is EnemyAIController enemyAI)
                {
                    // 后面的 Harmony Update 还会持续锁定目标。
                    enemyAI.SelectTarget(target.AiTarget, 100f);
                }
                issued = true;
            }
            return issued;
        }
        /// <summary>
        /// 攻击结束回到 Guard
        /// </summary>
        private static void FinishAttack(ushort attackerId, ushort targetId)
        {
            if (!states.TryGetValue(attackerId, out TLRtsUnitState? state)) return;
            if (state.Mode != TLRTSUnitAIState.Attack || state.AttackTargetId != targetId) return;
            state.Mode = TLRTSUnitAIState.Guard;
            state.AttackTargetId = Entity.NullEntityID;
            if (Entity.FindEntityByID(attackerId) is not Character character || character.AIController is not HumanAIController humanAI) return;
            if (!TryGetValidTarget(state, out _, out Hull? guardHull) || guardHull == null) return;
            AssignHumanMoveObjective(
               character,
               humanAI,
               state,
               guardHull,
               state.GetTargetWorldPosition());
        }

        public static Vector2 ClampInsideHull(Vector2 worldPosition, Hull hull)
        {
            Rectangle rect = hull.WorldRect;
            float left = rect.Left + HullHorizontalMargin;
            float right = rect.Right - HullHorizontalMargin;
            float bottom = rect.Y - rect.Height + HullVerticalMargin;
            float top = rect.Y - HullVerticalMargin;

            if (right < left)
            {
                left = right = (rect.Left + rect.Right) * 0.5f;
            }

            if (top < bottom)
            {
                top = bottom = rect.Y - rect.Height * 0.5f;
            }
            return new Vector2(
                MathHelper.Clamp(worldPosition.X, left, right),
                MathHelper.Clamp(worldPosition.Y, bottom, top));
        }
        /// <summary>
        /// 寻找最接近的 WayPoint。
        /// </summary>
        /// <param name="targetWorldPosition"></param>
        /// <param name="targetHull"></param>
        /// <returns></returns>
        private static WayPoint? FindNearestRouteAnchor(Vector2 targetWorldPosition, Hull targetHull)
        {
            return WayPoint.WayPointList.Where(wp =>
                wp != null && !wp.Removed &&
                wp.SpawnType == SpawnType.Path &&
                wp.CurrentHull == targetHull &&
                wp.Submarine == targetHull.Submarine).OrderBy(wp =>
                Vector2.DistanceSquared(wp.WorldPosition, targetWorldPosition)).FirstOrDefault();
        }
        /// <summary>
        /// 创建单位阵型槽位。
        /// </summary>
        /// <param name="centerWorldPosition"></param>
        /// <param name="targetHull"></param>
        /// <param name="unitCount"></param>
        /// <returns></returns>
        private static List<Vector2> CreateFormationSlots(Vector2 centerWorldPosition, Hull targetHull, int unitCount)
        {
            Rectangle hullRect = targetHull.WorldRect;
            float left = hullRect.Left + HullHorizontalMargin;
            float right = hullRect.Right - HullHorizontalMargin;
            float bottom =
                hullRect.Y - hullRect.Height + HullVerticalMargin;
            float top =
                hullRect.Y - HullVerticalMargin;

            if (right < left)
            {
                float center = (hullRect.Left + hullRect.Right) * 0.5f;
                left = center;
                right = center;
            }
            if (top < bottom)
            {
                float center = hullRect.Y - hullRect.Height * 0.5f;
                bottom = center;
                top = center;
            }

            float centerY = MathHelper.Clamp(centerWorldPosition.Y, bottom, top);
            if (unitCount == 1)
            {
                return new List<Vector2>
                {
                    new(
                        MathHelper.Clamp(centerWorldPosition.X, left, right),
                        centerY)
                };
            }
            float availableWidth = Math.Max(right - left, 1.0f);
            float spacing = Math.Min(SingleUnitSpacing, availableWidth / Math.Max(unitCount - 1, 1));
            float totalWidth = spacing * (unitCount - 1);
            float formationCenterX = MathHelper.Clamp(centerWorldPosition.X, left + totalWidth * 0.5f, right - totalWidth * 0.5f);
            List<Vector2> slots = new(unitCount);
            for (int i = 0; i < unitCount; i++)
            {
                float offsetX = (i - (unitCount - 1) * 0.5f) * spacing;
                slots.Add(new Vector2(formationCenterX + offsetX, centerY));
            }
            return slots;
        }
        /// <summary>
        /// 判断单位是否可命令。
        /// </summary>
        /// <param name="character"></param>
        /// <returns></returns>
        private static bool IsCommandable(Character character)
        {
            return character != null && !character.IsDead && !character.Removed && character.Enabled &&
                character.Submarine != null && TLRtsRoundSubContext.Contains(character.Submarine) &&
                TLRtsUnitRegistryPermission.IsControllable(character);
        }
        public static bool TryGetState(ushort characterId, out TLRtsUnitState? state)
        {
            return states.TryGetValue(characterId, out state);
        }
        /// <summary>
        /// 移除状态
        /// </summary>
        /// <param name="characterId"></param>
        private static void RemoveState(ushort characterId)
        {
            states.Remove(characterId);
            ClearHumanObjective(characterId);
        }
        private static void ClearHumanObjective(ushort characterId)
        {
            if (!humanObjectives.Remove(characterId, out AIObjective? objective)) return;
            if (Entity.FindEntityByID(characterId) is Character character && character.AIController is HumanAIController humanAi && humanAi.ObjectiveManager.ForcedOrder == objective)
            {
                humanAi.ClearForcedOrder();
            }
        }
        private static void StopMovement(Character character, AIController ai)
        {
            ai.SteeringManager.Reset();
            character.AnimController.TargetMovement = Vector2.Zero;
            character.SetInput(InputType.Left, false, false);
            character.SetInput(InputType.Right, false, false);
            character.SetInput(InputType.Up, false, false);
            character.SetInput(InputType.Down, false, false);
        }
        /// <summary>
        /// 尝试获取活动状态。
        /// </summary>
        /// <param name="character"></param>
        /// <param name="state"></param>
        /// <returns></returns>
        private static bool TryGetActiveState(Character? character, out TLRtsUnitState? state)
        {
            state = null;
            if (character == null || character.Removed || character.IsDead || character.Submarine == null ||
                 !TLRtsRoundSubContext.IsValid || !TLRtsRoundSubContext.Contains(character.Submarine) || !TLRtsUnitRegistryPermission.IsControllable(character)) return false;
            if (!states.TryGetValue(character.ID, out state))
            {
                return false;
            }
            return TryGetValidTarget(state, out _, out _);
        }
        /// <summary>
        /// 尝试获取有效的目标。尝试获取目标单位或目标船。
        /// </summary>
        /// <param name="state"></param>
        /// <param name="targetSub"></param>
        /// <param name="targetHull"></param>
        /// <returns></returns>
        private static bool TryGetValidTarget(TLRtsUnitState? state, out Submarine? targetSub, out Hull? targetHull)
        {
            targetSub = state?.GetTargetSubmarine();
            targetHull = state?.GetTargetHull();
            return state != null &&
               targetSub != null &&
               !targetSub.Removed &&
               targetHull != null &&
               !targetHull.Removed &&
               targetHull.Submarine == targetSub &&
               TLRtsRoundSubContext.Contains(targetSub);
        }
        public static void ClearRoundState()
        {
            foreach (ushort characterId in humanObjectives.Keys.ToArray())
            {
                ClearHumanObjective(characterId);
            }
            states.Clear();
            TLRtsUnitRegistryPermission.Clear();
            TLRtsRoundSubContext.Clear();

            contextRefreshTimer = 0.0f;
            RoundStateCleared?.Invoke();
        }
        private static bool HasReadyRangedWeapon(Character character)
        {
            if (character.Inventory == null || character.LockHands) return false;
            foreach (Item item in character.Inventory.AllItems)
            {
                if (item == null || item.Removed || item.Condition <= 0.0f) continue;
                RangedWeapon? rangedWeapon = item.GetComponent<RangedWeapon>();
                if (rangedWeapon == null) continue;
                if (rangedWeapon.CombatPriority <= 0.0f) continue;
                if (rangedWeapon.IsEmpty(character)) continue;
                return true;
            }
            return false;
        }
        /// <summary>
        /// 鼠标附近目标搜索
        /// </summary>
        /// <returns></returns>
        public static Character? FindAttackTargetAt(IEnumerable<Character> selectedCharacters, Vector2 worldPosition, float hitRadius = 65f)
        {
            List<Character> units = selectedCharacters.Where(IsCommandable).Distinct().ToList();
            if (units.Count == 0) return null;
            float radiusSquared = hitRadius * hitRadius;
            return Character.CharacterList
                    .Where(IsValidAttackTarget)
                    .Where(target => units.Any(unit => IsHostileTarget(unit, target)))
                    .Select(target => new
                    {
                        Target = target,
                        // 目前以角色中心点作为点击判定。
                        DistanceSquared = Vector2.DistanceSquared(
                            target.WorldPosition,
                            worldPosition)
                    })
                    .Where(entry =>
                        entry.DistanceSquared <= radiusSquared)
                    .OrderBy(entry =>
                        entry.DistanceSquared)
                    .Select(entry => entry.Target).FirstOrDefault();
        }
    }
}

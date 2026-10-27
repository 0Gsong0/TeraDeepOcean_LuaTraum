using Barotrauma.Items.Components;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public static class TLRtsSystem
    {
        /// <summary>
        /// 常规分散队形的单位中心间距。
        /// 保持当前版本原有的分散效果。
        /// </summary>
        private const float RegularFormationSpacing = 130.0f;

        /// <summary>
        /// 密集行进队形的单位中心间距。
        /// 
        /// 人类角色的身体宽度通常会大于这个数值，
        /// 所以角色模型可能少量重叠，这是密集行进队形的预期效果。
        /// </summary>
        private const float CompactFormationSpacing = 35.0f;
        /// <summary>
        /// 排枪重叠队形的单位中心间距。
        ///
        /// 设置为 0 表示每个单位都会获得完全相同的目标位置。
        /// </summary>
        private const float VolleyStackFormationSpacing = 0.0f;
        /// <summary>
        /// 排枪重叠队形的到达判定距离。
        ///
        /// 普通人类当前使用 50，怪物使用 35；这对于重叠队形过大，
        /// 会导致单位还没真正聚拢就进入 Guard。
        ///
        /// 不建议设置为 0，因为 AI 很难精确停在单个浮点坐标上，
        /// 可能在目标附近不断来回修正。
        /// </summary>
        private const float VolleyStackArrivalDistance = 6.0f;
        /// <summary>
        /// 排枪队形中，非人类单位被推离目标多远后重新尝试归位。
        /// </summary>
        private const float VolleyStackGuardRadius = 20.0f;
        private const float HullHorizontalMargin = 45.0f;
        private const float HullVerticalMargin = 40.0f;

        /// <summary>
        /// 强制方向射击持续时间。
        /// </summary>
        private const float DirectionalFireDuration = 3.0f;

        /// <summary>
        /// 拿出武器后正式开火前的瞄准时间。
        /// </summary>
        private const float DirectionalFireAimDelay = 0.25f;

        /// <summary>
        /// 每个单位的虚拟瞄准点距离。
        ///
        /// 方向射击使用：角色位置 + 方向 × 距离。
        /// 较大的距离可以让不同位置的单位保持近似平行射击。
        /// </summary>
        private const float DirectionalFireAimDistance = 2500.0f;

        /// <summary>
        /// 用于拒绝长度过小的射击方向。
        /// </summary>
        private const float MinimumDirectionalFireLengthSquared = 0.0001f;

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
        /// 单人模式由本机执行 RTS AI；
        /// 多人模式只能由服务器执行。
        /// </summary>
        public static bool IsAuthority => !GameMain.IsMultiplayer || GameMain.NetworkMember is { IsServer: true };
        public static event Action? RoundStateCleared;
        /// <summary>
        /// 成功下达移动命令后触发。
        /// 第一个参数是鼠标点击的世界坐标；
        /// 第二个参数是经过 Hull 限制和地面修正后的最终槽位。
        /// 阶段 1 只用于单人客户端表现。
        /// </summary>
        public static event Action<Vector2, IReadOnlyList<Vector2>>? MoveOrderIssued;
        // 每只受 RTS 控制的怪物使用自己的攻击参数；不要修改全局 XML 预制体。
        private static readonly Dictionary<ushort, CharacterParams.TargetParams> forcedCreatureAttackParams = new();

        public static void Init(Harmony harmony)
        {
            if (initialized) return;
            initialized = true;
            TLRtsUnitRegistryPermission.UnitUnregistered += OnUnitUnregistered;
            var enemyUpdate = AccessTools.Method(typeof(EnemyAIController), nameof(EnemyAIController.Update));
            var humanUpdate = AccessTools.Method(typeof(HumanAIController), nameof(HumanAIController.Update), new[] { typeof(float) });
            var characterRemove = AccessTools.Method(typeof(Character), nameof(Character.Remove));
            var addCombatObjective = AccessTools.Method(typeof(HumanAIController), nameof(HumanAIController.AddCombatObjective));
            var startRound = AccessTools.Method(typeof(GameSession), nameof(GameSession.StartRound), new[]
            {
                typeof(LevelData),
                typeof(bool),
            });
            var endRound = AccessTools.Method(typeof(GameSession), nameof(GameSession.EndRound));
            var enemyUpdateTargets = AccessTools.Method(typeof(EnemyAIController),nameof(EnemyAIController.UpdateTargets));
            if (enemyUpdate == null || characterRemove == null || startRound == null || endRound == null ||
                addCombatObjective == null || humanUpdate == null || enemyUpdateTargets == null)
                throw new MissingMethodException("RTS 所需的游戏方法不存在，请检查游戏与引用程序集版本。");
            harmony.Patch(enemyUpdate, prefix: new HarmonyMethod(typeof(TLRtsSystem), nameof(BeforeEnemyAIUpdate)), postfix: new HarmonyMethod(typeof(TLRtsSystem), nameof(AfterEnemyAIUpdate)));
            harmony.Patch(characterRemove, postfix: new HarmonyMethod(typeof(TLRtsSystem), nameof(OnCharacterRemoved)));
            harmony.Patch(addCombatObjective, prefix: new HarmonyMethod(typeof(TLRtsSystem), nameof(BeforeHumanAddCombatObjective)));
            harmony.Patch(startRound, postfix: new HarmonyMethod(typeof(TLRtsSystem), nameof(OnRoundStarted)));
            harmony.Patch(endRound, prefix: new HarmonyMethod(typeof(TLRtsSystem), nameof(OnRoundEnding)));
            harmony.Patch(humanUpdate, prefix: new HarmonyMethod(typeof(TLRtsSystem), nameof(BeforeHumanAIUpdate)), postfix: new HarmonyMethod(typeof(TLRtsSystem), nameof(AfterHumanAIUpdate)));
            harmony.Patch(enemyUpdateTargets,prefix: new HarmonyMethod(typeof(TLRtsSystem),nameof(BeforeEnemyUpdateTargets)));
        }

        /// <summary>
        /// 取得这只怪物执行 RTS 强制攻击时使用的目标参数。
        /// 优先借用它已有的攻击配置，以保留其攻击移动模式；
        /// 完全没有攻击目标配置时，创建仅供这只单位使用的临时参数。
        /// </summary>
        private static CharacterParams.TargetParams GetForcedCreatureAttackParams(
            EnemyAIController enemyAI)
        {
            ushort id = enemyAI.Character.ID;

            if (forcedCreatureAttackParams.TryGetValue(
                    id, out CharacterParams.TargetParams? cached))
            {
                return cached;
            }

            CharacterParams.TargetParams parameters =
                enemyAI.AIParams.Targets
                    .Where(p => p.State == AIState.Attack && p.Priority > 0f)
                    .OrderByDescending(p => p.Priority)
                    .FirstOrDefault()
                ?? new CharacterParams.TargetParams(
                    "rts_forced_attack",
                    AIState.Attack,
                    1000f,
                    enemyAI.Character.Params);

            forcedCreatureAttackParams[id] = parameters;
            return parameters;
        }

        /// <summary>
        /// 同时设置具体角色、攻击参数和攻击状态。
        /// 仅 SelectTarget 不够：原版 UpdateAttack 在参数为空时会直接退出。
        /// </summary>
        private static bool ApplyForcedCreatureAttack(
            EnemyAIController enemyAI,
            Character target,
            ref CharacterParams.TargetParams currentTargetingParams)
        {
            if (target.AiTarget == null) return false;

            currentTargetingParams = GetForcedCreatureAttackParams(enemyAI);
            enemyAI.SelectTarget(target.AiTarget, 1000f);
            enemyAI.State = AIState.Attack;
            return true;
        }

        /// <summary>
        /// RTS 强制攻击期间，不让原版 UpdateTargets 按 XML 重新选择目标。
        /// 其他状态仍完整使用原版目标系统。
        /// Harmony 的 ___currentTargetingParams 是对原类同名私有字段的注入。
        /// </summary>
        private static bool BeforeEnemyUpdateTargets(
            EnemyAIController __instance,
            ref CharacterParams.TargetParams ___currentTargetingParams)
        {
            if (!IsAuthority ||
                !TryGetActiveState(
                    __instance.Character, out TLRtsUnitState? state) ||
                state == null ||
                state.Mode != TLRTSUnitAIState.Attack)
            {
                return true;
            }

            Character? target = state.GetAttackTarget();
            if (!IsValidAttackTarget(target) ||
                target == null ||
                !IsHostileTarget(__instance.Character, target) ||
                !ApplyForcedCreatureAttack(
                    __instance, target, ref ___currentTargetingParams))
            {
                FinishAttack(__instance.Character.ID, state.AttackTargetId);
                return false;
            }

            return false; // 跳过这次原版 XML 目标重算。
        }
        private static bool BeforeEnemyAIUpdate(EnemyAIController __instance, float deltaTime, ref CharacterParams.TargetParams ___currentTargetingParams)
        {
            if (!IsAuthority ||
        !TryGetActiveState(
            __instance.Character, out TLRtsUnitState? state))
            {
                return true;
            }

            if (state.Mode == TLRTSUnitAIState.Attack)
            {
                Character? target = state.GetAttackTarget();

                if (IsValidAttackTarget(target) &&
                    target != null &&
                    IsHostileTarget(__instance.Character, target) &&
                    ApplyForcedCreatureAttack(
                        __instance, target, ref ___currentTargetingParams))
                {
                    // 保留原版 UpdateAttack：它负责寻路、选择攻击肢体和真正造成伤害。
                    return true;
                }

                FinishAttack(__instance.Character.ID, state.AttackTargetId);
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
        /// <summary>
        /// 方向射击期间禁止原版 HumanAIController.Update 执行。
        ///
        /// 否则原版 AI 可能重新选择目标、收起武器、移动或覆盖瞄准输入。
        /// </summary>
        private static bool BeforeHumanAIUpdate(HumanAIController __instance, float deltaTime)
        {
            if (!IsAuthority || !TryGetActiveState(__instance.Character, out TLRtsUnitState? state) || state == null) return true;
            if (state.Mode != TLRTSUnitAIState.DirectionalFire) return true;
            /*
             * 方向射击要求单位原地开火。
             * 原版 HumanAIController 本帧不再执行。
             */
            StopMovement(__instance.Character, __instance);
            return false;
        }
        /// <summary>
        /// 在 HumanAIController.Update 之后执行方向射击。
        ///
        /// 即使 Prefix 阻止了原方法，Harmony Postfix 仍然会执行。
        /// </summary>
        private static void AfterHumanAIUpdate(HumanAIController __instance, float deltaTime)
        {
            if (!IsAuthority || !TryGetActiveState(__instance.Character, out TLRtsUnitState? state) || state == null) return;
            switch (state.Mode)
            {
                case TLRTSUnitAIState.DirectionalFire:
                    UpdateHumanDirectionalFire(__instance.Character, __instance, state, deltaTime);
                    break;

                case TLRTSUnitAIState.Move:
                    EnforceHumanRtsMovement(__instance.Character, __instance, deltaTime);
                    break;

                case TLRTSUnitAIState.Guard:
                    // 允许原版 AI 判断敌人、瞄准和开火；
                    // 但不能因为战斗目标自行跑离防守位置。
                    StopMovement(__instance.Character, __instance);
                    break;
            }
        }
        /// <summary>
        /// 当战斗等原版目标压过移动命令时，在本帧末重新执行 RTS 的 GoTo。
        /// 不删除战斗目标，因此已经具备武器的单位仍可机会射击。
        /// </summary>
        private static void EnforceHumanRtsMovement(Character character, HumanAIController humanAI, float deltaTime)
        {
            if (!humanObjectives.TryGetValue(character.ID, out AIObjective? objective) || objective is not AIObjectiveGoTo goTo || humanAI.ObjectiveManager.ForcedOrder != goTo)
            {
                return;
            }

            // 本帧原版已经执行了移动目标，不需要重复执行。
            if (humanAI.ObjectiveManager.CurrentObjective == goTo)
            {
                return;
            }

            // 清除战斗目标本帧留下的转向，再让 RTS 路线产生最终转向。
            // Reset 只清除当前 steering 向量，不会删除 GoTo 的寻路路径。
            if (humanAI.SteeringManager == null) return;
            humanAI.SteeringManager.Reset();
            goTo.ForceAct(deltaTime);
            humanAI.SteeringManager.Update(character.AnimController.GetCurrentSpeed(true));
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
        /// 释放所有当前受到 RTS 命令控制的单位，让原版 AI 重新接管。
        ///
        /// 此操作只清除 Move、Guard 和 Attack 状态，
        /// 不清除 RTS 单位注册表，因此之后仍然可以重新选择这些单位下令。
        /// </summary>
        /// <returns>实际清除 RTS 状态的单位数量。</returns>
        public static int ReleaseAllUnitsToVanillaAi()
        {
            if (!IsAuthority) return 0;
            ushort[] affectedCharacterIds = states.Keys.ToArray();
            states.Clear();
            foreach (ushort characterId in affectedCharacterIds)
            {
                ClearHumanObjective(characterId);
                if (Entity.FindEntityByID(characterId) is not Character character || character.Removed)
                {
                    continue;
                }
                switch (character.AIController)
                {
                    case HumanAIController humanAI:
                        /*
                         * 清除 RTS 强制攻击留下的战斗目标。
                         * RTS 状态已经先被删除，所以回调不会重新建立 Guard。
                         */
                        ClearHumanCombatObjectives(humanAI);
                        humanAI.SteeringManager?.Reset();
                        break;
                    case EnemyAIController enemyAI:
                        /*
                         * 清除 RTS 强制指定的 SelectedAiTarget。
                         * 下一次原版 EnemyAIController.Update 会自行重新评估目标。
                         */
                        enemyAI.Reset();
                        enemyAI.SteeringManager?.Reset();
                        break;
                    case AIController ai:
                        ai.SteeringManager?.Reset();
                        break;
                }
                // 清除最后一帧可能残留的移动输入。
                character.ClearInputs();
                character.AnimController.TargetMovement = Vector2.Zero;
            }
            return affectedCharacterIds.Length;
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
        /// <summary>
        /// 注册当前队伍生物
        /// </summary>
        /// <param name="commanderTeam"></param>
        /// <returns></returns>
        public static int RegisterCurrentTeamCreatures(CharacterTeamType commanderTeam)
        {
            if (!IsAuthority || !TLRtsRoundSubContext.IsValid || commanderTeam == CharacterTeamType.None) return 0;
            int count = 0;
            foreach (Character character in Character.CharacterList)
            {
                if (character == null || character.Removed || character.IsDead || character.IsIncapacitated || !character.Enabled) continue;
                if (character.TeamID != commanderTeam) continue;
                if (character.AIController is not EnemyAIController { Enabled: true }) continue;
                if (character.Submarine == null || !TLRtsRoundSubContext.Contains(character.Submarine)) continue;
                if (TLRtsUnitRegistryPermission.IsRegistered(character)) continue;
                if (TLRtsUnitRegistryPermission.Register(character, unitClass: "creature".ToIdentifier(), isSpawnByRts: false)) count++;
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
                if (Entity.FindEntityByID(state.CharacterId) is not Character character || character.IsDead || character.Removed || !TLRtsUnitRegistryPermission.IsControllable(character))
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
                // Guard 单位被推离防守点时，重新移动回去。
                if (state.Mode == TLRTSUnitAIState.Guard && distanceSquared > state.GuardRadius * state.GuardRadius)
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
        /// 更新一个人类 AI 的方向射击状态。
        /// </summary>
        private static void UpdateHumanDirectionalFire(Character character, HumanAIController humanAI, TLRtsUnitState state, float deltaTime)
        {
            if (character.IsIncapacitated || !character.Enabled || !character.CanAim)
            {
                FinishHumanDirectionalFire(character, humanAI, state);
                return;
            }
            Item? weapon = state.GetDirectionalFireWeapon();
            RangedWeapon? rangedWeapon = weapon?.GetComponent<RangedWeapon>();
            if (weapon == null || weapon.Removed || weapon.Condition <= 0.0f || rangedWeapon == null || rangedWeapon.IsEmpty(character) || !character.HasEquippedItem(weapon, predicate: CharacterInventory.IsHandSlotType))
            {
                FinishHumanDirectionalFire(character, humanAI, state);
                return;
            }
            Vector2 direction = state.DirectionalFireDirection;
            if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || direction.LengthSquared() < MinimumDirectionalFireLengthSquared)
            {
                FinishHumanDirectionalFire(character, humanAI, state);
                return;
            }
            direction.Normalize();
            /*
            * 每帧停止寻路和移动输入，
            * 但允许瞄准动画自行转动角色身体和手臂。
            */
            StopMovement(character, humanAI);
            character.ClearInput(InputType.Aim);
            character.ClearInput(InputType.Shoot);
            Vector2 aimWorldPosition = character.WorldPosition + direction * DirectionalFireAimDistance;
            /*
            * Character.CursorPosition 在潜艇内部使用潜艇局部坐标。
            */
            character.CursorPosition = character.Submarine == null ? aimWorldPosition : aimWorldPosition - character.Submarine.Position;
            character.SetInput(InputType.Aim, hit: false, held: true);
            /*
            * 先等待短暂瞄准时间。
            * 正式射击时间不会在这个阶段减少。
            */
            if (state.DirectionalFireAimDelayRemaining > 0.0f)
            {
                state.DirectionalFireAimDelayRemaining -= deltaTime;
                return;
            }
            if (state.DirectionalFireRemaining <= 0.0f)
            {
                FinishHumanDirectionalFire(character, humanAI, state);
                return;
            }
            /*
             * 持续保持 Shoot，并直接调用武器 Use。
             *
             * 武器自己的 ReloadTimer、射速、点射和弹药逻辑
             * 仍由原版 RangedWeapon 处理。
             */
            character.SetInput(InputType.Shoot, hit: false, held: true);
            weapon.Use(deltaTime, user: character);
            state.DirectionalFireRemaining -= deltaTime;
            if (state.DirectionalFireRemaining <= 0.0f)
            {
                FinishHumanDirectionalFire(character, humanAI, state);
            }
        }
        /// <summary>
        /// 结束方向射击并让单位在开火位置进入 Guard。
        /// </summary>
        private static void FinishHumanDirectionalFire(Character character, HumanAIController humanAI, TLRtsUnitState state)
        {
            character.ClearInput(InputType.Aim);
            character.ClearInput(InputType.Shoot);

            StopMovement(character, humanAI);

            state.DirectionalFireRemaining = 0.0f;
            state.DirectionalFireAimDelayRemaining = 0.0f;
            state.DirectionalFireWeaponItemId = Entity.NullEntityID;
            state.Mode = TLRTSUnitAIState.Guard;
            /*
            * 重新建立原地等待目标。
            * 否则 DirectionalFire 结束后，原版 Human AI
            * 可能恢复自己的普通工作和行动。
            */
            if (!TryGetValidTarget(state, out _, out Hull? guardHull) || guardHull == null)
            {
                RemoveState(character.ID);
                return;
            }
            AssignHumanMoveObjective(character, humanAI, state, guardHull, state.GetTargetWorldPosition());
        }
        /// <summary>
        /// 清除方向射击可能留下的瞄准和开火输入。
        /// </summary>
        private static void ClearDirectionalFireInputs(Character character)
        {
            character.ClearInput(InputType.Aim);
            character.ClearInput(InputType.Shoot);
        }
        /// <summary>
        /// 让选中的人类 AI 原地朝统一方向持续射击三秒。
        ///
        /// 非人类单位会被忽略，因为怪物身体攻击依赖真实攻击目标，
        /// 不能安全地复用人类手持武器逻辑。
        /// </summary>
        /// <returns>实际开始执行方向射击的单位数量。</returns>
        public static int IssueDirectionalFire(IEnumerable<Character> selectedCharacters, Vector2 direction)
        {
            if (!IsAuthority || !TLRtsRoundSubContext.IsValid || !float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || direction.LengthSquared() < MinimumDirectionalFireLengthSquared) return 0;
            /*
             * 服务端和单人权威端都重新归一化方向，
             * 不能直接信任客户端提交的向量长度。
             */
            direction.Normalize();
            List<Character> units = selectedCharacters.Where(IsCommandable).Distinct().ToList();
            int issuedCount = 0;
            foreach (Character character in units)
            {
                /*
                 * 初版只支持 HumanAIController。
                 */
                if (character.AIController is not HumanAIController humanAI || character.IsIncapacitated || character.LockHands)
                {
                    continue;
                }
                Hull? currentHull = character.CurrentHull ?? Hull.FindHull(character.WorldPosition);
                if (currentHull?.Submarine == null || !TLRtsRoundSubContext.Contains(currentHull.Submarine)) continue;
                /*
                 * 从角色自身物品栏中寻找战斗优先级最高、
                 * 有弹药并且能够使用的远程武器，然后拿到手中。
                 */
                if (!TryEquipBestDirectionalFireWeapon(character, humanAI, out Item? weapon) || weapon == null) continue;
                /*
                * 武器成功装备后才清除旧命令。
                * 如果没有武器，单位会继续执行之前的 RTS 命令。
                */
                ClearHumanObjective(character.ID);
                ClearHumanCombatObjectives(humanAI);
                character.ClearInputs();
                StopMovement(character, humanAI);
                Vector2 guardWorldPosition = ClampInsideHull(character.WorldPosition, currentHull);
                WayPoint? routeAnchor = FindNearestRouteAnchor(guardWorldPosition, currentHull);
                /*
                 * 方向射击结束后，单位会在当前站位进入 Guard，
                 * 不会继续前往之前的移动目标。
                 */
                states[character.ID] = new TLRtsUnitState
                {
                    CharacterId = character.ID,
                    Mode = TLRTSUnitAIState.DirectionalFire,

                    TargetSubId = currentHull.Submarine.ID,
                    TargetHullId = currentHull.ID,
                    TargetWaypointId = routeAnchor?.ID ?? Entity.NullEntityID,
                    TargetLocalPosition = guardWorldPosition - currentHull.Submarine.Position,
                    ArrivalDistance = 35.0f,
                    GuardRadius = 80.0f,
                    AttackTargetId = Entity.NullEntityID,
                    DirectionalFireDirection = direction,
                    DirectionalFireRemaining = DirectionalFireDuration,
                    DirectionalFireAimDelayRemaining = DirectionalFireAimDelay,
                    DirectionalFireWeaponItemId = weapon.ID
                };
                issuedCount++;
            }
            return issuedCount;
        }
        /// <summary>
        /// 从角色自身物品栏中寻找战斗优先级最高的可用远程武器。
        ///
        /// 搜索角色主物品栏，以及主物品栏物品内部的一层容器，
        /// 与原版 AIObjectiveCombat 的物品栏武器搜索范围接近。
        /// </summary>
        private static Item? FindBestDirectionalFireWeapon(Character character)
        {
            Item? bestWeapon = null;
            float bestCombatPriority = float.MinValue;
            bool bestWeaponAlreadyEquipped = false;
            float bestCondition = float.MinValue;
            foreach (Item item in EnumerateDirectionalFireInventoryItems(character))
            {
                if (item == null || item.Removed || item.Condition <= 0.0f) continue;
                RangedWeapon? rangedWeapon = item.GetComponent<RangedWeapon>();
                if (rangedWeapon == null || rangedWeapon.CombatPriority <= 0.0f || rangedWeapon.IsEmpty(character)) continue;
                /*
                * 必须允许放入至少一个手部槽位。
                * 这样可以排除炮台等非手持 RangedWeapon。
                */
                bool canEquipInHands = item.AllowedSlots.Any(CharacterInventory.IsHandSlotType);
                if (!canEquipInHands) continue;
                bool alreadyEquipped = character.HasEquippedItem(item, predicate: CharacterInventory.IsHandSlotType);
                float combatPriority = rangedWeapon.CombatPriority;
                /*
                 * 首先比较 CombatPriority。
                 * 优先级相同则优先当前已经拿在手里的武器，
                 * 再按物品耐久度选择。
                 */
                bool isBetter = bestWeapon == null || combatPriority > bestCombatPriority || MathUtils.NearlyEqual(combatPriority, bestCombatPriority) &&
                     alreadyEquipped && !bestWeaponAlreadyEquipped || MathUtils.NearlyEqual(combatPriority, bestCombatPriority) && alreadyEquipped == bestWeaponAlreadyEquipped &&
                     item.Condition > bestCondition;
                if (!isBetter) continue;
                bestWeapon = item;
                bestCombatPriority = combatPriority;
                bestWeaponAlreadyEquipped = alreadyEquipped;
                bestCondition = item.Condition;
            }
            return bestWeapon;
        }
        /// <summary>
        /// 枚举角色可以用于方向射击的自身物品。
        ///
        /// HashSet 用于防止同一个物品被重复返回。
        /// </summary>
        private static IEnumerable<Item> EnumerateDirectionalFireInventoryItems(Character character)
        {
            HashSet<Item> visited = new();
            foreach (Item item in character.Inventory.AllItems)
            {
                if (item == null || !visited.Add(item)) continue;
                yield return item;

                /*
                 * 搜索工具带、背包等物品内部的一层容器。
                 * 初版不进行无限递归，防止异常容器结构。
                 */
                if (item.OwnInventory == null) continue;
                foreach (Item containedItem in item.OwnInventory.AllItems)
                {
                    if (containedItem != null && visited.Add(containedItem))
                    {
                        yield return containedItem;
                    }
                }
            }
        }
        /// <summary>
        /// 找到并装备战斗优先级最高的可用远程武器。
        ///
        /// 不允许因为手部物品无法收回而将其丢到地上。
        /// </summary>
        private static bool TryEquipBestDirectionalFireWeapon(Character character, HumanAIController humanAI, out Item? weapon)
        {
            weapon = FindBestDirectionalFireWeapon(character);
            if (weapon == null || character.LockHands) return false;
            /*
             * 武器已经在手里时不需要移动物品。
             */
            if (character.HasEquippedItem(weapon, predicate: CharacterInventory.IsHandSlotType)) return true;
            /*
             * 与原版 AIObjectiveCombat.Equip 接近：
             * 先进行一次物品交互，再将物品放入手部槽位。
             */
            weapon.TryInteract(character, forceSelectKey: true);
            bool equipped = humanAI.TakeItem(weapon, character.Inventory, equip: true, wear: false,
                // 手上物品无法收回时，不允许直接丢弃。
                dropOtherIfCannotMove: false,
                // 允许从普通物品栏交换到手部槽位。
                allowSwapping: true,
                // 本次不记录原手持物品，射击后继续持有武器。
                storeUnequipped: false);
            return equipped && character.HasEquippedItem(weapon, predicate: CharacterInventory.IsHandSlotType);
        }
        /// <summary>
        /// 给一组单位下达移动命令。
        /// 单个单位：尽可能抵达鼠标位置。
        /// 多个单位：围绕鼠标位置水平分散。
        /// </summary>
        /// <returns></returns>
        public static bool IssueMove(IEnumerable<Character> selectedCharacters, Vector2 clickedWorldPosition)
        {
            return IssueMove(selectedCharacters, clickedWorldPosition, out _);
        }
        /// <summary>
        /// 按指定队形给一组 RTS 单位下达移动命令。
        /// </summary>
        /// <param name="selectedCharacters"></param>
        /// <param name="clickedWorldPosition"></param>
        /// <param name="formation"></param>
        /// <param name="resolvedSlots"></param>
        /// <returns></returns>
        public static bool IssueMove(IEnumerable<Character> selectedCharacters, Vector2 clickedWorldPosition, TLRtsFormationType formation, out IReadOnlyList<Vector2> resolvedSlots)
        {
            resolvedSlots = Array.Empty<Vector2>();
            if (!IsAuthority || !TLRtsRoundSubContext.IsValid) return false;
            Hull? targetHull = Hull.FindHull(clickedWorldPosition);
            if (targetHull?.Submarine == null || !TLRtsRoundSubContext.Contains(targetHull.Submarine)) return false;

            List<Character> units = selectedCharacters.Where(IsCommandable).Distinct().OrderBy(c => c.WorldPosition.X).ToList();
            if (units.Count == 0) return false;

            List<Vector2> slots = CreateFormationSlots(clickedWorldPosition, targetHull, units.Count, formation);
            slots.Sort((a, b) => a.X.CompareTo(b.X));
            List<Vector2> finalSlots = new(units.Count);
            for (int i = 0; i < units.Count; i++)
            {
                Character character = units[i];
                Vector2 finalWorldPosition = slots[i];
                WayPoint? routeAnchor = FindNearestRouteAnchor(finalWorldPosition, targetHull);
                bool useSwimmingPosition = targetHull.WaterPercentage >= 80f;
                if (!useSwimmingPosition && routeAnchor != null)
                {
                    finalWorldPosition.Y = routeAnchor.WorldPosition.Y;
                }
                finalWorldPosition = ClampInsideHull(finalWorldPosition, targetHull);
                // 保存实际下发给单位的最终位置，供客户端绘制槽位。
                finalSlots.Add(finalWorldPosition);
                AssignMoveState(character, targetHull, routeAnchor, finalWorldPosition, formation);
            }
            resolvedSlots = finalSlots.ToArray();
            MoveOrderIssued?.Invoke(
                clickedWorldPosition,
                resolvedSlots);
            return true;
        }
        public static bool IssueMove(IEnumerable<Character> selectedCharacters, Vector2 clickedWorldPosition, out IReadOnlyList<Vector2> resolvedSlots)
        {
            resolvedSlots = Array.Empty<Vector2>();
            if (!IsAuthority || !TLRtsRoundSubContext.IsValid) return false;
            Hull? targetHull = Hull.FindHull(clickedWorldPosition);
            if (targetHull?.Submarine == null || !TLRtsRoundSubContext.Contains(targetHull.Submarine)) return false;

            List<Character> units = selectedCharacters.Where(IsCommandable).Distinct().OrderBy(c => c.WorldPosition.X).ToList();
            if (units.Count == 0) return false;

            List<Vector2> slots = CreateFormationSlots(clickedWorldPosition, targetHull, units.Count);
            slots.Sort((a, b) => a.X.CompareTo(b.X));
            List<Vector2> finalSlots = new(units.Count);
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
                finalSlots.Add(finalWorldPosition);
                AssignMoveState(character, targetHull, routeAnchor, finalWorldPosition);
            }
            resolvedSlots = finalSlots.ToArray();
            MoveOrderIssued?.Invoke(
                clickedWorldPosition,
                resolvedSlots);
            return true;
        }
        public static void AssignMoveState(Character character, Hull targetHull, WayPoint? routeAnchor, Vector2 finalWorldPosition, TLRtsFormationType formation = TLRtsFormationType.RegularSpread)
        {
            /*
             * 新移动命令会立即中断旧的方向射击。
             */
            ClearDirectionalFireInputs(character);
            Submarine targetSub = targetHull.Submarine;
            bool isVolleyStack = formation == TLRtsFormationType.VolleyStack;
            float arrivalDistance = isVolleyStack ? VolleyStackArrivalDistance : character.AIController is HumanAIController ? 50.0f : 35.0f;
            float guardRadius = isVolleyStack ? VolleyStackGuardRadius : Math.Max(arrivalDistance + 40.0f, 80.0f);
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
                ArrivalDistance = arrivalDistance,
                GuardRadius = guardRadius,
                AttackTargetId = Entity.NullEntityID
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
                !target.Enabled)
            {
                return false;
            }
            // TeamID 相同绝不接受强制攻击；不要依赖原版跨物种友军判断。
            if (attacker.TeamID == target.TeamID || TLRtsFriendlyPolicy.IsProtectedAlly(attacker, target))
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
                /*
                 * 新的角色攻击命令会立即中断方向射击。
                 */
                ClearDirectionalFireInputs(character);
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
        /// <summary>
        /// 为刚部署的单位建立当前位置 Guard。
        /// 人类额外获得“在此等待”的强制命令；生物沿用 RTS Guard 控制。
        /// </summary>
        public static bool TryInitializeDeployedGuard(Character character)
        {
            if (!IsAuthority || !TLRtsUnitRegistryPermission.IsControllable(character) || !TryEnsureGuardAnchor(character, out TLRtsUnitState? state) || state == null)
            {
                return false;
            }
            if (character.AIController is HumanAIController humanAI)
            {
                if (!TryGetValidTarget(state, out _, out Hull? guardHull) || guardHull == null)
                {
                    RemoveState(character.ID);
                    return false;
                }
                // 沿用现有方法建立原版 wait/GoTo 命令。
                AssignHumanMoveObjective(character, humanAI, state, guardHull, state.GetTargetWorldPosition());
                StopMovement(character, humanAI);
            }
            return true;
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
        private static List<Vector2> CreateFormationSlots(Vector2 centerWorldPosition, Hull targetHull, int unitCount, TLRtsFormationType formation = TLRtsFormationType.RegularSpread)
        {
            List<Vector2> slots = new(Math.Max(unitCount, 0));
            if (unitCount <= 0) return slots;
            Rectangle hullRect = targetHull.WorldRect;
            float left = hullRect.Left + HullHorizontalMargin;
            float right = hullRect.Right - HullHorizontalMargin;
            float bottom = hullRect.Y - hullRect.Height + HullVerticalMargin;
            float top = hullRect.Y - HullVerticalMargin;

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
                slots.Add(new Vector2(MathHelper.Clamp(centerWorldPosition.X, left, right), centerY));
                return slots;
            }
            float requestedSpacing = formation switch
            {
                TLRtsFormationType.CompactMarch => CompactFormationSpacing,
                TLRtsFormationType.RegularSpread => RegularFormationSpacing,
                TLRtsFormationType.VolleyStack => VolleyStackFormationSpacing,
                _ => RegularFormationSpacing
            };
            float availableWidth = Math.Max(right - left, 1.0f);
            float maximumSpacingThatFits = availableWidth / Math.Max(unitCount - 1, 1);
            float actualSpacing = Math.Min(requestedSpacing, maximumSpacingThatFits);
            float totalWidth = actualSpacing * (unitCount - 1);
            float minimumCenterX = left + totalWidth * 0.5f;
            float maximumCenterX = right - totalWidth * 0.5f;
            float formationCenterX = MathHelper.Clamp(centerWorldPosition.X, minimumCenterX, maximumCenterX);
            for (int i = 0; i < unitCount; i++)
            {
                float offsetX = (i - (unitCount - 1) * 0.5f) * actualSpacing;
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
        /// 移除一个单位的 RTS 状态。
        /// </summary>
        /// <param name="characterId"></param>
        private static void RemoveState(ushort characterId)
        {
            if (states.TryGetValue(characterId, out TLRtsUnitState? oldState) && oldState.Mode == TLRTSUnitAIState.DirectionalFire && Entity.FindEntityByID(characterId) is Character character)
            {
                ClearDirectionalFireInputs(character);
            }
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
            ai.SteeringManager?.Reset();
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
        private static bool TryGetActiveState(Character? character, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TLRtsUnitState? state)
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
        public static void Dispose()
        {
            ReleaseAllUnitsToVanillaAi();
            ClearRoundState();
            TLRtsUnitRegistryPermission.UnitUnregistered -= OnUnitUnregistered;
            initialized = false;
            MoveOrderIssued = null;
            RoundStateCleared = null;
        }

        public static void ClearRoundState()
        {
            foreach (ushort characterId in humanObjectives.Keys.ToArray())
            {
                ClearHumanObjective(characterId);
            }
            states.Clear();
            forcedCreatureAttackParams.Clear();
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

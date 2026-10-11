using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public static class TLRtsDeploymentService
    {
        /// <summary>
        /// 创建并支付一个 RTS 单位。
        ///
        /// 顺序：
        /// 1. 再次验证来源、位置、费用；
        /// 2. 创建尚未向客户端广播的角色；
        /// 3. 设置阵营并注册 RTS 控制资格；
        /// 4. 扣款；失败则移除刚创建的角色；
        /// 5. 向客户端广播生成，再发放 NPC 预设物品。
        /// </summary>
        public static bool TryDeploy(TLRtsDeploymentPrefabDefinition prefab, Hull targetHull, Vector2 worldPosition, TLRtsDeploymentPaymentContext paymentContext, out Character? deployedCharacter, out string error)
        {
            deployedCharacter = null;
            error = string.Empty;
            if (!TLRtsSystem.IsAuthority || !paymentContext.IsAuthoritative)
            {
                error = "只有单人游戏或多人服务器可以生成 RTS 单位。";
                return false;
            }
            Character commander = paymentContext.CommanderCharacter;
            if (commander == null || commander.Removed || commander.IsDead)
            {
                error = "RTS 指挥官已失效。";
                return false;
            }
            if (!TryValidateTarget(commander, targetHull, worldPosition, out error)) return false;
            if (!TLRtsDeploymentSpawnSourceValidator.TryValidate(prefab, out error)) return false;
            if (!HasSupportedFirstVersionCosts(prefab, out error)) return false;
            if (!TLRtsDeploymentCostSystem.CanAfford(prefab, paymentContext, out error)) return false;
            if (!TryPrepareCharacter(prefab, commander.TeamID, out Identifier species, out CharacterInfo? characterInfo, out HumanPrefab? humanPrefab, out error)) return false;
#if SERVER
            // 支付前确认服务器能够发送角色生成事件。
            if (GameMain.NetworkMember is { IsServer: true } && Entity.Spawner == null)
            {
                error = "服务器角色生成器尚未就绪。";
                return false;
            }
#endif
            Character? created;
            try
            {
                /*
                 * 不立即广播，也不生成初始物品：
                 * 如果随后扣款失败，可以移除这个尚未发布的角色。
                 * 人类 NPC 的预设物品会在支付成功后单独发放。
                 */
                created = Character.Create(
                    species,
                    worldPosition,
                    ToolBox.RandomSeed(8),
                    characterInfo,
                    hasAi: true,
                    createNetworkEvent: false,
                    throwErrorIfNotFound: false,
                    spawnInitialItems: prefab.UnitType == TLRtsDeploymentUnitType.Creature);
            }
            catch (Exception exception)
            {
                error = $"创建 RTS 单位时出错：{exception.Message}";
                return false;
            }
            if (created == null)
            {
                error = $"无法创建角色预制体：{species}。";
                return false;
            }
            // 在广播之前设置阵营，使客户端收到的初始状态尽量正确。
            created.TeamID = commander.TeamID;
            if (humanPrefab != null)
            {
                created.HumanPrefab = humanPrefab;
            }
            Identifier unitClass = prefab.UnitType == TLRtsDeploymentUnitType.HumanNpc ? "human".ToIdentifier() : "creature".ToIdentifier();
            created.Enabled = true;
            if (!TLRtsUnitRegistryPermission.Register(created, unitClass, isSpawnByRts: true))
            {
                // 没有可用 AI 的角色不能成为 RTS 单位，也不能收钱。
                created.Remove();
                error = "角色已创建，但未能注册为可指挥的 RTS 单位。";
                return false;
            }
            // 所有部署单位出生时都建立 Guard 点。
            // 人类还会获得原版的“在此等待”命令，避免出生后执行自主工作。
            if (!TLRtsSystem.TryInitializeDeployedGuard(created))
            {
                TLRtsUnitRegistryPermission.Unregister(created);
                created.Remove();

                error = "单位已创建，但无法建立初始 Guard 点。";
                return false;
            }
            // 部署的怪物出生时先原地 Guard，不让原版 AI 抢先寻找船员目标。
            // 人类单位仍按现有 Guard/原版人类 AI 逻辑运作。
            if (prefab.UnitType == TLRtsDeploymentUnitType.Creature && !TLRtsSystem.TryInitializeDeployedGuard(created))
            {
                TLRtsUnitRegistryPermission.Unregister(created);
                created.Remove();

                error = "生物已创建，但无法建立初始 Guard 点。";
                return false;
            }
            if (!TLRtsDeploymentCostSystem.TryPay(prefab, paymentContext, out error))
            {
                // 扣款失败：撤销注册并移除尚未广播的角色。
                TLRtsUnitRegistryPermission.Unregister(created);
                created.Remove();
                return false;
            }
#if SERVER
            if (GameMain.NetworkMember is { IsServer: true })
            {
                // 支付成功后才向多人客户端广播这个新角色。
                Entity.Spawner!.CreateNetworkEvent(new EntitySpawner.SpawnEntity(created));
            }
#endif
            if (humanPrefab != null)
            {
                /*
                 * 与游戏自带 NPC 生成流程相同：
                 * 先发布角色生成事件，再生成其预设装备。
                 *
                 * 装备生成异常不撤销已完成的单位部署；
                 * 单位仍然存在，但服务器日志会记录装备错误。
                 */
                try
                {
                    humanPrefab.GiveItems(created, targetHull.Submarine, spawnPoint: null);
                    humanPrefab.InitializeCharacter(created);
                }
                catch (Exception exception)
                {
                    DebugConsole.NewMessage($"[RTS 部署] {prefab.Identifier} 已生成，但 NPC 装备或初始化失败：{exception.Message}", Color.OrangeRed);
                }
            }
            deployedCharacter = created;
            DebugConsole.NewMessage($"[RTS 部署] 已生成 {prefab.Identifier}，角色 ID={created.ID}，阵营={created.TeamID}。", Color.LightGreen);
            return true;
        }
        /// <summary>
        /// 部署位置必须仍位于指挥官阵营的主潜艇房间。
        /// 调用方即使已经验证过，这里仍再次检查，防止状态变化。
        /// </summary>
        private static bool TryValidateTarget(Character commander, Hull targetHull, Vector2 worldPosition, out string error)
        {
            error = string.Empty;

            if (!TLRtsRoundSubContext.Refresh())
            {
                error = "前哨站停靠网络已经失效。";
                return false;
            }

            if (commander.Submarine == null ||
                !TLRtsRoundSubContext.Contains(commander.Submarine))
            {
                error = "指挥官已经离开 RTS 活动范围。";
                return false;
            }

            Submarine? targetSub = targetHull?.Submarine;
            if (targetHull == null ||
                targetHull.Removed ||
                targetSub == null ||
                targetSub.Removed ||
                !TLRtsRoundSubContext.Contains(targetSub) ||
                Hull.FindHull(worldPosition) != targetHull)
            {
                error = "部署位置不在前哨站停靠网络的有效房间内。";
                return false;
            }

            return true;
        }
        /// <summary>
        /// 首版只允许实际支付马克。
        /// 其他资源可以继续留在数据结构和 UI 中，但暂不执行扣费，
        /// 避免现有 TryPay 在多资源情况下发生部分支付。
        /// </summary>
        private static bool HasSupportedFirstVersionCosts(TLRtsDeploymentPrefabDefinition prefab, out string error)
        {
            error = string.Empty;
            if (prefab.Costs == null)
            {
                error = "单位费用列表无效。";
                return false;
            }
            foreach (TLRtsDeploymentCostDefinition cost in prefab.Costs)
            {
                if (cost == null || cost.Amount < 0)
                {
                    error = "单位费用项目无效。";
                    return false;
                }

                if (cost.Amount == 0)
                {
                    continue;
                }

                if (!string.Equals(
                        cost.ResourceIdentifier,
                        TLRtsDeploymentResourceIds.Marks,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error =
                        $"首版部署尚不支持实际支付资源“" +
                        $"{cost.ResourceIdentifier}”。";
                    return false;
                }
            }

            return true;
        }
        /// <summary>
        /// 根据注册定义准备角色种类与角色资料。
        /// 人类使用 NPCSet 中的预设；生物使用 SpeciesIdentifier。
        /// </summary>
        private static bool TryPrepareCharacter(TLRtsDeploymentPrefabDefinition prefab, CharacterTeamType team, out Identifier species, out CharacterInfo? characterInfo, out HumanPrefab? humanPrefab, out string error)
        {
            species = Identifier.Empty;
            characterInfo = null;
            humanPrefab = null;
            error = string.Empty;
            try
            {
                if (prefab.UnitType == TLRtsDeploymentUnitType.HumanNpc)
                {
                    humanPrefab = NPCSet.Get(prefab.NpcSetIdentifier.ToIdentifier(), prefab.NpcIdentifier.ToIdentifier(), false);
                    if (humanPrefab == null)
                    {
                        error = "找不到指定的人类 NPC 预制体。";
                        return false;
                    }
                    species = CharacterPrefab.HumanSpeciesName;
                    characterInfo = humanPrefab.CreateCharacterInfo();
                    // Character.Create 会从 CharacterInfo 读取初始阵营。
                    characterInfo.TeamID = team;
                    return true;
                }
                if (prefab.UnitType == TLRtsDeploymentUnitType.Creature)
                {
                    species = prefab.SpeciesIdentifier.ToIdentifier();
                    if (species.IsEmpty || species == CharacterPrefab.HumanSpeciesName)
                    {
                        error = "非人类单位的 SpeciesIdentifier 无效。";
                        return false;
                    }
                    CharacterPrefab? creaturePrefab = CharacterPrefab.FindBySpeciesName(species);
                    if (creaturePrefab == null)
                    {
                        error = $"找不到生物预制体：{prefab.SpeciesIdentifier}。";
                        return false;
                    }
                    if (creaturePrefab.HasCharacterInfo)
                    {
                        characterInfo = new CharacterInfo(species)
                        {
                            TeamID = team
                        };
                    }
                    return true;
                }
                error = "未知的 RTS 部署单位类型。";
                return false;
            }
            catch (Exception exception)
            {
                error = $"准备角色预制体时出错：{exception.Message}";
                return false;
            }
        }
    }
}

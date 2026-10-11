using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 为 RTS 部署的非人类单位补充友军规则。
    /// </summary>
    public static class TLRtsFriendlyPolicy
    {
        public static void Init(Harmony harmony)
        {
            // 船员 AI 是否认为某角色是友军。
            var humanIsFriendly = AccessTools.Method(typeof(HumanAIController), nameof(HumanAIController.IsFriendly), new[]
            {
                typeof(Character),
                typeof(Character),
                typeof(bool),
                typeof(bool)
            });
            // 怪物受攻击后的友军判断、避免误伤等逻辑会使用此方法。
            var characterIsFriendly = AccessTools.Method(typeof(Character), nameof(Character.IsFriendly), new[] { typeof(Character), typeof(Character) });
            // 怪物搜索目标时，提前排除友方角色和己方潜艇设施。
            var getTargetingTags = AccessTools.Method(typeof(EnemyAIController), "GetTargetingTags", new[] { typeof(AITarget) });
            // 阻止受攻击等其他路径重新强制选中友军。
            var selectTarget = AccessTools.Method(typeof(EnemyAIController), nameof(EnemyAIController.SelectTarget), new[] { typeof(AITarget), typeof(float) });
            if (humanIsFriendly != null && characterIsFriendly != null && getTargetingTags != null && selectTarget != null)
            {
                harmony.Patch(humanIsFriendly, postfix: new HarmonyMethod(typeof(TLRtsFriendlyPolicy), nameof(AfterHumanIsFriendly)));
                harmony.Patch(characterIsFriendly, postfix: new HarmonyMethod(typeof(TLRtsFriendlyPolicy), nameof(AfterCharacterIsFriendly)));
                harmony.Patch(getTargetingTags, prefix: new HarmonyMethod(typeof(TLRtsFriendlyPolicy), nameof(BeforeGetTargetingTags)));
                harmony.Patch(selectTarget, prefix: new HarmonyMethod(typeof(TLRtsFriendlyPolicy), nameof(BeforeSelectTarget)));
                DebugConsole.NewMessage("RTS AI补丁加载完成");
            }
        }
        /// <summary>
        /// 判断某角色是否是 RTS 部署的非人类单位。
        /// 多人由服务端判定；单人读取本地注册表。
        /// </summary>
        private static bool IsDeployedCreature(Character? character)
        {
            if (character == null || character.Removed) return false;
            // 多人客户端不负责决定 AI 的敌友关系。
            if (GameMain.NetworkMember is { IsClient: true }) return false;
            return TLRtsUnitRegistryPermission.TryGetRegistration(character, out TLRtsUnitRegistration? registration) && registration != null && registration.IsSpawnByRts && registration.UnitClass == "creature".ToIdentifier();
        }
        /// <summary>
        /// 两个角色原本属于友方阵营，且至少一方是 RTS 部署生物时，
        /// 我们把它们明确视为友军。
        /// Team1 与 Team2 在 PvP 中仍然互为敌人。
        /// </summary>
        public static bool IsProtectedAlly(Character? first, Character? second)
        {
            return first != null && second != null && first != second && Character.IsOnFriendlyTeam(first, second) && (IsDeployedCreature(first) || IsDeployedCreature(second));
        }
        /// <summary>
        /// 让原版人类 AI 不再把同阵营 RTS 生物当成敌人。
        /// onlySameTeam=true 时仍尊重原调用者要求的严格同队限制。
        /// </summary>
        private static void AfterHumanIsFriendly(Character me, Character other, bool onlySameTeam, ref bool __result)
        {
            if (__result) return;
            if (onlySameTeam && me?.TeamID != other?.TeamID) return;
            if (IsProtectedAlly(me, other))
            {
                __result = true;
            }
        }
        /// <summary>
        /// 补充怪物侧的友军判断，包括受击反应和部分避免误伤逻辑。
        /// </summary>
        private static void AfterCharacterIsFriendly(Character me, Character other, ref bool __result)
        {
            if (!__result && IsProtectedAlly(me, other))
            {
                __result = true;
            }
        }
        /// <summary>
        /// 从原版怪物目标搜索中排除友军。
        /// 只影响 RTS 部署生物
        /// </summary>
        private static bool BeforeGetTargetingTags(EnemyAIController __instance, AITarget aiTarget, ref IEnumerable<Identifier> __result)
        {
            Character creature = __instance.Character;
            if (!IsDeployedCreature(creature) || aiTarget?.Entity == null) return true;
            if (aiTarget.Entity is Character target && IsProtectedAlly(creature, target))
            {
                __result = Array.Empty<Identifier>();
                return false;
            }
            // 防止待命或“释放全部 AI”后，怪物转而攻击己方船内物品/墙体。
            if (aiTarget.Entity is not Character && aiTarget.Entity.Submarine is Submarine sub && Character.IsOnFriendlyTeam(creature.TeamID, sub.TeamID))
            {
                __result = Array.Empty<Identifier>();
                return false;
            }
            return true;
        }
        /// <summary>
        /// 拦截受击反应等其他选目标入口，避免重新指定友军。
        /// </summary>
        private static bool BeforeSelectTarget(EnemyAIController __instance, AITarget target)
        {
            if (target?.Entity is Character targetCharacter && IsProtectedAlly(__instance.Character, targetCharacter)) return false;
            return true;
        }
    }
}

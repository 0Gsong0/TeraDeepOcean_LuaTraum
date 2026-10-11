using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 部署之前检查RTS预制体是否真的存在
    /// </summary>
    public static class TLRtsDeploymentSpawnSourceValidator
    {
        /// <summary>
        /// 验证人类 NPC 或非人类生物的生成来源。
        /// </summary>
        public static bool TryValidate(TLRtsDeploymentPrefabDefinition? prefab, out string error)
        {
            error = string.Empty;
            if (prefab == null)
            {
                error = "部署单位定义不存在。";
                return false;
            }
            switch (prefab.UnitType)
            {
                case TLRtsDeploymentUnitType.HumanNpc:
                    HumanPrefab? humanPrefab = NPCSet.Get(prefab.NpcSetIdentifier.ToIdentifier(), prefab.NpcIdentifier.ToIdentifier(), false);
                    if (humanPrefab == null)
                    {
                        error = $"找不到人类 NPC：{prefab.NpcSetIdentifier}/{prefab.NpcIdentifier}。";
                        return false;
                    }
                    return true;
                case TLRtsDeploymentUnitType.Creature:
                    Identifier species = prefab.SpeciesIdentifier.ToIdentifier();
                    if(species.IsEmpty||species == CharacterPrefab.HumanSpeciesName)
                    {
                        error = "非人类单位的 SpeciesIdentifier 无效。";
                        return false;
                    }
                    if (CharacterPrefab.FindBySpeciesName(species) == null)
                    {
                        error = $"找不到生物预制体：{prefab.SpeciesIdentifier}。";
                        return false;
                    }
                    return true;
                default:
                    error = $"未知的部署单位类型：{prefab.UnitType}。";
                    return false;
            }
        }
    }
}

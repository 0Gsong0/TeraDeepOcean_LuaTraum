using Lidgren.Network;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// RTS 可部署单位预制体注册表。
    /// </summary>
    public static class TLRtsDeploymentRegistry
    {
        /// <summary>
        /// 已注册的部署预制体。
        /// </summary>
        private static readonly Dictionary<Identifier, TLRtsDeploymentPrefabDefinition> prefabs = new();
        /// <summary>
        /// 防止注册和 UI 读取同时修改字典。
        /// </summary>
        private static readonly object registryLock = new();
        /// <summary>
        /// 注册一个部署预制体。
        /// </summary>
        /// <param name="sourcePackageName"></param>
        /// <param name="definition"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        public static bool TryRegister(string sourcePackageName, TLRtsDeploymentPrefabDefinition definition, out string error)
        {
            error = string.Empty;
            if (definition == null)
            {
                error = "部署预制体定义不能为空。";
                return false;
            }
            if (!TryGetCanonicalEnabledPackageName(sourcePackageName, out string canonicalPackageName, out error)) return false;
            return TryRegisterPrepared(canonicalPackageName, definition, out error);
        }
        /// <summary>
        /// 注册已经确认来源的预制体。
        /// 只处理普通数据并写入带锁的字典，因此允许在后台线程调用。
        /// </summary>
        /// <param name="canonicalPackageName"></param>
        /// <param name="definition"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        internal static bool TryRegisterPrepared(string canonicalPackageName, TLRtsDeploymentPrefabDefinition definition, out string error)
        {
            error = string.Empty;
            if (definition == null)
            {
                error = "部署预制体定义不能为空。";
                return false;
            }
            if(!TryNormalizeDefinition(canonicalPackageName, definition,out TLRtsDeploymentPrefabDefinition? normalizedDefinition, out error)|| normalizedDefinition == null) return false;
            Identifier prefabIdentifier = normalizedDefinition.Identifier.ToIdentifier();
            lock (registryLock)
            {
                if (prefabs.TryGetValue(prefabIdentifier, out TLRtsDeploymentPrefabDefinition? existing))
                {
                    bool sameSourcePackage = string.Equals(existing.SourcePackageName, normalizedDefinition.SourcePackageName, StringComparison.OrdinalIgnoreCase);
                    if (!sameSourcePackage)
                    {
                        error = $"RTS 部署预制体“{normalizedDefinition.Identifier}”" + $"已经由内容包“{existing.SourcePackageName}”注册，" + $"内容包“{normalizedDefinition.SourcePackageName}”不能覆盖它。";
                        return false;
                    }
                }
                prefabs[prefabIdentifier] = normalizedDefinition;
            }
            return true;
        }
        public static bool TryGet(string identifier, out TLRtsDeploymentPrefabDefinition? prefab)
        {
            prefab = null;
            if (String.IsNullOrWhiteSpace(identifier)) return false;
            Identifier prefabId = identifier.ToIdentifier();
            if (prefabId.IsEmpty) return false;
            lock (registryLock)
            {
                if (!prefabs.TryGetValue(prefabId, out TLRtsDeploymentPrefabDefinition? clonePrefab)) return false;
                prefab = CloneDefinition(clonePrefab);
                return true;
            }
        }
        public static IReadOnlyList<TLRtsDeploymentPrefabDefinition> GetAll()
        {
            lock (registryLock)
            {
                return prefabs.Values.OrderBy(p => p.Identifier, StringComparer.OrdinalIgnoreCase).Select(CloneDefinition).ToArray();
            }
        }
        /// <summary>
        /// 解析指定部署预制体的图标路径。
        /// </summary>
        /// <param name="prefabIdentifier"></param>
        /// <param name="iconPath"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        public static bool TryResolveIconPath(string prefabIdentifier, out ContentPath iconPath, out string error)
        {
            iconPath = ContentPath.Empty;
            error = string.Empty;
            if (!TryGet(prefabIdentifier, out var definition) || definition == null)
            {
                error = $"找不到 RTS 部署预制体“{prefabIdentifier}”。";
                return false;
            }
            if (!TryFindEnabledContentPackage(definition.SourcePackageName, out var sourcePackage))
            {
                error = $"部署预制体“{definition.Identifier}”所属的内容包“{definition.SourcePackageName}”当前没有启用。";
                return false;
            }
            try
            {
                /*
                 * ContentPath.FromRaw(contentPackage, rawPath) 同时支持：
                 *
                 * 1. "%ModDir%/..."
                 *    用传入的 sourcePackage 替换 %ModDir%。
                 *
                 * 2. "Content/Images/..."
                 *    路径中没有 %ModDir%，所以保持为游戏根目录相对路径。
                 *
                 * 3. "%ModDir:OtherModName%/..."
                 *    由游戏的 ContentPath 系统查找另一个内容包。
                 */
                iconPath = ContentPath.FromRaw(sourcePackage, definition.IconPath);
                /*
                 * 访问 Value 会真正执行 %ModDir% 替换。
                 * 如果引用了不存在的其他内容包，也会在这里抛出异常。
                 */
                _ = iconPath.Value;
                return true;
            }
            catch (Exception exception)
            {
                iconPath = ContentPath.Empty;
                error = $"无法解析部署预制体“{definition.Identifier}”的图标路径“{definition.IconPath}”：{exception.Message}";
                return false;
            }
        }
        /// <summary>
        /// 移除指定内容包注册的全部部署预制体。
        /// </summary>
        /// <param name="sourcePackageName"></param>
        /// <returns></returns>
        public static int UnregisterBySourcePackage(string sourcePackageName)
        {
            if (string.IsNullOrWhiteSpace(sourcePackageName)) return 0;
            lock (registryLock)
            {
                Identifier[] identifiersToRemove = prefabs
                    .Where(p => String.Equals(p.Value.SourcePackageName, sourcePackageName, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Key).ToArray();
                foreach (Identifier identifier in identifiersToRemove)
                {
                    prefabs.Remove(identifier);
                }
                return identifiersToRemove.Length;
            }
        }
        /// <summary>
        /// 清空全部部署预制体。
        /// </summary>
        public static void Clear()
        {
            lock (registryLock)
            {
                prefabs.Clear();
            }
        }
        private static TLRtsDeploymentPrefabDefinition CloneDefinition(TLRtsDeploymentPrefabDefinition old)
        {
            return new TLRtsDeploymentPrefabDefinition
            {
                Identifier = old.Identifier,
                DisplayName = old.DisplayName,
                Description = old.Description,
                SourcePackageName = old.SourcePackageName,
                IconPath = old.IconPath,
                UnitType = old.UnitType,
                NpcSetIdentifier = old.NpcSetIdentifier,
                NpcIdentifier = old.NpcIdentifier,
                SpeciesIdentifier = old.SpeciesIdentifier,
                Costs = old.Costs.Select(c => new TLRtsDeploymentCostDefinition
                {
                    ResourceIdentifier = c.ResourceIdentifier,
                    Amount = c.Amount
                }).ToList()
            };
        }
        /// <summary>
        /// 从已启用内容包中找到注册来源，并返回正式内容包名称。
        /// </summary>
        /// <param name="sourcePackageName"></param>
        /// <param name="canonicalPackageName"></param>
        /// <param name="error"></param>
        /// <returns>PackageName</returns>
        internal static bool TryGetCanonicalEnabledPackageName(string sourcePackageName, out string canonicalPackageName, out string error)
        {
            canonicalPackageName = string.Empty;
            error = string.Empty;
            if (!TryFindEnabledContentPackage(sourcePackageName, out ContentPackage? sourcePackage) || sourcePackage == null)
            {
                error = $"找不到已经启用的内容包“{sourcePackageName}”，无法注册 RTS 部署预制体。";
                return false;
            }
            /*
             * 保存实际内容包的正式名称，而不是外部传入的别名。
             * 这样图标路径和重复注册判断会保持一致。
             */
            canonicalPackageName = sourcePackage.Name;
            return true;
        }
        /// <summary>
        /// 尝试从已启用内容包中找到注册来源。
        /// </summary>
        /// <param name="sourcePackageName"></param>
        /// <param name="contentPackage"></param>
        /// <returns>ContentPackage</returns>
        private static bool TryFindEnabledContentPackage(string sourcePackageName, out ContentPackage? contentPackage)
        {
            contentPackage = null;
            if (string.IsNullOrWhiteSpace(sourcePackageName)) return false;
            contentPackage = ContentPackageManager.EnabledPackages.All.FirstOrDefault(p => p.NameMatches(sourcePackageName.Trim()));
            return contentPackage != null;
        }
        /// <summary>
        /// 验证传入的预制体
        /// </summary>
        /// <param name="sourcePackage"></param>
        /// <param name="input"></param>
        /// <param name="normalized"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        private static bool TryNormalizeDefinition(string canonicalPackageName, TLRtsDeploymentPrefabDefinition input, out TLRtsDeploymentPrefabDefinition? normalized, out string error)
        {
            normalized = null;
            error = string.Empty;
            Identifier prefabId = input.Identifier?.Trim().ToIdentifier() ?? String.Empty;
            if (prefabId.IsEmpty)
            {
                error = "部署预制体 Identifier 不能为空。";
                return false;
            }
            if (String.IsNullOrWhiteSpace(input.DisplayName))
            {
                error = $"部署预制体“{prefabId}”的 DisplayName 不能为空。";
                return false;
            }
            if (!Enum.IsDefined(typeof(TLRtsDeploymentUnitType), input.UnitType))
            {
                error = $"部署预制体“{prefabId}”使用了未知的单位类型：{input.UnitType}。";
                return false;
            }
            if (input.UnitType == TLRtsDeploymentUnitType.HumanNpc)
            {
                if (String.IsNullOrWhiteSpace(input.NpcSetIdentifier))
                {
                    error = $"人类部署预制体“{prefabId}”缺少 NpcSetIdentifier ";
                    return false;
                }
                if (String.IsNullOrWhiteSpace(input.NpcIdentifier))
                {
                    error = $"人类部署预制体“{prefabId}”缺少 NpcIdentifier ";
                    return false;
                }
            }
            if (input.UnitType == TLRtsDeploymentUnitType.Creature)
            {
                if (String.IsNullOrWhiteSpace(input.SpeciesIdentifier))
                {
                    error = $"生物部署预制体“{prefabId}”缺少 SpeciesIdentifier ";
                    return false;
                }
            }
            if (!TryNormalizeCosts(input.Costs, out List<TLRtsDeploymentCostDefinition>? normalizedCosts, out error))
            {
                error = $"部署预制体“{prefabId}”的费用无效：" + error;
                return false;
            }
            normalized = new TLRtsDeploymentPrefabDefinition
            {
                Identifier = prefabId.value,
                DisplayName = input.DisplayName.Trim(),
                Description = input.Description.Trim() ?? "",
                SourcePackageName = canonicalPackageName,
                IconPath = string.IsNullOrWhiteSpace(input.IconPath) ? TLRtsDeploymentDefaults.DefaultIconPath : input.IconPath.Trim(),
                UnitType = input.UnitType,
                NpcSetIdentifier = input.NpcSetIdentifier.Trim() ?? "",
                NpcIdentifier = input.NpcIdentifier.Trim() ?? "",
                SpeciesIdentifier = input.SpeciesIdentifier.Trim() ?? "",
                Costs = normalizedCosts
            };
            return true;
        }
        /// <summary>
        /// 尝试归一化费用
        /// </summary>
        /// <param name="inputCosts"></param>
        /// <param name="normalizedCosts"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        private static bool TryNormalizeCosts(List<TLRtsDeploymentCostDefinition>? inputCosts, out List<TLRtsDeploymentCostDefinition>? normalizedCosts, out string error)
        {
            normalizedCosts = new List<TLRtsDeploymentCostDefinition>();
            error = string.Empty;
            if (inputCosts == null)
            {
                normalizedCosts.Add(CreateDefaultMarksCost());
                return true;
            }
            TLRtsDeploymentCostDefinition[] inputs = inputCosts.ToArray();
            if (inputs.Length == 0)
            {
                normalizedCosts.Add(CreateDefaultMarksCost());
                return true;
            }
            var mergedCosts = new Dictionary<Identifier, int>();
            foreach (TLRtsDeploymentCostDefinition input in inputs)
            {
                if (input == null)
                {
                    error = "费用列表中不能包含空项目。";
                    return false;
                }
                Identifier resourceId = input.ResourceIdentifier?.Trim().ToIdentifier() ?? String.Empty;
                if (resourceId.IsEmpty)
                {
                    error = "ResourceIdentifier 不能为空。";
                    return false;
                }
                if (input.Amount < 0)
                {
                    error = $"资源“{resourceId}”的费用不能是负数。";
                    return false;
                }
                try
                {
                    /*
                     * checked 防止多个超大费用相加后发生整数溢出。
                     */
                    mergedCosts[resourceId] = checked(mergedCosts.GetValueOrDefault(resourceId) + input.Amount);
                }
                catch (OverflowException)
                {
                    error = $"资源“{resourceId}”的总费用超出了整数范围。";
                    return false;
                }
            }
            normalizedCosts = mergedCosts.OrderBy(r => r.Key.Value, StringComparer.OrdinalIgnoreCase)
                .Select(r => new TLRtsDeploymentCostDefinition
                {
                    ResourceIdentifier = r.Key.Value,
                    Amount = r.Value
                }).ToList();
            return true;
        }
        /// <summary>
        /// 创建默认费用：0 马克。
        /// </summary>
        /// <returns></returns>
        private static TLRtsDeploymentCostDefinition CreateDefaultMarksCost()
        {
            return new TLRtsDeploymentCostDefinition
            {
                ResourceIdentifier = TLRtsDeploymentResourceIds.Marks,
                Amount = 0
            };
        }
    }
}

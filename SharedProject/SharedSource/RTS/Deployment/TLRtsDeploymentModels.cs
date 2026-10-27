using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// RTS 可部署单位的生成类型。
    /// </summary>
    public enum TLRtsDeploymentUnitType
    {
        HumanNpc = 0,
        Creature = 1
    }
    /// <summary>
    /// RTS 部署系统内置的资源标识。
    /// </summary>
    public static class TLRtsDeploymentResourceIds
    {
        /// <summary>
        /// 游戏战役货币：马克。
        /// </summary>
        public const string Marks = "marks";
    }
    /// <summary>
    /// 一项部署费用。
    /// 一个部署预制体可以同时拥有多项费用
    /// </summary>
    public sealed class TLRtsDeploymentCostDefinition
    {
        public string ResourceIdentifier { get; set; } = TLRtsDeploymentResourceIds.Marks;
        public int Amount { get; set; }
    }
    /// <summary>
    /// 一个可部署单位的注册定义。
    /// </summary>
    public sealed class TLRtsDeploymentPrefabDefinition
    {
        /// <summary>
        /// 部署预制体的唯一标识。
        /// </summary>
        public string Identifier { get; set; } = string.Empty;
        /// <summary>
        /// UI 中显示的单位名称。
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;
        /// <summary>
        /// 鼠标停留在单位卡片上时显示的说明。
        /// </summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>
        /// 定义这个部署预制体的内容包名称。
        /// 它用于确定下面 IconPath 中的 %ModDir% 属于哪个模组。
        /// </summary>
        public string SourcePackageName { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public TLRtsDeploymentUnitType UnitType { get; set; } = TLRtsDeploymentUnitType.HumanNpc;
        #region 人类 NPC 来源
        public string NpcSetIdentifier { get; set; } = string.Empty;
        public string NpcIdentifier { get; set; } = string.Empty;
        #endregion
        #region 非人类生物来源
        public string SpeciesIdentifier { get; set; } = string.Empty;
        #endregion
        public List<TLRtsDeploymentCostDefinition> Costs { get; set; } = new()
        {
            new TLRtsDeploymentCostDefinition
            {
                ResourceIdentifier = TLRtsDeploymentResourceIds.Marks,
                Amount = 0
            }
        };
    }

    public static class TLRtsDeploymentDefaults
    {
        /// <summary>
        /// 本模组内容包的正式名称。
        ///
        /// 必须与 filelist.xml 中的 contentpackage name 保持一致。
        /// </summary>
        public const string OwnerContentPackageName = "TeraDeepOcean";
        /// <summary>
        /// 部署预制体没有填写 IconPath 时使用的默认图标。
        ///
        /// 这里使用 %ModDir:TeraDeepOcean%，而不是普通的 %ModDir%。
        /// 因为注册单位的可能是其他模组，但默认图标属于我们自己的模组。
        /// </summary>
        public const string DefaultIconPath = "%ModDir:TeraDeepOcean%/Content/UI/泰拉logo.png";
    }
}

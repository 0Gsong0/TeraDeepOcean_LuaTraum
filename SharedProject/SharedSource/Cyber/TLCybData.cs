using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 义体种类
    /// </summary>
    public enum TLCybCategory
    {
        Civilian,
        Industrial,
        Military
    }
    /// <summary>
    /// 义体实例数据
    /// </summary>
    public class TLCybData
    {
        public string ItemId { get; }
        public string InstallAffId { get; }
        public TLCybCategory Category { get; }
        public HashSet<LimbType> AllowedLimbs { get; }

        //伤害倍率：越低越耐打
        public float StructuralDamageMultiplier { get; }
        public float ServoDamageMultiplier { get; }
        public float CircuitDamageMultiplier { get; }
        public float SystemDamageMultiplier { get; }

        public TLCybData(string itemId,string installId,TLCybCategory category,IEnumerable<LimbType> allowLimbTypes,float structuralDamageMultiplier,float servoDamageMultiplier,float circuitDamageMultiplier,float systemDamageMultiplier)
        {
            ItemId = itemId;
            InstallAffId = installId;
            Category = category;
            AllowedLimbs = allowLimbTypes.ToHashSet();
            StructuralDamageMultiplier = structuralDamageMultiplier;
            ServoDamageMultiplier = servoDamageMultiplier;
            CircuitDamageMultiplier = circuitDamageMultiplier;
            SystemDamageMultiplier = systemDamageMultiplier;
        }
    }
    /// <summary>
    /// 目前的义体表单
    /// </summary>
    public static class TLCybDataForm
    {
        public static TLCybData[] CybDatas =
        {
            new(
                "TLCyb_CivilianArm",
                "TLCyb_CivilianArm_Init",
                TLCybCategory.Civilian,
                new[] { LimbType.LeftArm, LimbType.RightArm },
                structuralDamageMultiplier: 1.00f,
                servoDamageMultiplier: 1.00f,
                circuitDamageMultiplier: 1.00f,
                systemDamageMultiplier: 0.85f),
            new(
                "TLCyb_CivilianLeg",
                "TLCyb_CivilianLeg_Init",
                TLCybCategory.Civilian,
                new[] { LimbType.LeftLeg, LimbType.RightLeg },
                structuralDamageMultiplier: 1.00f,
                servoDamageMultiplier: 1.00f,
                circuitDamageMultiplier: 1.00f,
                systemDamageMultiplier: 0.85f),
        };
        public static TLCybData? FindByItemId(string id)
        {
            return CybDatas.FirstOrDefault(d => d.ItemId == id);
        }
        public static TLCybData? FindByInstallAffId(string id)
        {
            return CybDatas.FirstOrDefault(d => d.InstallAffId == id);
        }
    }
}

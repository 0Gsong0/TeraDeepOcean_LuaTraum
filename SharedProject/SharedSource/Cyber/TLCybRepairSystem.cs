using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public class TLCybRepairData
    {
        public string ItemId { get; }
        public string TargetAffId { get; }
        public float Min { get; }
        public float Max { get; }
        public bool UseCondition;
        public TLCybRepairData(string itemId, string targetAffId, float min, float max,bool useCondition = false)
        {
            ItemId = itemId;
            TargetAffId = targetAffId;
            Min = min;
            Max = max;
            UseCondition = useCondition;
        }
    }
    public static class TLCybRepairSystem
    {
        public static readonly TLCybRepairData[] RepairDataForm =
        {
            new("steel", "TLCyb_StructuralDamage", 5f, 10f, true),
            new("screwdriver", "TLCyb_ServoDamage", 8f, 15f, false),
            new("fpgacircuit", "TLCyb_CircuitDamage", 5f, 15f, true),
            new("TLCyb_Neuroleptic", "TLCyb_SystemDamage", 3f, 8f, true),
        };
        private static Random random = new();
        public static void Init(Harmony harmony)
        {
            var applyTreatment = AccessTools.Method(typeof(Item), nameof(Item.ApplyTreatment));
            if (applyTreatment != null)
            {
                harmony.Patch(applyTreatment, postfix: new HarmonyMethod(typeof(TLCybRepairSystem), nameof(TLCybRepairSystem.OnApplyTreatment)));
            }
        }
        private static void OnApplyTreatment(Item __instance, Character user, Character character, Limb targetLimb)
        {
            if (__instance == null || user == null || character == null || targetLimb == null) return;
            TryRepair(__instance, user, character, targetLimb);
        }
        private static void TryRepair(Item item, Character user, Character target, Limb limb)
        {
            string itemId = item.Prefab.Identifier.Value ?? "";
            TLCybRepairData? data = RepairDataForm.FirstOrDefault(d => d.ItemId == itemId);
            if (data == null) return;

            if (!TLCybState.HasAffOnLimb(target, limb, data.TargetAffId)) return;

            float source = TLCybState.GetAffStrengthOnLimb(target, limb, data.TargetAffId);
            if (source < 0.1f) return;

            float baseFix = Lerp(data.Min, data.Max, (float)random.NextDouble());
            float mechanical = target.GetSkillLevel("mechanical".ToIdentifier());
            float medical = target.GetSkillLevel("medical".ToIdentifier());

            float addFix = mechanical * 0.1f;
            if(data.TargetAffId == "TLCyb_SystemDamage")
            {
                addFix += medical * 0.15f;
            }

            TLCybState.AddAffOnLimb(target, limb, data.TargetAffId, -(baseFix + addFix));
            if (data.UseCondition)
            {
                item.Condition -= (baseFix + addFix) * 0.35f + Lerp(baseFix, baseFix + addFix, (float)random.NextDouble());
            }
        }
        private static float Lerp(float min, float max, float random)
        {
            return min + (max - min) * random;
        }
    }
}

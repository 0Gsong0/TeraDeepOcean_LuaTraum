using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public static class TLCybInstallSystem
    {
        public static void Init(Harmony harmony)
        {
            var applyTreatment = AccessTools.Method(typeof(Item), nameof(Item.ApplyTreatment));
            if(applyTreatment != null)
            {
                harmony.Patch(applyTreatment, postfix: new HarmonyMethod(typeof(TLCybInstallSystem), nameof(TLCybInstallSystem.OnApplyTreatment)));
            }
        }
        private static void OnApplyTreatment(Item __instance, Character user, Character character, Limb targetLimb)
        {
            if (__instance == null || user == null || character == null || targetLimb == null) return;
            TryInstallCyb(__instance, user, character, targetLimb);
        }
        private static void TryInstallCyb(Item item,Character user,Character target,Limb limb)
        {
            string itemId = item.Prefab.Identifier.Value ?? "";
            TLCybData? data = TLCybDataForm.FindByItemId(itemId);
            if (data == null) return;

            LimbType limbType = TLCybState.NormalizeLimbType(limb.type);
            if (!data.AllowedLimbs.Contains(limbType)) return;
            if (TLCybState.HasAnyCyb(target, limbType)) return;
            Limb? targetLimb = TLCybState.GetLimbByType(target, limbType);
            if (targetLimb == null) return;
            TLCybState.InstallCybLimb(target, targetLimb, data);
            Entity.Spawner.AddItemToRemoveQueue(item);
        }
    }
}

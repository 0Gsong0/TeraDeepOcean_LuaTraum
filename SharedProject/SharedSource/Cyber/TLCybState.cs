using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 义体状态写入与查询系统
    /// </summary>
    public static class TLCybState
    {
        public static LimbType NormalizeLimbType(LimbType limbType)
        {
            return limbType switch
            {
                LimbType.LeftForearm or LimbType.LeftHand => LimbType.LeftArm,
                LimbType.RightForearm or LimbType.RightHand => LimbType.RightArm,
                LimbType.LeftFoot or LimbType.LeftThigh => LimbType.LeftLeg,
                LimbType.RightFoot or LimbType.RightThigh => LimbType.RightLeg,
                _ => limbType
            };
        }
        public static Limb? GetLimbByType(Character character,LimbType limbType)
        {
            if (character?.AnimController?.Limbs == null) return null;
            limbType = NormalizeLimbType(limbType);
            return character.AnimController.Limbs.FirstOrDefault(a => NormalizeLimbType(a.type) == limbType);
        }
        public static TLCybData? GetInstallTLCybData(Character character, LimbType limbType)
        {
            Limb? limb = GetLimbByType(character, limbType);
            if (limb == null) return null;

            limbType = NormalizeLimbType(limbType);
            foreach (TLCybData data in TLCybDataForm.CybDatas)
            {
                if (!data.AllowedLimbs.Contains(limbType)) continue;
                if (HasAffOnLimb(character, limb, data.InstallAffId))
                {
                    return data;
                }
            }
            return null;
        }
        public static bool HasTLCyb(Character character,LimbType limbType)
        {
            return GetInstallTLCybData(character, limbType) != null;
        }
        public static bool HasNTCyb(Character character, LimbType limbType)
        {
            Limb? limb = GetLimbByType(character, limbType);
            if (limb == null) return false;
            limbType = NormalizeLimbType(limbType);
            if (HasAffOnLimb(character, limb, "ntc_cyberlimb")) { return true; }
            if (HasAffOnLimb(character, limb, "ntc_cyberarm")) { return true; }
            if (HasAffOnLimb(character, limb, "ntc_cyberleg")) { return true; }

            if (limbType == LimbType.Head && HasAffOnLimb(character, limb, "ntc_cyberbrain"))
            {
                return true;
            }

            return false;
        }
        public static bool HasNT()
        {
            AfflictionPrefab.Prefabs.TryGet("traumaticshock".ToIdentifier(), out AfflictionPrefab? aff);
            return aff != null;
        }
        public static bool HasAnyCyb(Character character,LimbType limbType)
        {
            return HasTLCyb(character, limbType) || HasNTCyb(character, limbType);
        }
        public static bool HasLycoris(Character character)
        {
            Limb? limb = GetLimbByType(character, LimbType.Head);
            return limb != null && HasAffOnLimb(character, limb, "TLCyb_LycorisChip_Init");
        }
        public static bool HasAffOnLimb(Character character,Limb? limb,string affId,float min = 0.1f)
        {
            if (character == null || limb == null) return false;
            return character.CharacterHealth.GetAffliction(affId.ToIdentifier(), limb)?.Strength >= min;
        }
        public static float GetAffStrengthOnLimb(Character character,Limb limb,string affId)
        {
            if (character?.CharacterHealth == null || limb == null) return 0f;
            return character.CharacterHealth.GetAffliction(affId.ToIdentifier(), limb)?.Strength ?? 0f;
        }
        public static void AddAffOnLimb(Character character,Limb limb,string affId,float amount)
        {
            if (character?.CharacterHealth == null || limb == null) { return; }
            if (!AfflictionPrefab.Prefabs.TryGet(affId.ToIdentifier(), out AfflictionPrefab? prefab)) return;
            character.CharacterHealth.ApplyAffliction(limb, prefab.Instantiate(amount));
        }
        public static void InstallCybLimb(Character character,Limb limb,TLCybData data)
        {
            if (character == null || limb == null) { return; }
            if (HasAnyCyb(character, limb.type)) { return; }
            if (!data.AllowedLimbs.Contains(NormalizeLimbType(limb.type))) return;

            AddAffOnLimb(character, limb, data.InstallAffId, 1000f);
        }
    }
}

using HarmonyLib;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public class TLCybDamageConvertData
    {
        public string SourceAff { get; }
        public string TargetAff { get; }
        public float Weight { get; }
        public TLCybDamageConvertData(string source,string target,float weight)
        {
            SourceAff = source;
            TargetAff = target;
            Weight = weight;
        }
    }
    public static class TLCybDamageSystem
    {
        public static TLCybDamageConvertData[] DamageConvertDataForm =
        {
            new("bleeding", "TLCyb_ServoDamage", 0.10f),
            new("gunshotwound", "TLCyb_StructuralDamage", 0.10f),
            new("gunshotwound", "TLCyb_ServoDamage", 0.20f),
            new("blunttrauma", "TLCyb_StructuralDamage", 0.10f),
            new("lacerations", "TLCyb_StructuralDamage", 0.10f),
            new("burn", "TLCyb_CircuitDamage", 0.15f),
            new("explosiondamage", "TLCyb_CircuitDamage", 0.50f),
            new("internaldamage", "TLCyb_SystemDamage", 0.05f),
        };
        private static float updateTimer;
        private static float lateUpdateTimer;
        
        public static void Init(Harmony harmony)
        {
            var update = AccessTools.Method(typeof(GameSession), nameof(GameSession.Update));
            var applyDamage = AccessTools.Method(typeof(CharacterHealth), nameof(CharacterHealth.ApplyDamage));
            if (update != null)
            {
                harmony.Patch(update, postfix: new HarmonyMethod(typeof(TLCybDamageSystem), nameof(TLCybDamageSystem.OnUpdate)));
                DebugConsole.NewMessage("TLCybDamageSystem挂载完成");
            }
            if (applyDamage != null)
            {
                harmony.Patch(applyDamage, prefix: new HarmonyMethod(typeof(TLCybDamageSystem), nameof(TLCybDamageSystem.OnApplyDamage)));
                DebugConsole.NewMessage("TLCybDamageSystem挂载完成");
            }
        }
        private static void OnUpdate(float deltaTime)
        {
            if (GameMain.NetworkMember is { IsClient: true }) return;
            Update(deltaTime);
            LateUpdate(deltaTime);
        }
        private static bool OnApplyDamage(CharacterHealth __instance, Limb hitLimb, AttackResult attackResult)
        {
            return TryConvertDamage(__instance, hitLimb, attackResult);
        }
        private static void Update(float deltaTime)
        {
            updateTimer += deltaTime;
            if (updateTimer < TLConfig.UpdateInterval) return;
            updateTimer = 0;
        }
        private static void LateUpdate(float deltaTime)
        {
            lateUpdateTimer += deltaTime;
            if (lateUpdateTimer < TLConfig.LateUpdateInterval) return;
            lateUpdateTimer = 0;

            foreach (var character in Character.CharacterList)
            {
                if (character == null || character.Removed || character.IsDead || !character.IsHuman) { continue; }
                LateUpdateCharacter(character);
            }
        }
        private static void LateUpdateCharacter(Character character)
        {
            if (!TLCybState.HasLycoris(character)) { return; }
            foreach (Limb limb in character.AnimController.Limbs)
            {
                LimbType limbType = TLCybState.NormalizeLimbType(limb.type);
                if (!TLCybState.HasTLCyb(character, limbType)) return;
                foreach(var group in DamageConvertDataForm.GroupBy(d => d.SourceAff))
                {
                    string affId = group.Key;
                    float sourceAmout = TLCybState.GetAffStrengthOnLimb(character, limb, affId);
                    if (sourceAmout < 0.1f) return;
                    foreach (TLCybDamageConvertData data in group)
                    {
                        float multiplier = GetDamageMultiplier(TLCybState.GetInstallTLCybData(character,limbType), data.TargetAff);
                        float amount = sourceAmout * data.Weight * multiplier * TLConfig.CybDamageMultiplier;
                        TLCybState.AddAffOnLimb(character, limb, data.TargetAff, amount);
                    }
                    TLCybState.AddAffOnLimb(character, limb, affId, -1000f);
                }
            }
        }
        private static bool TryConvertDamage(CharacterHealth __instance, Limb hitLimb, AttackResult attackResult)
        {
            if (GameMain.NetworkMember is { IsClient: true }) return true;

            CharacterHealth health = __instance;
            Character character = health.Character;
            if (character == null || hitLimb == null || character.IsDead || !character.IsHuman) return true;

            LimbType limbType = TLCybState.NormalizeLimbType(hitLimb.type);
            TLCybData? cybData = TLCybState.GetInstallTLCybData(character, limbType);
            if (cybData == null) return true;
            if (attackResult.Afflictions == null || attackResult.Afflictions.Count == 0) return true;

            bool anyConverted = false;
            foreach (Affliction aff in attackResult.Afflictions)
            {
                string affId = aff.Prefab.Identifier.Value;
                foreach (TLCybDamageConvertData data in DamageConvertDataForm)
                {
                    if (data.SourceAff != affId) continue;

                    float multiplier = GetDamageMultiplier(cybData,data.TargetAff);
                    float amount = aff.Strength * data.Weight * multiplier * TLConfig.CybDamageMultiplier;
                    TLCybState.AddAffOnLimb(character, hitLimb, data.TargetAff, amount);
                    anyConverted = true;
                }
            }
            if (anyConverted) return false;
            return true;
        }
        private static float GetDamageMultiplier(TLCybData? data,string targetAff)
        {
            if (data == null) return 1.0f;
            return targetAff switch
            {
                "TLCyb_StructuralDamage" => data.StructuralDamageMultiplier,
                "TLCyb_ServoDamage" => data.ServoDamageMultiplier,
                "TLCyb_CircuitDamage" => data.CircuitDamageMultiplier,
                "TLCyb_SystemDamage" => data.SystemDamageMultiplier,
                _ => 1.0f
            };
        }
    }
}

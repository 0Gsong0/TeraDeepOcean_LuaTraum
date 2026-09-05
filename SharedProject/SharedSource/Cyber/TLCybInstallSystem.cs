using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using static Microsoft.Xna.Framework.Graphics.VertexDeclaration;

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
            var roundStart = AccessTools.Method(typeof(GameSession), nameof(GameSession.StartRound), new[]
            {
                typeof(LevelData),
                typeof(bool),
                typeof(SubmarineInfo),
                typeof(SubmarineInfo),
            });
            if(roundStart != null)
            {
                harmony.Patch(roundStart, postfix: new HarmonyMethod(typeof(TLCybInstallSystem), nameof(TLCybInstallSystem.OnRoundStart)));
            }
        }
        private static HashSet<(ushort CharacterId,string NoticeKey)>? playedInstallNotices = new();
        private static void OnApplyTreatment(Item __instance, Character user, Character character, Limb targetLimb)
        {
            if (__instance == null || user == null || character == null || targetLimb == null) return;
            TryInstallCyb(__instance, user, character, targetLimb);
        }
        private static void OnRoundStart()
        {
            playedInstallNotices.Clear();
        }
        private static void TryInstallCyb(Item item,Character user,Character target,Limb limb)
        {
            string itemId = item.Prefab.Identifier.Value ?? "";
            LimbType limbType = TLCybState.NormalizeLimbType(limb.type);
            if (TLCybState.HasNT())
            {
                if (TLCybState.HasAffOnLimb(target, TLCybState.GetLimbByType(target,limbType), "retractedskin"))
                {
                    if (itemId == "TLCyb_LycorisChip")
                    {
                        TryInstallLycoris(target, item);
                    }
                    if (TLCybState.HasLycoris(target))
                    {
                        InstallCyb(target, itemId, limbType, item);
                    }
                }
            }
            else
            {
                if (itemId == "TLCyb_Tool")
                {
                    limb = TLCybState.GetLimbByType(target, limbType);
                    if (limb == null) return;
                    TLCybState.AddAffOnLimb(target, limb, "TLCyb_Tool_Init", 1000);
                    Entity.Spawner.AddItemToRemoveQueue(item);
                }
                if (TLCybState.HasAffOnLimb(target, limb, "TLCyb_Tool_Init"))
                {
                    if (itemId == "TLCyb_LycorisChip")
                    {
                        TryInstallLycoris(target, item);
                    }
                    if (TLCybState.HasLycoris(target))
                    {
                        InstallCyb(target, itemId, limbType, item);
                    }
                }
            }

        }
        private static void InstallCyb(Character target,string itemId,LimbType limbType,Item item)
        {
            TLCybData? data = TLCybDataForm.FindByItemId(itemId);
            if (data == null) return;

            if (!data.AllowedLimbs.Contains(limbType)) return;
            if (TLCybState.HasAnyCyb(target, limbType)) return;
            Limb? targetLimb = TLCybState.GetLimbByType(target, limbType);
            if (targetLimb == null) return;
            TLCybState.InstallCybLimb(target, targetLimb, data);
            var key = (target.ID, data.ItemId);
            if (playedInstallNotices.Add(key))
            {
                if (!ItemPrefab.Prefabs.TryGet(data.InstallSound.ToIdentifier(), out ItemPrefab? result))
                {
                    DebugConsole.NewMessage("找不到物品 prefab:" + data.InstallSound, Color.Red);
                    return;
                }
                Entity.Spawner.AddItemToSpawnQueue(result, target.WorldPosition);
#if CLIENT
            TLCybNotice.ShowInstallMessage(target, data);
#endif
            }
            Entity.Spawner.AddItemToRemoveQueue(item);
            TLCybState.AddAffOnLimb(target, targetLimb, "TLCyb_Tool_Init", -1000);
        }
        private static void TryInstallLycoris(Character target,Item item)
        {
            if (TLCybState.HasLycoris(target)) return;
            Limb? head = TLCybState.GetLimbByType(target, LimbType.Head);
            if (head == null) return;
            TLCybState.AddAffOnLimb(target, head, "TLCyb_LycorisChip_Init", 1000);
            Entity.Spawner.AddItemToRemoveQueue(item);
            TLCybState.AddAffOnLimb(target, head, "TLCyb_Tool_Init", -1000);
            var key = (target.ID, "TLCyb_LycorisChip_Sound");
            if (playedInstallNotices.Add(key))
            {
                if (!ItemPrefab.Prefabs.TryGet("TLCyb_LycorisChip_Sound".ToIdentifier(), out ItemPrefab? result))
                {
                    DebugConsole.NewMessage("找不到物品 prefab:TLCyb_LycorisChip_Sound", Color.Red);
                    return;
                }
                Entity.Spawner.AddItemToSpawnQueue(result, target.WorldPosition);
#if CLIENT
            TLCybNotice.ShowInstallMessage(target, "afflictionname.TLCyb_LycorisChip_Init", "TLCyb_LycorisChip_init", "GUITLCyb_LycorisChip");
#endif
            }
        }
    }
}

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
            if (applyTreatment != null)
            {
                harmony.Patch(applyTreatment, postfix: new HarmonyMethod(typeof(TLCybInstallSystem), nameof(TLCybInstallSystem.OnApplyTreatment)));
            }
            var roundStart = AccessTools.Method(typeof(GameSession), nameof(GameSession.StartRound), new[]
            {
                typeof(LevelData),
                typeof(bool),
            });
            if (roundStart != null)
            {
                harmony.Patch(roundStart, postfix: new HarmonyMethod(typeof(TLCybInstallSystem), nameof(TLCybInstallSystem.OnRoundStart)));
            }
        }
        private static readonly HashSet<(ushort CharacterId, string NoticeKey)> playedInstallNotices = new();
        internal static void Reset() => playedInstallNotices.Clear();
        private static void OnApplyTreatment(Item __instance, Character user, Character character, Limb targetLimb)
        {
            if (GameMain.NetworkMember is { IsClient: true }) return;
            if (__instance == null || user == null || character == null || targetLimb == null || Entity.Spawner == null) return;
            TryInstallCyb(__instance, user, character, targetLimb);
        }
        private static void OnRoundStart()
        {
            Reset();
            TLCybDamageSystem.Reset();
        }
        private static void TryInstallCyb(Item item, Character user, Character target, Limb limb)
        {
            string itemId = item.Prefab.Identifier.Value ?? "";
            LimbType limbType = TLCybState.NormalizeLimbType(limb.type);
            if (TLCybState.HasNT())
            {
                if (itemId == "TLCyb_Tool")
                {
                    TLCybNoticeBridge.ShowConversation(user, "TLCyb_Tool.HasNT", "TL_Error");
                    TLCybGameApi.RemoveItem(item);
                }
                if (TLCybState.HasAffOnLimb(target, TLCybState.GetLimbByType(target, limbType), "retractedskin"))
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
                    Limb? preparedLimb = TLCybState.GetLimbByType(target, limbType);
                    if (preparedLimb == null) return;
                    limb = preparedLimb;
                    TLCybState.AddAffOnLimb(target, limb, "TLCyb_Tool_Init", 1000);
                    TLCybGameApi.RemoveItem(item);
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
        private static void InstallCyb(Character target, string itemId, LimbType limbType, Item item)
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
                    Plugin.DebugConsole.NewMessage("找不到物品 prefab:" + data.InstallSound, Color.Red);
                }
                else
                {
                    Entity.Spawner.AddItemToSpawnQueue(result, target.WorldPosition);
                }
                string header = string.IsNullOrWhiteSpace(data.InstallNoticeHeaderTag) ? data.ItemId : data.InstallNoticeHeaderTag;
                string text = string.IsNullOrWhiteSpace(data.InstallNoticeTextTag) ? data.InstallAffId : data.InstallNoticeTextTag;
                TLCybNoticeBridge.ShowInstall(target, header, text, eventSpriteId: data.InstallNoticeBackImageTag);
            }
            TLCybGameApi.RemoveItem(item);
            TLCybState.AddAffOnLimb(target, targetLimb, "TLCyb_Tool_Init", -1000);
        }
        private static void TryInstallLycoris(Character target, Item item)
        {
            if (TLCybState.HasLycoris(target)) return;
            Limb? head = TLCybState.GetLimbByType(target, LimbType.Head);
            if (head == null) return;
            TLCybState.AddAffOnLimb(target, head, "TLCyb_LycorisChip_Init", 1000);
            TLCybGameApi.RemoveItem(item);
            TLCybState.AddAffOnLimb(target, head, "TLCyb_Tool_Init", -1000);
            var key = (target.ID, "TLCyb_LycorisChip_Sound");
            if (playedInstallNotices.Add(key))
            {
                if (!ItemPrefab.Prefabs.TryGet("TLCyb_LycorisChip_Sound".ToIdentifier(), out ItemPrefab? result))
                {
                    Plugin.DebugConsole.NewMessage("找不到物品 prefab:TLCyb_LycorisChip_Sound", Color.Red);
                }
                else
                {
                    Entity.Spawner.AddItemToSpawnQueue(result, target.WorldPosition);
                }
                TLCybNoticeBridge.ShowInstall(target, "afflictionname.TLCyb_LycorisChip_Init", "TLCyb_LycorisChip_init", iconStyle: "GUITLCyb_LycorisChip");
            }
        }
    }
}

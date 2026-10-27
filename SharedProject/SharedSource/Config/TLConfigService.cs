using Barotrauma.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace TeraDeepOcean
{
    //全局入口，方便其他系统读
    /// <summary>
    /// 从这里读取配置系统数据
    /// </summary>
    public static class TLConfig
    {
        public static int UpdateInterval => TLConfigService.UpdateInterval;
        public static int LateUpdateInterval => TLConfigService.LateUpdateInterval;
        public static bool DebugModel => TLConfigService.DebugModel;
        public static float CybDamageMultiplier => TLConfigService.CybDamageMultiplier;

        public static IEnumerable All => TLConfigService.All;
    }
    //配置逻辑类
    /// <summary>
    /// 配置系统服务层
    /// </summary>
    public static class TLConfigService
    {
        public static TLConfigState Current { get; } = new();
        public static int UpdateInterval => Current.GetInt("TL_TLUpdateInterval");
        public static int LateUpdateInterval => Current.GetInt("TL_TLLateUpdateInterval");
        public static bool DebugModel => Current.GetBool("TL_TLDebugModel");

        public static float CybDamageMultiplier => Current.GetFloat("TL_CybDamageMultiplier");

        public static IEnumerable All => Current.GetAll();

        public static void LoadLocal()
        {
            TLConfigStorage.Load();
        }
        private static void SaveLocal()
        {
            TLConfigStorage.Save();
        }
        public static void ResetToDefaults()
        {
            Current.RestToDefaults();
        }
        public static void SaveOrSubmit()
        {
#if SERVER
            SaveLocal();
            TLConfigBridge.SendUpdateToClient(null, ToJson());
#elif CLIENT
            if (!GameMain.IsMultiplayer)
            {
                SaveLocal();
                return;
            }
            TLConfigBridge.SendUpdateToServer(ToJson());
#endif
        }
        public static string ToJson()
        {
            return JsonSerializer.Serialize(Current.GetAll());
        }
        // 先解析临时状态，全部有效后才提交，避免错误消息修改一半配置。
        public static bool TryApplyJson(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
                var next = new TLConfigState();
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    var entry = TLConfigData.Find(property.Name);
                    if (entry == null) continue;
                    var value = property.Value;
                    switch (entry.VauleType)
                    {
                        case TLConfigVauleType.Int:
                            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int integer)) return false;
                            next.SetInt(entry.Key, integer);
                            break;
                        case TLConfigVauleType.Float:
                            if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float number) || !float.IsFinite(number)) return false;
                            next.SetFloat(entry.Key, number);
                            break;
                        case TLConfigVauleType.Bool:
                            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False) return false;
                            next.SetBool(entry.Key, value.GetBoolean());
                            break;
                        case TLConfigVauleType.String:
                            if (value.ValueKind != JsonValueKind.String) return false;
                            next.SetString(entry.Key, value.GetString() ?? string.Empty);
                            break;
                    }
                }
                Current.ReplaceWith(next);
                return true;
            }
            catch (JsonException) { return false; }
            catch (ArgumentException) { return false; }
        }

        public static void ApplyJson(string json)
        {
            if (!TryApplyJson(json))
                Plugin.DebugConsole.NewMessage("配置 JSON 无效，未应用更改。");
        }
        /// <summary>
        /// 客户端接受配置
        /// </summary>
        /// <param name="json"></param>
        public static void ClientReceiveConfig(string json)
        {
            if (!TryApplyJson(json)) return;
#if CLIENT
            TLConfigMenuEntry.OnConfigReceived();
            TLConfigMenu.OnConfigReceived();
#endif
        }
        /// <summary>
        /// 客户端请求配置时，服务器处理请求
        /// </summary>
        /// <param name="sender"></param>
        public static void ServerHandleConfigRequest(Client sender)
        {
#if SERVER
            TLConfigBridge.SendUpdateToClient(sender, ToJson());
#endif
        }
        /// <summary>
        /// 服务器接受配置
        /// </summary>
        /// <param name="json"></param>
        /// <param name="sender"></param>
        public static void ServerHandleConfigUpdate(string json, Client sender)
        {
#if SERVER
            if (!sender.HasPermission(ClientPermissions.ManageCampaign))
            {
                TLConfigBridge.SendUpdateToClient(sender, ToJson());
                return;
            }
            if (!TryApplyJson(json))
            {
                TLConfigBridge.SendUpdateToClient(sender, ToJson());
                return;
            }
            SaveLocal();
            TLConfigBridge.SendUpdateToClient(null, ToJson());
#endif
        }
    }
}


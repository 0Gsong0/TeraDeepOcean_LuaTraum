using Barotrauma.Networking;
using System;
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

        //以后 LuaBridge 来赋值：C# 生成 json，Lua 只负责发出去。
        public static Action<string> SendToServer;
        public static Action<Client?, string> SendToClient;

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
            if (!GameMain.IsMultiplayer)
            {
                SaveLocal();
                return;
            }
            SendToServer?.Invoke(ToJson());
            TLConfigBridge.SendUpdateToServer(ToJson());
        }
        public static string ToJson()
        {
            return JsonSerializer.Serialize(Current.GetAll());
        }
        public static void ApplyJson(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if(document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                TLConfigDataEntry? entry = TLConfigData.Find(property.Name);
                if (entry == null) continue;

                JsonElement value = property.Value;
                switch (entry.VauleType)
                {
                    case TLConfigVauleType.Int:
                        if(value.TryGetInt32(out int intNum))
                        {
                            Current.SetInt(entry.Key, intNum);
                        }
                        break;
                    case TLConfigVauleType.Float:
                        if(value.TryGetSingle(out float floatNum))
                        {
                            Current.SetFloat(entry.Key, floatNum);
                        }
                        break;
                    case TLConfigVauleType.Bool:
                        if(value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                        {
                            Current.SetBool(entry.Key, value.GetBoolean());
                        }
                        break;
                }
            }
        }
        /// <summary>
        /// 客户端接受配置
        /// </summary>
        /// <param name="json"></param>
        public static void ClientReceiveConfig(string json)
        {
            Current.RestToDefaults();
            ApplyJson(json);
#if CLIENT
            TLConfigMenu.OnConfigReceived();
#endif
        }
        /// <summary>
        /// 客户端请求配置时，服务器处理请求
        /// </summary>
        /// <param name="sender"></param>
        public static void ServerHandleConfigRequest(Client sender)
        {
            LoadLocal();
            SendToClient?.Invoke(sender, ToJson());
#if SERVER
            TLConfigBridge.SendUpdateToClient(sender, ToJson());
#endif
        }
        /// <summary>
        /// 服务器接受配置
        /// </summary>
        /// <param name="json"></param>
        /// <param name="sender"></param>
        public static void ServerHandleConfigUpdate(string json,Client sender)
        {
            if(GameMain.NetworkMember == null || !sender.HasPermission(ClientPermissions.ManageCampaign))
            {
                return;
            }
            Current.RestToDefaults();
            ApplyJson(json);
            SaveLocal();

            // sender 为 null 时表示广播给所有客户端，具体发送由 LuaBridge 实现。
            SendToClient?.Invoke(null, ToJson());
#if SERVER
            TLConfigBridge.SendUpdateToClient(null, ToJson());
#endif
        }
    }
}

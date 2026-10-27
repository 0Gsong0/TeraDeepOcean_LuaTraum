using Barotrauma.Networking;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public static partial class TLConfigBridge
    {
        public static string GetJson() => TLConfigService.ToJson();
        public static void ApplyJson(string json) => TLConfigService.ApplyJson(json);
        public static void LoadLocal() => TLConfigStorage.Load();
        public static void SaveLocal() => TLConfigStorage.Save();
        public static void RegisterHandlers()
        {
#if SERVER
            // 服务器监听：客户端请求配置。
            Plugin.Network.RegisterHandler<TLNetworkHeader, TLConfigRequestMessage>(
                TLNetworkHeader.ConfigRequest,
                OnConfigRequest);
            // 服务器监听：客户端提交配置修改。
            Plugin.Network.RegisterHandler<TLNetworkHeader, TLConfigJsonMessage>(
                TLNetworkHeader.ConfigSubmit,
                OnConfigSubmit);
#elif CLIENT
            // 客户端监听：服务器下发配置。
            Plugin.Network.RegisterHandler<TLNetworkHeader, TLConfigJsonMessage>(
                TLNetworkHeader.ConfigSnapshot, 
                OnConfigSnapshot);
#endif
        }
        /// <summary>
        /// 客户端：请求服务器当前配置。
        /// </summary>
        public static void ClientRequestUpdate()
        {
#if CLIENT
            if (GameMain.Client is null) return;
            Plugin.Network.Send(TLNetworkHeader.ConfigRequest, new TLConfigRequestMessage());
#endif
        }
        /// <summary>
        /// 客户端：提交配置修改。
        /// </summary>
        /// <param name="json"></param>
        public static void SendUpdateToServer(string json)
        {
#if CLIENT
            if(GameMain.Client is null) return;
            Plugin.Network.Send(TLNetworkHeader.ConfigSubmit, new TLConfigJsonMessage(json));
#endif
        }
        /// <summary>
        /// 服务器：发送配置。
        /// </summary>
        /// <param name="client">null：广播给所有客户端</param>
        /// <param name="json"></param>
        public static void SendUpdateToClient(Client? client, string json)
        {
#if SERVER
            var message = new TLConfigJsonMessage(json);
            if (client is null)
            {
                Plugin.Network.Broadcast(TLNetworkHeader.ConfigSnapshot, message);
            }
            else
            {
                Plugin.Network.SendToClient(client, TLNetworkHeader.ConfigSnapshot, message);
            }
#endif
        }
#if SERVER
        private static void OnConfigRequest(TLConfigRequestMessage data, Client sender)
        {
            TLConfigService.ServerHandleConfigRequest(sender);
        }
        private static void OnConfigSubmit(TLConfigJsonMessage data, Client sender)
        {
            TLConfigService.ServerHandleConfigUpdate(data.json, sender);
        }
#elif CLIENT
        private static void OnConfigSnapshot(TLConfigJsonMessage data)
        {
            TLConfigService.ClientReceiveConfig(data.json);
        }
#endif
    }
}

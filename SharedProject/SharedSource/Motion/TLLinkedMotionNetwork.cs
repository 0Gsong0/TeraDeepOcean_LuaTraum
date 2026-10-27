using Barotrauma.Networking;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    [NetworkSerialize]
    internal readonly record struct TLMotionJsonMessage(string Json) : INetSerializableStruct;
    public static partial class TLLinkedMotionSystem
    {
        public static void RegisterHandlers()
        {
#if CLIENT
            Plugin.Network.RegisterHandler<TLNetworkHeader, TLMotionJsonMessage>(
                TLNetworkHeader.MotionServerSendSnapshot,
                OnMotionServerSendSnapshot);
#endif
        }
#if SERVER
        private static void SendSnapShotToClient(Client? client,TLNetworkHeader header,string json)
        {

            if (GameMain.NetworkMember is not { IsServer: true }) return;
            var message = new TLMotionJsonMessage(json);
            if(client == null)
            {
                if(header==TLNetworkHeader.MotionServerSendSnapshot)
                    Plugin.Network.Broadcast(header, message);
            }
            else if (client.Connection != null)
            {
                Plugin.Network.SendToClient(client, header, message);
            }
        }
#else
        private static void OnMotionServerSendSnapshot(TLMotionJsonMessage data)
        {
            ClientReceiveSnapshot(data.Json);
        }
#endif
    }
}

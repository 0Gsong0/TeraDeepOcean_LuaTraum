using Barotrauma.Networking;
using MoonSharp.Interpreter;
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
        //TLNet.Config.ClientRequestUpdate
        public static void HandleServerConfigRequest(Client sender) => TLConfigService.ServerHandleConfigRequest(sender);
        //TLNet.Config.SendUpdateToServer
        public static void HandleServerConfigUpdate(string json, Client sender) => TLConfigService.ServerHandleConfigUpdate(json, sender);
        //TLNet.Config.ClientRequestUpdate
        public static void ClientRequestUpdate()
        {
            if (GameMain.NetworkMember == null) return;
            CallLua("TLConfigNet", "ClientRequestUpdate");
        }
        //TLNet.Config.ClientReceiveConfig
        public static void ClientReceiveConfig(string json) => TLConfigService.ClientReceiveConfig(json);
        //TLNet.Config.SendUpdateToServer
        public static void SendUpdateToServer(string json)
        {
            if (GameMain.NetworkMember == null) return;
            CallLua("TLConfigNet", "SendUpdateToServer", json);
        }
        //TLConfigNet.SendUpdateToClient
        public static void SendUpdateToClient(Client? client, string json)
        {
            CallLua("TLConfigNet", "SendUpdateToClient", client, json);
        }
        private static void CallLua(string tableName, string functionName, params object[] args)
        {
            Table table = LuaCsSetup.Instance.Lua.Globals.Get(tableName).Table;
            DynValue function = table.Get(functionName);
            if (table == null || function == null) return;
            LuaCsSetup.Instance.Lua.Call(function, args);
        }
    }
}

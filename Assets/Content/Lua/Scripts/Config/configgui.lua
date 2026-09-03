LuaUserData.RegisterType("TeraDeepOcean.TLConfigBridge")
Lib_Config = LuaUserData.CreateStatic("TeraDeepOcean.TLConfigBridge", false)
TLConfigNet = TLConfigNet or {}

if CLIENT then
    local easySettings = dofile(TL.Path .. "/Lua/Scripts/Config/easysettings.lua")
    easySettings.AddMenu("TeraDeepOcean-泰拉渊洋", function(parent)
        local ui = easySettings.BasicFrame(parent, "TeraDeepOcean", Vector2(0.62, 0.72))
        Lib_Config.OpenTLConfigMenu(ui.Frame, ui.List, ui.ButtonRow)
    end)
end

if CLIENT then
    --收到服务端的配置
    Networking.Receive("TLNet.Config.ClientReceiveConfig", function(msg)
        Lib_Config.ClientReceiveConfig(msg.ReadString())
    end)
    --主动请求服务端
    function TLConfigNet.ClientRequestUpdate()
        local msg = Networking.Start("TLNet.Config.ClientRequestUpdate")
        Networking.Send(msg)
	end
    --发送配置到服务端
    function TLConfigNet.SendUpdateToServer(jsonText)
	    local msg = Networking.Start("TLNet.Config.SendUpdateToServer")
        msg.WriteString(jsonText)
        Networking.Send(msg)
    end
end

if SERVER then
    --收到客户端的配置请求
    Networking.Receive("TLNet.Config.ClientRequestUpdate", function(msg, sender)
        if sender == nil then return end
        Lib_Config.HandleServerConfigRequest(sender)
    end)
    --配置发给客户端
    function TLConfigNet.SendUpdateToClient(receiver, jsonText)
	    local msg = Networking.Start("TLNet.Config.ClientReceiveConfig")
        msg.WriteString(jsonText)
        if receiver ~= nil then
            Networking.Send(msg, receiver.Connection)
        else
            Networking.Send(msg)
        end
    end
    --收到客户端的配置更新
    Networking.Receive("TLNet.Config.SendUpdateToServer", function(msg, sender)
        if sender == nil then return end
        Lib_Config.HandleServerConfigUpdate(msg.ReadString(),sender)
    end)
end
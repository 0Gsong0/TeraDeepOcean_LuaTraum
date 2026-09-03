TLConfig = TLConfig or {}
TLConfig.Entries = {}
TLConfig.Values = {}

local configDirectoryPath = Game.SaveFolder .. "/ModConfigs"
local configFilePath = configDirectoryPath .. "/TeraDeepOcean.json"
TLConfig.Entries = {
    TL_header_System = {
        name = Lib.ReadXmlRichText("config.TL_header_System"),
        type = "category",
        description = Lib.ReadXmlRichText("config.des.TL_header_System")
    },
    TL_TLUpdateInterval = {
        name = Lib.ReadXmlRichText("config.TL_TLUpdateInterval"),
        type = "int",
        default = 2,
        range = {1, 5},
        description = Lib.ReadXmlRichText("config.des.TL_TLUpdateInterval")
    },
    TL_TLLateUpdateInterval = {
        name = Lib.ReadXmlRichText("config.TL_TLLateUpdateInterval"),
        type = "int",
        default = 5,
        range = {1, 10},
        description = Lib.ReadXmlRichText("config.des.TL_TLLateUpdateInterval")
    },
    TL_TLDebugModel = {
        name = Lib.ReadXmlRichText("config.TL_TLDebugModel"),
        type = "bool",
        default = false,
        description = Lib.ReadXmlRichText("config.des.TL_TLDebugModel")
    },
    TL_TLDeltaTime = {
        name = "默认帧间隔",
        type = "show",
        default = 2,
        range = {1, 10},
        description = "原生状态下的帧间隔，当你修改 正常函数更新间隔 时候会发生变动（此处仅做展示，不会发生改变）"
    },
    TL_header_cyb = {
        name = Lib.ReadXmlRichText("config.TL_header_cyb"),
        type = "category",
        description = Lib.ReadXmlRichText("config.des.TL_header_cyb")
    },
    TL_cybDamageMultiplier = {
        name = Lib.ReadXmlRichText("config.TL_cybDamageMultiplier"),
        type = "float",
        default = 1.5,
        range = {1, 5},
        description = Lib.ReadXmlRichText("config.des.TL_cybDamageMultiplier")
    },
    TL_TLCybTripChance = {
        name = Lib.ReadXmlRichText("config.TL_TLCybTripChance"),
        type = "float",
        default = 0.25,
        range = {0, 1},
        description = Lib.ReadXmlRichText("config.des.TL_TLCybTripChance")
    },
    TL_TLCybProtectFallNum = {
        name = Lib.ReadXmlRichText("config.TL_TLCybProtectFallNum"),
        type = "int",
        default = 5,
        range = {0, 20},
        description = Lib.ReadXmlRichText("config.des.TL_TLCybProtectFallNum")
    },
    TLCyb_LycorisSystemDamage_AutoIncrementThreshold = {
        name = Lib.ReadXmlRichText("config.TLCyb_LycorisSystemDamage_AutoIncrementThreshold"),
        type = "int",
        default = 80,
        range = {10, 100},
        description = Lib.ReadXmlRichText("config.des.TLCyb_LycorisSystemDamage_AutoIncrementThreshold")
    },
    TLCyb_SystemDamageCrawlThreshold = {
        name = Lib.ReadXmlRichText("config.TLCyb_SystemDamageCrawlThreshold"),
        type = "int",
        default = 60,
        range = {10, 100},
        description = Lib.ReadXmlRichText("config.des.TLCyb_SystemDamageCrawlThreshold"),
    },
    TLCyb_ServoDamageCrawlThreshold = {
        name = Lib.ReadXmlRichText("config.TLCyb_ServoDamageCrawlThreshold"),
        type = "int",
        default = 80,
        range = {10, 100},
        description = Lib.ReadXmlRichText("config.des.TLCyb_ServoDamageCrawlThreshold"),
    },
    TLCyb_totalDisableScoreoDamageCrawlThreshold = {
        name = Lib.ReadXmlRichText("config.TLCyb_totalDisableScoreoDamageCrawlThreshold"),
        type = "int",
        default = 150,
        range = {10, 300},
        description = Lib.ReadXmlRichText("config.des.TLCyb_totalDisableScoreoDamageCrawlThreshold"),
    },
}
--初始化默认值函数
function TLConfig.ResetToDefaults()
    TLConfig.Values = {}

    for key, entry in pairs(TLConfig.Entries) do
        if entry.default ~= nil then
            TLConfig.Values[key] = entry.default
        end
    end
end
function TLConfig.Get(key, default)
    local value = TLConfig.Values[key]
    if value ~= nil then
        return value
    end
    return default
end
function TLConfig.Set(key, value)
    if TLConfig.Entries[key] ~= nil then
        TLConfig.Values[key] = value
    end
end
--本地读取写入
function TLConfig.SaveConfig()
    File.CreateDirectory(configDirectoryPath)
    File.Write(configFilePath, json.serialize(TLConfig.Values))
end
function TLConfig.CSSaveConfig()
    local box
    if Game.IsSingleplayer then
        TLConfig.SaveConfig()
        box = GUI.MessageBox("保存成功", "配置已保存到本地。", { "确定" })
    else
        TLConfig.SendConfig()
        box = GUI.MessageBox("保存成功", "配置已提交至服务器。", { "确定" })
    end

    box.DrawOnTop = true
    box.Buttons[1].OnClicked = function()
        box.Close()
    end
end
function TLConfig.LoadConfig()
    TLConfig.ResetToDefaults()

    if not File.Exists(configFilePath) then
        return
    end

    local text = File.Read(configFilePath)
    if not text or text == "" then
        return
    end

    local data = json.parse(text)
    if not data then
        return
    end

    for key, value in pairs(data) do
        if TLConfig.Entries[key] ~= nil then
            TLConfig.Values[key] = value
        end
    end
end
--网络收发
function TLConfig.SendConfig(receiver)
    local msg = Networking.Start("TL.ConfigUpdate")
    msg.WriteString(json.serialize(TLConfig.Values))
    if TLConfig.Get("TL_TLDebugModel") then
        print("读取到配置文件内容："..json.serialize(TLConfig.Values))
    end
    if SERVER then
        Networking.Send(msg, receiver and receiver.Connection or nil)
    else
        Networking.Send(msg)
    end
end
function TLConfig.ReceiveConfig(msg)
    local data = json.parse(msg.ReadString())
    if not data then return end

    TLConfig.ResetToDefaults()

    for key, value in pairs(data) do
        if TLConfig.Entries[key] ~= nil then
            TLConfig.Values[key] = value
        end
    end
end

--CS侧桥
TLConfigBridge = TLConfigBridge or {}

function TLConfigBridge.GetFloat(key, defaultValue)
    local value = TLConfig.Get(key, defaultValue)
    return tonumber(value) or defaultValue
end
function TLConfigBridge.SetFloat(key, value)
    TLConfig.Set(key, tonumber(value) or 0)
end
function TLConfigBridge.GetBool(key, defaultValue)
    local value = TLConfig.Get(key, defaultValue)
    return value == true
end
function TLConfigBridge.SetBool(key, value)
    TLConfig.Set(key, value == true)
end
function TLConfigBridge.Save()
    TLConfig.CSSaveConfig()
end
function TLConfigBridge.Reset()
    local box = GUI.MessageBox("恢复默认", "确定要恢复默认配置吗？记得手动点击保存哦...", { "确定", "取消" })
    box.DrawOnTop = true

    box.Buttons[1].OnClicked = function()
        TLConfig.ResetToDefaults()
        box.Close()
    end

    box.Buttons[2].OnClicked = function()
        box.Close()
    end
end
function TLConfigBridge.GetDescription(key)
    if not TLConfig or not TLConfig.Entries then return "" end
    local entry = TLConfig.Entries[key]
    if not entry then return "" end
    return tostring(entry.description or "")
end
function TLConfigBridge.GetName(key)
    if not TLConfig or not TLConfig.Entries then return key end
    local entry = TLConfig.Entries[key]
    if not entry then return key end
    return tostring(entry.name or key)
end
TL = {}
TL.Name="TaiLa - Deep - Ocean - New Era"
TL.Version = "Dev开发版本-1.0.2"
TL.Path = ...
TLCyb = {}


dofile(TL.Path .. "/Lua/Scripts/Config/configgui.lua")
-- Hook.Add("roundStart", "TLconfig.LoadConfig", function()
--     Timer.Wait(function()
--         TLConfig.LoadConfig()
--     end, 100)
-- end)
--dofile(ST.Path.."/Lua/Autorun/Helperfunctions.lua")
if (Game.IsMultiplayer and SERVER) or not Game.IsMultiplayer then
    dofile(TL.Path.."/Lua/Autorun/HelpFunction.lua")
    --dofile(TL.Path.."/Lua/Scripts/Cyb/CybHF.Lua")
    --dofile(TL.Path.."/Lua/Scripts/Cyb/CybItemUse.lua")
    --dofile(TL.Path.."/Lua/Scripts/Cyb/CybHumanUpdate.Lua")
    --dofile(TL.Path.."/Lua/Scripts/Cyb/items.shared.lua")
    --dofile(TL.Path.."/Lua/Scripts/Missile/Enemy_Anti-shipMissile.lua")

    Timer.Wait(function() Timer.Wait(function()
        local runstring = "\n/// ____Running TaiLa - Deep - Ocean____  "..TL.Version.." ///  \n Loading TaiLa  Deep  Ocean.... Subject \n 泰拉渊洋主体加载已完成...  \n 即将开始读取附件...  \n"

        -- add dashes
        local linelength = string.len(runstring)+4
        local i = 0
        while i < linelength do runstring=runstring.."-" i=i+1 end

        local hasSurgical, err = pcall(function()
            local Prefab = AfflictionPrefab.Prefabs["Surgicalexpansion"]
        end)
        local hasMusics, err = pcall(function()
            local Prefab = AfflictionPrefab.Prefabs["TLmusicpacks"]
        end)
        local hasSub, err = pcall(function()
            local Prefab = AfflictionPrefab.Prefabs["TLsub"]
        end)
        local hasMissiles, err = pcall(function()
            local Prefab = AfflictionPrefab.Prefabs["TLMissiles"]
        end)
        local HasNT, err = pcall(function()
            local Prefab = AfflictionPrefab.Prefabs["th_amputation"]
        end)

        -- No expansions
        runstring = runstring.."\n"
        if hasSurgical then
            runstring = runstring.."--- Surgical expansion has been added \n     -泰拉渊洋 - 改造手术拓展已添加\n    -Version ：0.0.1 测试版(test) \n"
        end
        if hasMusics then
            runstring = runstring.."--- Attached music packs has been added \n     -泰拉渊洋 - 附属音乐包已添加\n    -Version ：1.0.0 \n"
        end
        if hasSub then
            runstring = runstring.."--- Attached Sub packs has been added \n     -泰拉渊洋 - 附属舰艇包已添加\n    -Version ：1.0.0 \n"
        end
        if not hasSurgical and not hasMusics and not hasSub then
            runstring = runstring.."- Not running any expansions\n 目前未运行任何附件\n  目前发布拓展有：附属舰艇包、附属音乐包"
        end
        runstring = runstring.."\n开始读取测试包...\n"
        if hasMissiles then
            runstring = runstring.."--- Missile test package has been added \n     -泰拉渊洋 - 导弹测试包已添加\n    -Version ：0.0.3 \n"
        end
        if not hasMissiles then
            runstring = runstring.."- Not running any test expansions\n 目前未运行任何测试包\n"
        end
        runstring = runstring.."\n开始读取神经创伤...\n"
        if HasNT then
            runstring = runstring.."--- Neurotrauma has been added \n     - 神经创伤 - 已添加\n    医疗物品将执行对应逻辑 \n\n"
        end
        if not HasNT then
            runstring = runstring.."- Not running Neurotrauma\n 未运行 - 神经创伤 -\n \n"
        end
        runstring = runstring.."QQ群：860432216 \n"
        runstring = runstring.."///——————————泰拉渊洋已全部加载完毕——————————\\\\\\"
        print(package.path)
        print(runstring)
    end,3) end,6)
end
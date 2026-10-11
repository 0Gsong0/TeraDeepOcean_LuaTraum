-- ============================================================
-- 其他模组接入「泰拉渊洋 RTS 单位部署」的示例
-- 请把此文件同时加载在客户端和服务器端。
-- ============================================================

-- 改为自己模组 filelist.xml 中的 contentpackage name。
-- 不能填错，否则 %ModDir% 会指向错误的模组。
local PackageName = "YourModName"

-- 最多等待约 5 秒，解决两个模组 Lua 文件加载顺序不固定的问题。
local MaxAttempts = 20
local RetryIntervalMs = 250

-- 在这里填写要注册的单位；不需要的示例可以整项删除。
local Prefabs = {
    {
        -- 示例一：非人类生物
        Identifier = "yourmod:assault_creature",
        DisplayName = "突击生物",
        Description = "可部署的非人类战斗单位。",

        -- 空字符串会使用泰拉渊洋的默认图标。
        -- 自定义图标可写成 "%ModDir%/Content/Images/creature.png"，
        -- 此处的 %ModDir% 会解析到上方 PackageName 对应的模组。
        IconPath = "",
        -- 0 = 人类 NPC
        -- 1 = 非人类生物
        UnitType = 1, 
        SpeciesIdentifier = "your_creature_speciesname",
        -- 部署消耗 目前仅完成了马克花费 填0则无条件部署
        Costs = {
            { ResourceIdentifier = "marks", Amount = 0 }
        }
    },

    {
        -- 示例二：人类 NPC
        Identifier = "yourmod:security_guard",
        DisplayName = "安保队员",
        Description = "可部署的人类战斗单位。",
        IconPath = "%ModDir%/Content/Images/security_guard.png",

        UnitType = 0,
        -- NPC 集
        NpcSetIdentifier = "your_npc_set",
        -- NPC ID
        NpcIdentifier = "your_npc_identifier",

        Costs = {
            { ResourceIdentifier = "marks", Amount = 0 }
        }
    }
}
------------------------ 以下内容不需要修改 ------------------------
-- 尝试取得泰拉渊洋提供的全局部署接口。
-- 接口不存在时不会直接调用，以免在未安装泰拉渊洋时报错。
local function TryRegister(attempt)
    local deployment = rawget(_G, "TLRtsDeployment")

    if type(deployment) ~= "table"
        or type(deployment.RegisterPrefab) ~= "function" then

        if attempt >= MaxAttempts then
            print(
                "[" .. PackageName .. "]"  .. "[RTS] 未找到泰拉渊洋部署接口；" ..
                "跳过单位注册。"
            )
            return
        end

        -- 接口可能只是尚未加载，稍后再检查；不会无限等待。
        if Timer == nil or Timer.Wait == nil then
            return
        end

        Timer.Wait(function()
            TryRegister(attempt + 1)
        end, RetryIntervalMs)
        return
    end

    -- RegisterPrefab 是普通的“点调用”，第一个参数必须是本模组内容包名称。
    -- 不要使用 RegisterLocalPrefab：那个接口属于泰拉渊洋自身。
    for _, prefab in ipairs(Prefabs) do
        local callSucceeded, queuedOrError = pcall(
            deployment.RegisterPrefab,
            PackageName,
            prefab
        )

        if not callSucceeded then
            print(
                "[" .. PackageName .. "]"  .. "[RTS] 调用注册接口出错：" ..
                tostring(prefab.Identifier) .. "，" ..
                tostring(queuedOrError)
            )
        elseif queuedOrError ~= true then
            print(
                "[" .. PackageName .. "]"  .. "[RTS] 注册任务未被接受：" ..
                tostring(prefab.Identifier)
            )
        end
    end
end

-- 稍作延迟，让两个模组的初始化有机会完成。
if Timer ~= nil and Timer.Wait ~= nil then
    Timer.Wait(function()
        TryRegister(1)
    end, 100)
else
    -- 当前运行环境没有计时接口时，安全停止。
    print("[" .. PackageName .. "]" .. "[RTS] Timer 不可用；跳过单位注册。")
end
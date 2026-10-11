-- ============================================================
-- 当前模组的内容包名称
--
-- 必须与 filelist.xml 中的 contentpackage name 完全一致。
-- ============================================================
local PackageName = "TeraDeepOcean"
-- ============================================================
-- 是否注册一个测试单位
-- ============================================================
local EnableTestPrefabRegistration = true
-- ============================================================
-- 注册并取得 C# 静态桥
-- ============================================================
-- 允许 Lua 访问这个 C# 类型。
LuaUserData.RegisterType(
    "TeraDeepOcean.TLRtsDeploymentBridge"
)
-- 创建 C# 静态类的 Lua 访问对象。
local DeploymentBridge = LuaUserData.CreateStatic(
    "TeraDeepOcean.TLRtsDeploymentBridge",
    false
)
-- ============================================================
-- 对外公开的 RTS 部署接口
--
-- 其他模组可以通过全局 TLRtsDeployment 调用我们的部署注册系统。
-- 使用 or {} 可以避免 Lua 热重载时重复创建并丢失已有字段。
-- ============================================================
TLRtsDeployment = TLRtsDeployment or {}
-- 将本模组内容包名称公开出去，方便调试。
TLRtsDeployment.DefaultPackageName = PackageName
-- ============================================================
-- 内部函数：把 Lua table 转换为 JSON
-- ============================================================
---将一个 Lua table 序列化为 JSON 字符串。
---@param prefabDefinition table
---@return boolean success
---@return string resultOrError
local function SerializePrefabDefinition(prefabDefinition)
    if type(prefabDefinition) ~= "table" then
        return false, "部署预制体必须是 Lua table。"
    end
    -- LuaCs 环境正常情况下会提供 json.serialize。
    if json == nil or type(json.serialize) ~= "function" then
        return false, "当前 Lua 环境没有提供 json.serialize。"
    end
    --[[
        Lua 的 pcall 相当于受保护调用。

        如果 json.serialize 抛出错误，例如：
        - table 中包含无法序列化的函数；
        - table 中存在循环引用；
        - 数据结构格式不正确；

        pcall 会返回 false 和错误内容，
        不会让整个 Lua 文件停止运行。
    ]]
    local succeeded, jsonTextOrError = pcall(
        function()
            return json.serialize(prefabDefinition)
        end
    )
    if not succeeded then
        return false,
            "部署预制体转换 JSON 失败：" ..
            tostring(jsonTextOrError)
    end
    if type(jsonTextOrError) ~= "string" or
       jsonTextOrError == "" then
        return false, "部署预制体转换后得到的 JSON 为空。"
    end
    return true, jsonTextOrError
end
-- ============================================================
-- 公共接口：指定内容包注册部署预制体
-- ============================================================

---注册一个 RTS 部署预制体。
---
---这个函数只表示“是否成功提交给 C# 后台线程”。
---最终注册成功或失败会由 C# 输出到控制台。
---
---其他模组应当传入自己的 filelist.xml 内容包名称，
---这样 IconPath 中的普通 %ModDir% 才能解析到正确的模组目录。
---
---示例：
---TLRtsDeployment.RegisterPrefab(
---    "OtherModName",
---    {
---        Identifier = "othermod:unit",
---        ...
---    }
---)
function TLRtsDeployment.RegisterPrefab(sourcePackageName,prefabDefinition)
    if type(sourcePackageName) ~= "string" or sourcePackageName == "" then
        print("[TLRTS部署][Lua] 注册失败：" .."sourcePackageName 不能为空。")
        return false
    end
    local serializeSucceeded, jsonTextOrError = SerializePrefabDefinition(prefabDefinition)
    if not serializeSucceeded then
        print("[TLRTS部署][Lua] 注册失败：" .. tostring(jsonTextOrError))
        return false
    end
    --[[
        调用 C# 时同样使用 pcall。

        这里只能捕获立即发生的错误，例如：
        - C# 类型没有正确加载；
        - 函数名称写错；
        - 参数无法转换；
        - 注册桥尚未初始化。

        后台线程中发生的最终注册错误，
        会由 C# 输出到游戏控制台。
    ]]
    local callSucceeded, queuedOrError = pcall(
        function()
            return DeploymentBridge.RegisterJsonAsync(
                sourcePackageName,
                jsonTextOrError
            )
        end
    )
    if not callSucceeded then
        print("[TLRTS部署][Lua] 调用 C# 注册桥失败：" ..tostring(queuedOrError))
        return false
    end
    --[[
        RegisterJsonAsync 返回 true 只表示任务已经提交。
        这时预制体可能仍然在后台等待解析。
    ]]
    if queuedOrError ~= true then 
         print("[TLRTS部署][Lua] C# 没有接受部署预制体注册任务。")
         return false
    end
    local prefabIdentifier = "<未填写 Identifier>"
    if type(prefabDefinition.Identifier) == "string" and prefabDefinition.Identifier ~= "" then
        prefabIdentifier = prefabDefinition.Identifier
    end
    print("[TLRTS部署][Lua] 已提交异步注册：" ..prefabIdentifier .."（来源：" ..sourcePackageName .."）")
    return true
end
-- ============================================================
-- 便捷接口：注册当前模组自己的单位
-- ============================================================
---使用文件开头定义的 PackageName 注册当前模组单位。
---
---泰拉渊洋自己的单位使用这个函数即可，不必每次重复填写：
---"TeraDeepOcean"
function TLRtsDeployment.RegisterLocalPrefab(prefabDefinition)
    return TLRtsDeployment.RegisterPrefab(PackageName,prefabDefinition)
end
-- ============================================================
-- 调试接口：输出当前 C# 部署预制体注册表
---在控制台输出所有已注册的 RTS 部署预制体。
-- ============================================================
function TLRtsDeployment.DebugPrintRegistry()
    local succeeded, errorMessage = pcall( function()
        DeploymentBridge.DebugPrintRegistry()
    end)
    if not succeeded then
        print("[TLRTS部署][Lua] 输出注册表失败：" ..tostring(errorMessage))
        return false
    end
    return true
end
-- ============================================================
-- 第一版测试单位
--
-- 这里只测试：
-- 1. Lua table 转换 JSON；
-- 2. Lua 调用 C#；
-- 3. C# 后台线程反序列化；
-- 4. 预制体进入注册表；
-- 5. IconPath 为空时使用默认图标；
-- 6. 部署费用为 0 马克。
--
-- 当前阶段不会实际生成 crawler。
-- ============================================================
if EnableTestPrefabRegistration then
    --[[
        稍微延迟提交测试单位。

        这样可以确保 C# 插件的 PreInitPatching 和
        TLRtsDeploymentBridge.Init() 已经完成。
    ]]
    Timer.Wait(
        function()
            TLRtsDeployment.RegisterLocalPrefab(
                {
                    -- 全局唯一的部署预制体标识。
                    Identifier =
                        "teradeepocean:test_crawler",

                    -- 以后显示在部署 UI 中的名称。
                    DisplayName =
                        "测试爬行者",

                    -- 鼠标停留时显示的单位说明。
                    Description =
                        "用于测试 RTS 异步部署预制体注册。",

                    --[[
                        故意留空。

                        C# 标准化时应该将它替换为：
                        TLRtsDeploymentDefaults.DefaultIconPath
                    ]]
                    IconPath = "",

                    --[[
                        TLRtsDeploymentUnitType：
                        HumanNpc = 0
                        Creature = 1
                    ]]
                    UnitType = 1,

                    -- UnitType 为 Creature 时使用 speciesname。
                    SpeciesIdentifier = "crawler",

                    --[[
                        当前第一版部署费用：0 马克。

                        Costs 必须是数组
                    ]]
                    Costs =
                    {
                        {
                            ResourceIdentifier = "marks",
                            Amount = 0
                        }
                    }
                }
            )
            -- 注册一个人类 RTS 部署单位：泰拉护航特战队。
            TLRtsDeployment.RegisterLocalPrefab(
                {
                    -- RTS 部署表中的唯一 ID，不是 NPC XML 的 identifier。
                    Identifier = "teradeepocean:test_escort",

                    DisplayName = "泰拉护航特战队",
                    Description = "测试用人类 RTS 单位，使用模组现有 NPC 预设和装备。",

                    -- 留空则使用 RTS 部署系统的默认图标。
                    IconPath = "",

                    -- HumanNpc = 0；Creature = 1。
                    UnitType = 0,

                    -- 对应 Npcs.xml 中 <npcset identifier="TLnpc">。
                    NpcSetIdentifier = "TLnpc",

                    -- 对应该集合内的 <npc identifier="TLnpc2">。
                    NpcIdentifier = "TLnpc2",

                    -- 首先用 0 马克测试生成；确认成功后再调整价格。
                    Costs =
                    {
                        {
                            ResourceIdentifier = "marks",
                            Amount = 0
                        }
                    }
                }
            )
            -- XCOM测试
           TLRtsDeployment.RegisterLocalPrefab(
                {
                    -- RTS 部署表中的唯一 ID，不是 NPC XML 的 identifier。
                    Identifier = "teradeepocean:ADVENT_Officer",

                    DisplayName = "ADVENT_Officer",
                    Description = "不知道啥玩意",

                    -- 留空则使用 RTS 部署系统的默认图标。
                    IconPath = "",

                    -- HumanNpc = 0；Creature = 1。
                    UnitType = 0,

                    -- 对应 Npcs.xml 中 <npcset identifier="TLnpc">。
                    NpcSetIdentifier = "XCOM_Force",

                    -- 对应该集合内的 <npc identifier="TLnpc2">。
                    NpcIdentifier = "ADVENT_Officer",

                    -- 首先用 0 马克测试生成；确认成功后再调整价格。
                    Costs =
                    {
                        {
                            ResourceIdentifier = "marks",
                            Amount = 0
                        }
                    }
                }
            )
            TLRtsDeployment.RegisterLocalPrefab(
                {
                    -- RTS 部署表中的唯一 ID，不是 NPC XML 的 identifier。
                    Identifier = "teradeepocean:ELACF_W",

                    DisplayName = "牛逼炸肛的NPC",
                    Description = "不知道啥玩意",

                    -- 留空则使用 RTS 部署系统的默认图标。
                    IconPath = "",

                    -- HumanNpc = 0；Creature = 1。
                    UnitType = 0,

                    -- 对应 Npcs.xml 中 <npcset identifier="TLnpc">。
                    NpcSetIdentifier = "XCOM_Force",

                    -- 对应该集合内的 <npc identifier="TLnpc2">。
                    NpcIdentifier = "ELACF_W",

                    -- 首先用 0 马克测试生成；确认成功后再调整价格。
                    Costs =
                    {
                        {
                            ResourceIdentifier = "marks",
                            Amount = 0
                        }
                    }
                }
            )
            TLRtsDeployment.RegisterLocalPrefab(
                {
                    -- 全局唯一的部署预制体标识。
                    Identifier =
                        "teradeepocean:Xcom_mechtoid_azathoth",

                    -- 以后显示在部署 UI 中的名称。
                    DisplayName =
                        "Xcom_mechtoid_azathoth",

                    -- 鼠标停留时显示的单位说明。
                    Description =
                        "不知道啥玩意",

                    --[[
                        故意留空。

                        C# 标准化时应该将它替换为：
                        TLRtsDeploymentDefaults.DefaultIconPath
                    ]]
                    IconPath = "",

                    --[[
                        TLRtsDeploymentUnitType：
                        HumanNpc = 0
                        Creature = 1
                    ]]
                    UnitType = 1,

                    -- UnitType 为 Creature 时使用 speciesname。
                    SpeciesIdentifier = "Xcom_mechtoid_azathoth",

                    --[[
                        当前第一版部署费用：0 马克。

                        Costs 必须是数组
                    ]]
                    Costs =
                    {
                        {
                            ResourceIdentifier = "marks",
                            Amount = 0
                        }
                    }
                }
            )
            TLRtsDeployment.RegisterLocalPrefab(
                {
                    -- 全局唯一的部署预制体标识。
                    Identifier =
                        "teradeepocean:XCOM_Muton_Elite",

                    -- 以后显示在部署 UI 中的名称。
                    DisplayName =
                        "会爬梯子会开门的家伙",

                    -- 鼠标停留时显示的单位说明。
                    Description =
                        "不知道啥玩意",

                    --[[
                        故意留空。

                        C# 标准化时应该将它替换为：
                        TLRtsDeploymentDefaults.DefaultIconPath
                    ]]
                    IconPath = "",

                    --[[
                        TLRtsDeploymentUnitType：
                        HumanNpc = 0
                        Creature = 1
                    ]]
                    UnitType = 1,

                    -- UnitType 为 Creature 时使用 speciesname。
                    SpeciesIdentifier = "XCOM_Muton_Elite",

                    --[[
                        当前第一版部署费用：0 马克。

                        Costs 必须是数组
                    ]]
                    Costs =
                    {
                        {
                            ResourceIdentifier = "marks",
                            Amount = 0
                        }
                    }
                }
            )
        end,

        -- Timer.Wait 使用毫秒。
        100
    )
    if EnableTestPrefabRegistration then
        -- 等待后台注册任务完成后输出注册表。
        Timer.Wait(
            function()
                TLRtsDeployment.DebugPrintRegistry()
            end,

            -- 这里只是调试等待时间，不参与正式注册流程。
            1000
        )
    end
end
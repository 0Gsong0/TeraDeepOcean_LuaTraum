LuaUserData.RegisterType("TeraDeepOcean.TLRtsNetwork")
-- 创建对 C# 静态类 TLRtsNetwork 的访问对象。
local LibRtsNetwork = LuaUserData.CreateStatic("TeraDeepOcean.TLRtsNetwork", false)
--C# 会通过这个全局表调用下面定义的 Lua 函数。
TLRtsNet = TLRtsNet or {}

-- ============================================================
-- 网络消息名称
-- 集中定义可以避免发送端和接收端拼写不一致。
-- ============================================================
local MessageName = {
    -- 申请成为 RTS 指挥官
    ClaimRequest =
    "TLNet.RTS.ClaimRequest",
    -- 主动放弃 RTS 指挥权
    ReleaseRequest =
    "TLNet.RTS.ReleaseRequest",
    -- 请求当前指挥官状态
    SnapshotRequest =
    "TLNet.RTS.CommanderSnapshotRequest",
    -- 接收服务器广播或单独发送的指挥官状态
    CommanderState =
    "TLNet.RTS.CommanderState",
    -- 接收本客户端的指挥官申请结果
    ClaimResult =
    "TLNet.RTS.ClaimResult"
}
-- ============================================================
-- 客户端部分
-- ============================================================
if CLIENT then
    -- 客户端申请成为 RTS 指挥官
    -- jsonText 内容是 TLRtsClaimRequest。
    function TLRtsNet.ClientSendClaimRequest(jsonText)
        if jsonText == nil then return end
        local message = Networking.Start(MessageName.ClaimRequest)
        message.WriteString(jsonText)
        Networking.Send(message)
    end

    -- 客户端主动放弃 RTS 指挥权
    -- 这个请求不需要附带角色 ID。
    -- 服务器能通过消息的 sender 确定发送者。
    function TLRtsNet.ClientSendReleaseRequest()
        local message = Networking.Start(MessageName.ReleaseRequest)
        Networking.Send(message)
    end

    -- 客户端请求当前指挥官状态
    -- 用于刚进入服务器、Lua 刚加载完成或者回合开始。
    function TLRtsNet.ClientRequestCommanderSnapshot()
        local message = Networking.Start(MessageName.SnapshotRequest)
        Networking.Send(message)
    end

    -- 接收服务器广播或单独发送的指挥官状态
    Networking.Receive(MessageName.CommanderState, function(message)
        local jsonText = message.ReadString()
        if jsonText == nil or jsonText == "" then return end
        LibRtsNetwork.ClientReceiveCommanderState(jsonText)
    end)
    -- 接收本客户端的指挥官申请结果
    -- 这个消息只会发送给提出申请的客户端。
    Networking.Receive(MessageName.ClaimResult, function(message)
        local jsonText = message.ReadString()
        if jsonText == nil or jsonText == "" then return end
        LibRtsNetwork.ClientReceiveClaimResult(jsonText)
    end)
end
-- ============================================================
-- 服务器部分
-- ============================================================
if SERVER then
    -- 接收客户端的指挥官申请
    -- sender 是 Barotrauma 网络层提供的真实发送者，
    -- 客户端无法通过 JSON 冒充其他玩家。
    Networking.Receive(MessageName.ClaimRequest, function(message, sender)
        if sender == nil then return end
        local jsonText = message.ReadString()
        if jsonText == nil or jsonText == "" then return end
        LibRtsNetwork.ServerReceiveClaimRequest(jsonText, sender)
    end)
    -- 接收客户端主动释放指挥权的请求
    Networking.Receive(MessageName.ReleaseRequest, function(message, sender)
        if sender == nil then return end
        LibRtsNetwork.ServerReceiveReleaseRequest(sender)
    end)
    -- 接收客户端的当前状态快照请求
    Networking.Receive(MessageName.SnapshotRequest, function(message, sender)
        if sender == nil then return end
        LibRtsNetwork.ServerReceiveSnapshotRequest(sender)
    end)
    -- 服务器发送指挥官申请结果
    -- 申请结果只发送给申请者，不进行广播。
    function TLRtsNet.ServerSendClaimResult(receiver,jsonText)
        if receiver == nil or receiver.Connection == nil or  jsonText == nil then return end
        local message = Networking.Start(MessageName.ClaimResult)
        message.WriteString(jsonText)
        Networking.Send(message,receiver.Connection)
    end
    -- 服务器发送当前指挥官状态
    -- receiver 为 nil：广播给所有客户端。
    function TLRtsNet.ServerSendCommanderState(receiver,jsonText)
        if jsonText == nil then return end
        local message = Networking.Start(MessageName.CommanderState)
        message.WriteString(jsonText)
        if receiver ~= nil and receiver.Connection ~= nil then 
            Networking.Send(message,receiver.Connection)
        else
            Networking.Send(message)
        end
    end
end

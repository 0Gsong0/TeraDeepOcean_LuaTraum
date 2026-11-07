using Barotrauma;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    internal enum TLNetworkHeader
    {
        ConfigRequest = 1,// 客户端请求配置
        ConfigSubmit = 2,// 客户端提交配置
        ConfigSnapshot = 3,// 服务器发送最终配置
        // RTS。
        RtsClaimRequest = 10,
        RtsReleaseRequest = 11,
        RtsSnapshotRequest = 12,
        RtsCommanderState = 13,
        RtsClaimResult = 14,
        RtsMoveRequest = 15,
        RtsAttackRequest = 16,
        RtsDirectionalFireRequest = 17,
        RtsCommandResult = 18,
        RtsRegistrySnapshot = 19,
        RtsReleaseAllAiRequest = 20,
        RtsDeployRequest = 21,
        // Motion
        MotionServerSendSnapshot = 22,
        CybNotice = 23,
        // 客户端请求通过某个外呼面板呼叫电梯。
        ElevatorCallRequest = 30,
        // 服务器返回请求是否被接受。
        ElevatorCallResult = 31,
        // 操作界面的信息查询与楼层提交，共用经过服务器校验的入口。
        ElevatorPanelRequest = 32,
        ElevatorPanelReply = 33,
    }
    // 提交配置和下发配置
    [NetworkSerialize]
    internal readonly record struct TLConfigJsonMessage(string json) : INetSerializableStruct;
    // 请求当前配置
    [NetworkSerialize]
    internal readonly record struct TLConfigRequestMessage : INetSerializableStruct;
}

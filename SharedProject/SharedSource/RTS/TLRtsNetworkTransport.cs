using Barotrauma.Networking;

namespace TeraDeepOcean;


// JSON 仍由 System.Text.Json 处理；这些结构只负责官方 C# 网络封装。
[NetworkSerialize]
internal readonly record struct TLRtsJsonMessage(string Json) : INetSerializableStruct;

[NetworkSerialize]
internal readonly record struct TLRtsEmptyMessage : INetSerializableStruct;

public static partial class TLRtsNetwork
{
    private static void RegisterHandlers()
    {

#if SERVER
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsJsonMessage>(
            TLNetworkHeader.RtsClaimRequest, (data, sender) => ServerReceiveClaimRequest(data.Json, sender));
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsEmptyMessage>(
            TLNetworkHeader.RtsReleaseRequest, (_, sender) => ServerReceiveReleaseRequest(sender));
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsEmptyMessage>(
            TLNetworkHeader.RtsSnapshotRequest, (_, sender) => ServerReceiveSnapshotRequest(sender));
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsJsonMessage>(
            TLNetworkHeader.RtsMoveRequest, (data, sender) => ServerReceiveMoveRequest(data.Json, sender));
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsJsonMessage>(
            TLNetworkHeader.RtsAttackRequest, (data, sender) => ServerReceiveAttackRequest(data.Json, sender));
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsJsonMessage>(
            TLNetworkHeader.RtsDirectionalFireRequest, (data, sender) => ServerReceiveDirectionalFireRequest(data.Json, sender));
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsJsonMessage>(
            TLNetworkHeader.RtsReleaseAllAiRequest, (data, sender) => ServerReceiveReleaseAllAiRequest(data.Json, sender));
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsJsonMessage>(
            TLNetworkHeader.RtsDeployRequest, (data, sender) => ServerReceiveDeployRequest(data.Json, sender));
#else
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsJsonMessage>(
            TLNetworkHeader.RtsCommanderState, data => ClientReceiveCommanderState(data.Json));
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsJsonMessage>(
            TLNetworkHeader.RtsClaimResult, data => ClientReceiveClaimResult(data.Json));
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsJsonMessage>(
            TLNetworkHeader.RtsCommandResult, data => ClientReceiveCommandResult(data.Json));
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLRtsJsonMessage>(
            TLNetworkHeader.RtsRegistrySnapshot, data => ClientReceiveRegistrySnapshot(data.Json));
#endif
    }

#if CLIENT
    private static bool SendRequest(TLNetworkHeader header, string? json = null)
    {
        if (!initialized || GameMain.Client is not { connected: true }) return false;
        if (json is null)
            Plugin.Network.Send(header, new TLRtsEmptyMessage());
        else
            Plugin.Network.Send(header, new TLRtsJsonMessage(json));
        return true;
    }

    // 每次连接都从零开始，避免上一台服务器的 Revision/Sequence 干扰新服务器。
    public static void ResetClientState()
    {
        commanderState = CreateEmptyState(0);
        nextCommandSequence = 0;
        registrySnapshot = new();
        CommanderStateChanged?.Invoke(commanderState);
        RegistrySnapshotChanged?.Invoke(registrySnapshot);
    }
#endif

#if SERVER
    private static void SendResponse(Client? receiver, TLNetworkHeader header, string json)
    {
        if (!initialized || GameMain.NetworkMember is not { IsServer: true }) return;
        var message = new TLRtsJsonMessage(json);
        // 只有指挥官状态允许广播；单位列表与命令回执始终只发给指定玩家。
        if (receiver is null)
        {
            if (header == TLNetworkHeader.RtsCommanderState) 
                Plugin.Network.Broadcast(header, message);
        }
        else if (receiver.Connection != null)
        {
            Plugin.Network.SendToClient(receiver, header, message);
        }
    }
#endif

    public static void Dispose()
    {
        TLRtsSystem.RoundStateCleared -= OnRoundStateCleared;
        initialized = false;
        commanderState = CreateEmptyState(0);
#if SERVER
        commanderClient = null;
        validationTimer = 0;
        lastCommandSequence = 0;
        registryRevision = 0;
#else
        nextCommandSequence = 0;
        registrySnapshot = new();
        RegistrySnapshotChanged = null;
        CommanderStateChanged = null;
        ClaimResultReceived = null;
        CommandResultReceived = null;
#endif
        // IGameNetwork 的消息处理器由官方插件服务在卸载时统一释放。
    }
}

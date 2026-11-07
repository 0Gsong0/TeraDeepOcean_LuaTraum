using System;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace TeraDeepOcean
{
    /// <summary>
    /// 客户端发送的外呼请求。
    /// </summary>
    /// <param name="RequestId">用于匹配本次请求及回执的序号。</param>
    /// <param name="PanelId">玩家操作的外呼面板物品 ID。</param>
    [NetworkSerialize]
    internal readonly record struct TLElevatorCallRequest(
        uint RequestId,
        ushort PanelId) : INetSerializableStruct;

    /// <summary>
    /// 服务器返回的外呼处理结果。
    /// 单人模式也使用同一种结果通知 UI。
    /// </summary>
    /// <param name="RequestId">对应的请求序号。</param>
    /// <param name="PanelId">对应的面板物品 ID。</param>
    /// <param name="Accepted">请求是否被电梯接受。</param>
    /// <param name="Error">拒绝原因，成功时为空字符串。</param>
    [NetworkSerialize]
    public readonly record struct TLElevatorCallResult(
        uint RequestId,
        ushort PanelId,
        bool Accepted,
        string Error) : INetSerializableStruct;

    /// <summary>
    /// 外呼面板的请求与回执通道。
    ///
    /// 客户端只提供面板 ID；
    /// 服务器检查角色交互权限，再通过 Registry 找到电梯和楼层。
    /// </summary>
    public static class TLElevatorCallNetwork
    {
        /// <summary>
        /// 注册此功能使用的网络处理函数。
        /// 在 Plugin.Init 中、注册网络头之后调用一次。
        /// </summary>
        public static void RegisterHandlers()
        {
#if SERVER
            Plugin.Network.RegisterHandler<
                TLNetworkHeader,
                TLElevatorCallRequest>(
                TLNetworkHeader.ElevatorCallRequest,
                OnServerCallRequest);
#endif

#if CLIENT
            Plugin.Network.RegisterHandler<
                TLNetworkHeader,
                TLElevatorCallResult>(
                TLNetworkHeader.ElevatorCallResult,
                OnClientCallResult);
#endif
        }

#if CLIENT
        /// <summary>
        /// 当前客户端的请求序号。
        /// 不在换回合时归零，降低旧回执与新请求混淆的可能性。
        /// </summary>
        private static uint nextRequestId;

        /// <summary>
        /// 请求处理结果通知。
        /// 后续 UI 订阅此事件，并根据 RequestId 判断是否属于自己。
        ///
        /// 单人模式可能在 TryRequestCall 返回之前同步触发事件。
        /// </summary>
        public static event Action<TLElevatorCallResult>? CallResultReceived;

        /// <summary>
        /// UI 点击呼叫按钮时使用的统一入口。
        ///
        /// 返回 true：
        /// 请求已提交，最终是否接受由 CallResultReceived 通知。
        ///
        /// 返回 false：
        /// 本地检查没有通过，请求没有提交，error 给出原因。
        ///
        /// 多人模式的本地检查只改善操作反馈，服务器仍会独立验证。
        /// </summary>
        public static bool TryRequestCall(
            TLElevatorCallPanelComponent panel,
            out uint requestId,
            out string error)
        {
            requestId = 0;
            error = string.Empty;

            if (GameMain.GameSession is not { IsRunning: true })
            {
                error = "当前没有运行中的回合。";
                return false;
            }

            Character? character = Character.Controlled;

            if (!CanCharacterUsePanel(character, panel, out error))
            {
                return false;
            }

            if (GameMain.IsMultiplayer &&
                GameMain.Client is not { connected: true })
            {
                error = "尚未连接到服务器。";
                return false;
            }

            // 0 保留为“未提交请求”，生成有效序号时跳过它。
            unchecked
            {
                nextRequestId++;

                if (nextRequestId == 0)
                {
                    nextRequestId++;
                }
            }

            requestId = nextRequestId;
            ushort panelId = panel.PanelItem.ID;

            if (!GameMain.IsMultiplayer)
            {
                // 单人无需网络，直接进入同一套权限及绑定检查。
                bool accepted = TryCallForCharacter(
                    character,
                    panel,
                    out string callError);

                PublishResult(new TLElevatorCallResult(
                    requestId,
                    panelId,
                    accepted,
                    callError));

                return true;
            }

            // 不发送楼层和控制器 ID。
            // 所属电梯和楼层由服务器上的 Registry 决定。
            Plugin.Network.Send(
                TLNetworkHeader.ElevatorCallRequest,
                new TLElevatorCallRequest(requestId, panelId));

            return true;
        }

        /// <summary>
        /// 接收服务器回执。
        /// 请求序号由 UI 用来筛选当前等待的操作。
        /// </summary>
        private static void OnClientCallResult(TLElevatorCallResult result)
        {
            PublishResult(result);
        }

        /// <summary>
        /// 将单人结果和多人结果通过同一个事件发布。
        /// 未打开 UI 时不额外打印每次呼叫结果。
        /// </summary>
        private static void PublishResult(TLElevatorCallResult result)
        {
            CallResultReceived?.Invoke(result);
        }
#endif

#if SERVER
        /// <summary>
        /// 处理客户端外呼请求。
        ///
        /// sender 由网络服务提供，不相信客户端自行声明角色身份。
        /// 即使客户端跳过本地检查，服务器仍会验证实际角色和面板。
        /// </summary>
        private static void OnServerCallRequest(
            TLElevatorCallRequest request,
            Client sender)
        {
            if (sender is null ||
                sender.Connection is null ||
                GameMain.NetworkMember is not { IsServer: true })
            {
                return;
            }

            bool accepted = false;
            string error;

            if (request.RequestId == 0)
            {
                error = "请求序号无效。";
            }
            else if (Entity.FindEntityByID(request.PanelId)
                     is not Item { Removed: false } panelItem)
            {
                error = "外呼面板不存在。";
            }
            else
            {
                var panel =
                    panelItem.GetComponent<TLElevatorCallPanelComponent>();

                accepted = TryCallForCharacter(
                    sender.Character,
                    panel,
                    out error);
            }

            // 只回复提出请求的玩家，不广播操作回执。
            Plugin.Network.SendToClient(
                sender,
                TLNetworkHeader.ElevatorCallResult,
                new TLElevatorCallResult(
                    request.RequestId,
                    request.PanelId,
                    accepted,
                    error));
        }
#endif

        /// <summary>
        /// 检查角色是否能使用指定外呼面板。
        /// 单人、客户端预检查和服务器验证共用。
        /// </summary>
        private static bool CanCharacterUsePanel(
            Character? character,
            TLElevatorCallPanelComponent? panel,
            out string error)
        {
            error = string.Empty;

            if (character is null ||
                character.Removed ||
                character.IsDead)
            {
                error = "当前角色无法操作外呼面板。";
                return false;
            }

            if (panel is null)
            {
                error = "目标物品没有外呼面板组件。";
                return false;
            }

            Item panelItem = panel.PanelItem;

            if (panelItem.Removed ||
                panelItem.ParentInventory is not null ||
                !ReferenceEquals(
                    Entity.FindEntityByID(panelItem.ID),
                    panelItem))
            {
                error = "外呼面板已失效，或位于库存中。";
                return false;
            }

            // 使用原版距离、遮挡及角色交互状态检查。
            // 不强制要求角色和面板处于同一个 Hull。
            if (!character.CanInteractWith(panelItem))
            {
                error = "无法操作此外呼面板，请靠近并检查是否存在遮挡。";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 以实际操作角色执行呼叫。
        /// 只有单人或服务器可以修改请求队列。
        /// </summary>
        private static bool TryCallForCharacter(
            Character? character,
            TLElevatorCallPanelComponent? panel,
            out string error)
        {
            error = string.Empty;

            if (!TLLinkedMotionSystem.IsAuthority)
            {
                error = "外呼请求需要由服务器处理。";
                return false;
            }

            if (GameMain.GameSession is not { IsRunning: true })
            {
                error = "当前没有运行中的回合。";
                return false;
            }

            if (!CanCharacterUsePanel(character, panel, out error))
            {
                return false;
            }

            // 上面的检查已确认 panel 非空。
            // Registry 再检查绑定、楼层和主控制器有效性。
            return TLElevatorPanelRegistry.TryCall(panel!, out error);
        }

        /// <summary>
        /// 插件卸载时清除 UI 事件订阅。
        /// 网络处理器由官方插件服务随插件卸载统一释放。
        /// </summary>
        public static void Dispose()
        {
#if CLIENT
            CallResultReceived = null;
#endif
        }
    }
}
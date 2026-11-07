using System;
using System.Collections.Generic;
using System.Text.Json;
using Barotrauma;
using Barotrauma.Networking;

namespace TeraDeepOcean
{
    /// <summary>界面请求：查询或提交；楼层号只用于轿厢选层。</summary>
    /// <param name="RequestId">客户端关联序号。</param>
    /// <param name="ItemId">实际操作的物品。</param>
    /// <param name="IsCallPanel">是否为外呼面板。</param>
    /// <param name="Submit">false 查询状态，true 提交呼叫。</param>
    /// <param name="Floor">轿厢面板选择的楼层，外呼忽略此值。</param>
    [NetworkSerialize]
    internal readonly record struct TLElevatorPanelRequest(
        uint RequestId, ushort ItemId, bool IsCallPanel, bool Submit, int Floor) : INetSerializableStruct;

    /// <summary>网络层封装，楼层数组由 JSON 处理，避免依赖数组的网络序列化规则。</summary>
    /// <param name="Json">完整界面回执。</param>
    [NetworkSerialize]
    internal readonly record struct TLElevatorPanelReplyMessage(string Json) : INetSerializableStruct;

    /// <summary>服务器返回给单个操作界面的状态快照。</summary>
    public sealed class TLElevatorPanelReply
    {
        /// <summary>请求序号。</summary>
        public uint RequestId { get; set; }
        /// <summary>操作物品 ID。</summary>
        public ushort ItemId { get; set; }
        /// <summary>查询或提交是否成功，不表示电梯已经到达。</summary>
        public bool Accepted { get; set; }
        /// <summary>错误原因，成功时为空。</summary>
        public string Error { get; set; } = string.Empty;
        /// <summary>当前面板可提交的楼层，按物理高度排序。</summary>
        public int[] Floors { get; set; } = Array.Empty<int>();
        /// <summary>其中已在等待或处理中的楼层。</summary>
        public int[] RequestedFloors { get; set; } = Array.Empty<int>();
        /// <summary>查询或提交时的服务器电梯状态；点击刷新可重新读取。</summary>
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>最小测试 UI 的查询、提交及回执。调度状态仍由主控制器持有。</summary>
    public static class TLElevatorUiNetwork
    {
        /// <summary>在插件初始化时注册两端网络处理器。</summary>
        public static void RegisterHandlers()
        {
#if SERVER
            Plugin.Network.RegisterHandler<TLNetworkHeader, TLElevatorPanelRequest>(
                TLNetworkHeader.ElevatorPanelRequest, OnServerRequest);
#endif
#if CLIENT
            Plugin.Network.RegisterHandler<TLNetworkHeader, TLElevatorPanelReplyMessage>(
                TLNetworkHeader.ElevatorPanelReply, OnClientReply);
#endif
        }

        /// <summary>检查实际角色对实际物品的交互资格，不使用客户端声明的角色。</summary>
        private static bool CanUse(Character? character, Item? item)
        {
            return GameMain.GameSession is { IsRunning: true } &&
                character is { Removed: false, IsDead: false } &&
                item is { Removed: false, ParentInventory: null } &&
                ReferenceEquals(Entity.FindEntityByID(item.ID), item) &&
                character.CanInteractWith(item);
        }

        /// <summary>在单人或服务器处理请求，查询不会向队列添加请求。</summary>
        private static TLElevatorPanelReply Process(TLElevatorPanelRequest request, Character? character)
        {
            var reply = new TLElevatorPanelReply { RequestId = request.RequestId, ItemId = request.ItemId };
            if (!TLLinkedMotionSystem.IsAuthority || request.RequestId == 0 ||
                Entity.FindEntityByID(request.ItemId) is not Item item || !CanUse(character, item))
            {
                reply.Error = "无法交互此面板，请靠近面板并确认角色可以操作。";
                return reply;
            }

            TLElevatorControllerComponent controller;
            TLElevatorLayoutBuilder.ElevatorLayout? layout;
            int requestedFloor = request.Floor;

            if (request.IsCallPanel)
            {
                var panel = item.GetComponent<TLElevatorCallPanelComponent>();
                if (panel is null)
                {
                    reply.Error = "目标不是外呼面板。";
                    return reply;
                }
                if (!TLElevatorPanelRegistry.TryGetBinding(panel, out var binding, out string error))
                {
                    reply.Error = error;
                    return reply;
                }
                controller = binding!.Controller;
                layout = binding.Layout;
                requestedFloor = binding.Floor.Number;
                reply.Floors = new[] { requestedFloor };
            }
            else
            {
                var component = item.GetComponent<TLElevatorControllerComponent>();
                if (component is null)
                {
                    reply.Error = "目标不是电梯主控制器。";
                    return reply;
                }
                controller = component;
                if (!controller.TryGetRuntimeLayout(out layout, out string error))
                {
                    reply.Error = error;
                    return reply;
                }
                var numbers = new List<int>();
                foreach (var floor in layout!.Floors) numbers.Add(floor.Number);
                reply.Floors = numbers.ToArray();
            }

            reply.Accepted = true;
            if (request.Submit)
            {
                // 外呼只允许登记的本站楼层；轿厢选层由主控检查楼层是否存在。
                reply.Accepted = controller.TryRequestFloor(requestedFloor, out string error);
                reply.Error = error;
            }
            var requested = new List<int>();
            foreach (int floor in reply.Floors)
                if (controller.IsFloorRequested(floor)) requested.Add(floor);
            reply.RequestedFloors = requested.ToArray();
            reply.Status = controller.RuntimeStatus;
            return reply;
        }

#if SERVER
        /// <summary>只向真实请求者回复界面数据。</summary>
        private static void OnServerRequest(TLElevatorPanelRequest request, Client sender)
        {
            if (sender?.Connection is null) return;
            var reply = Process(request, sender.Character);
            Plugin.Network.SendToClient(sender, TLNetworkHeader.ElevatorPanelReply,
                new TLElevatorPanelReplyMessage(JsonSerializer.Serialize(reply)));
        }
#endif

#if CLIENT
        /// <summary>每个未结束请求保存物品身份和一次性回调。</summary>
        private static readonly Dictionary<uint, (Item Item, Action<TLElevatorPanelReply> Callback)> pending = new();
        /// <summary>连接或换回合时不复用序号，避免旧回执匹配新的操作。</summary>
        private static uint sequence;

        /// <summary>
        /// 提交界面操作。单人回调会同步执行，多人等待服务器回执。
        /// 调用前应先设置 UI 等待状态，返回后不要再次覆盖回调已经设置的状态。
        /// </summary>
        public static uint Request(Item item, bool isCallPanel, bool submit, int floor, Action<TLElevatorPanelReply> callback)
        {
            unchecked { sequence++; if (sequence == 0) sequence++; }
            var request = new TLElevatorPanelRequest(sequence, item.ID, isCallPanel, submit, floor);
            uint id = request.RequestId;
            pending[id] = (item, callback);
            if (!CanUse(Character.Controlled, item) ||
                (GameMain.IsMultiplayer && GameMain.Client is not { connected: true }))
            {
                Deliver(new TLElevatorPanelReply { RequestId = id, ItemId = item.ID, Error = "当前无法操作面板或尚未连接服务器。" });
            }
            else if (!GameMain.IsMultiplayer)
            {
                Deliver(Process(request, Character.Controlled));
            }
            else
            {
                Plugin.Network.Send(TLNetworkHeader.ElevatorPanelRequest, request);
            }
            return id;
        }

        /// <summary>关闭界面或超时时取消本地等待，不撤销服务器已经接受的请求。</summary>
        public static void Cancel(uint requestId) => pending.Remove(requestId);

        /// <summary>解析服务器返回的 JSON；无效消息不触发操作回调。</summary>
        private static void OnClientReply(TLElevatorPanelReplyMessage message)
        {
            try
            {
                var reply = JsonSerializer.Deserialize<TLElevatorPanelReply>(message.Json);
                if (reply is not null && reply.Floors is not null && reply.RequestedFloors is not null)
                    Deliver(reply);
            }
            catch (JsonException) { }
        }

        /// <summary>按请求号和原物品身份匹配回执，并在回调前移除等待记录。</summary>
        private static void Deliver(TLElevatorPanelReply reply)
        {
            if (!pending.TryGetValue(reply.RequestId, out var entry) || entry.Item.ID != reply.ItemId) return;
            pending.Remove(reply.RequestId);
            if (!entry.Item.Removed && ReferenceEquals(Entity.FindEntityByID(reply.ItemId), entry.Item))
                entry.Callback(reply);
        }
#endif
        /// <summary>插件卸载时释放界面回调；网络处理器由官方插件服务回收。</summary>
        public static void Dispose()
        {
#if CLIENT
            pending.Clear();
#endif
        }
    }
}

using Barotrauma.Networking;
using MoonSharp.Interpreter;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace TeraDeepOcean
{
    /// <summary>
    /// RTS 多人网络系统。
    /// 当前版本只负责“唯一 RTS 指挥官”的申请、验证和状态同步。
    /// </summary>
    public static class TLRtsNetwork
    {
        //指挥设备物品标签。
        private static readonly Identifier CommandDeviceTag = "rtscommanddevice".ToIdentifier();
        private const float CommanderValidationInterval = 0.5f;
        private static bool initialized;
        /// <summary>
        /// 当前已知的指挥官状态。
        /// 服务器上的状态具有最终权威；客户端这里只保存服务器同步过来的副本。
        /// </summary>
        private static TLRtsCommanderState commanderState = CreateEmptyState(0);
        public static TLRtsCommanderState CommanderState => commanderState;

        /// <summary>
        /// 客户端收到新的指挥官状态时触发。
        /// 客户端 UI 可以订阅此事件。
        /// </summary>
        public static event Action<TLRtsCommanderState>? CommanderStateChanged;
        /// <summary>
        /// 客户端收到自己申请指挥官的处理结果时触发。
        /// </summary>
        public static event Action<TLRtsClaimResult>? ClaimResultReceived;
#if SERVER 
        /// <summary>
        /// 服务器保存真正的指挥官客户端对象。
        /// </summary>
        private static Client? commanderClient;
        private static float validationTimer;
#endif
        public static void Init()
        {
            if (initialized) return;
            initialized = true;
            // RTS 回合状态被清除时，网络指挥官状态也必须清除。
            TLRtsSystem.RoundStateCleared += OnRoundStateCleared;
#if SERVER
            commanderClient = null;
            validationTimer = 0.0f;
#endif
            commanderState = CreateEmptyState(0);
        }
        public static void Update(float deltaTime)
        {
#if SERVER
            if (!initialized || GameMain.NetworkMember is not { IsServer: true }) return;
            if (!commanderState.HasCommander) return;
            validationTimer -= deltaTime;
            if (validationTimer > 0f) return;
            validationTimer = CommanderValidationInterval;
            if (!TLRtsRoundSubContext.Refresh())
            {
                ServerClearCommander(broadcast: true);
                return;
            }
            if (!ValidateCurrentCommander(out _))
            {
                ServerClearCommander(broadcast: true);
            }
#endif
        }
        #region 服务器接口
#if SERVER
        /// <summary>
        /// Lua 网络桥收到客户端申请后调用。
        /// sender 由 Barotrauma 网络层提供，
        /// 不能使用客户端自己提交的角色 ID 判断申请者。
        /// </summary>
        /// <param name="json"></param>
        /// <param name="sender"></param>
        public static void ServerReceiveClaimRequest(string json, Client sender)
        {
            if (GameMain.NetworkMember is not { IsServer: true } || sender == null) return;
            TLRtsClaimRequest? request = Deserialize<TLRtsClaimRequest>(json);
            if (request == null)
            {
                ServerSendClaimResult(sender, accepted: false, reason: "RTS 指挥官申请数据无效。");
                return;
            }
            if (commanderClient != null && commanderClient != sender)
            {
                ServerSendClaimResult(sender, accepted: false, reason: $"当前 RTS 指挥官是 {commanderState.CommanderName}。");
                return;
            }
            if (!ValidateClaim(sender, request.DeviceItemId, out Item? device, out string reason))
            {
                ServerSendClaimResult(
                    sender,
                    accepted: false,
                    reason: reason);
                return;
            }
            Character character = sender.Character;
            bool stateChanged = commanderClient != sender || !commanderState.HasCommander || commanderState.CommanderCharacterId != character.ID || commanderState.DeviceItemId != device.ID;
            commanderClient = sender;
            if (stateChanged)
            {
                int nextRevision = commanderState.Revision + 1;
                commanderState = new TLRtsCommanderState
                {
                    HasCommander = true,
                    CommanderCharacterId = character.ID,
                    DeviceItemId = device.ID,
                    CommanderName = sender.Name ?? string.Empty,
                    Revision = nextRevision
                };
                // 向所有客户端公布新的唯一指挥官。
                ServerSendCommanderState(receiver: null);
            }
            /*
             * 单独回复申请者。
             * 即使这是重复申请，也会回复成功。
             */
            ServerSendClaimResult(
                sender,
                accepted: true,
                reason: string.Empty);
        }
        /// <summary>
        /// 客户端加入服务器或主动刷新时，
        /// 把当前状态单独发送给该客户端。
        /// </summary>
        /// <param name="sender"></param>
        public static void ServerReceiveSnapshotRequest(Client sender)
        {
            if (GameMain.NetworkMember is not { IsServer: true } || sender == null) return;
            ServerSendCommanderState(sender);
        }
        /// <summary>
        /// 接收客户端主动释放 RTS 指挥权的请求。
        /// 只有现任指挥官本人才能释放。
        /// </summary>
        /// <param name="sender"></param>
        public static void ServerReceiveReleaseRequest(Client sender)
        {
            if (GameMain.NetworkMember is not { IsServer: true } || sender == null) return;
            // 非指挥官发送释放请求时直接忽略。
            if (commanderClient != sender) return;
            ServerClearCommander(broadcast: true);
        }
        /// <summary>
        /// 验证一次新的指挥官申请。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceItemId"></param>
        /// <param name="device"></param>
        /// <param name="reason"></param>
        /// <returns></returns>
        private static bool ValidateClaim(Client sender, ushort deviceItemId, out Item? device, out string reason)
        {
            device = null;
            reason = string.Empty;
            if (!GameMain.Server.ConnectedClients.Contains(sender))
            {
                reason = "申请者没有连接到服务器。";
                return false;
            }
            if (!Level.IsLoadedOutpost || !TLRtsRoundSubContext.Refresh())
            {
                reason = "RTS 战术模式只能在前哨站回合使用。";
                return false;
            }
            Character? character = sender.Character;
            if (character == null || character.IsDead || character.Removed)
            {
                reason = "申请者角色不可用。";
                return false;
            }
            if (character.IsIncapacitated)
            {
                reason = "申请者已无法行动";
                return false;
            }
            if (!TLRtsRoundSubContext.Contains(character.Submarine))
            {
                reason = "申请者不在前哨站或相连潜艇中。";
                return false;
            }
            if (Entity.FindEntityByID(deviceItemId) is not Item foundDevice || foundDevice.Removed)
            {
                reason = "找不到 RTS 指挥设备。";
                return false;
            }
            if (!foundDevice.HasTag(CommandDeviceTag))
            {
                reason = "指定物品不是有效的 RTS 指挥设备。";
                return false;
            }
            if (!character.HeldItems.Contains(foundDevice))
            {
                reason = "必须把 RTS 指挥设备拿在手中。";
                return false;
            }
            device = foundDevice;
            return true;
        }
        /// <summary>
        /// 定期验证现任指挥官。
        /// </summary>
        /// <param name="reason"></param>
        /// <returns></returns>
        private static bool ValidateCurrentCommander(out string reason)
        {
            reason = string.Empty;
            if (commanderClient == null)
            {
                reason = "指挥官客户端不存在。";
                return false;
            }
            if (!GameMain.Server.ConnectedClients.Contains(commanderClient))
            {
                reason = "指挥官已经断开连接。";
                return false;
            }
            Character? character = commanderClient.Character;
            if (character == null || character.IsDead || character.Removed)
            {
                reason = "指挥官角色已经失效。";
                return false;
            }
            if (character.IsIncapacitated)
            {
                reason = "指挥官已无法行动。";
                return false;
            }
            if (!TLRtsRoundSubContext.Contains(character.Submarine))
            {
                reason = "指挥官已经离开 RTS 活动范围。";
                return false;
            }
            if (Entity.FindEntityByID(commanderState.DeviceItemId) is not Item foundDevice || foundDevice.Removed || !foundDevice.HasTag(CommandDeviceTag))
            {
                reason = "RTS 指挥设备已经失效。";
                return false;
            }
            if (!character.HeldItems.Contains(foundDevice))
            {
                reason = "指挥官已经放下 RTS 指挥设备。";
                return false;
            }
            return true;
        }
        /// <summary>
        /// 服务器清除当前指挥官。
        /// </summary>
        private static void ServerClearCommander(bool broadcast)
        {
            if (!commanderState.HasCommander && commanderClient == null) return;
            int nextRevision = commanderState.Revision + 1;
            commanderClient = null;
            validationTimer = 0.0f;
            commanderState = CreateEmptyState(nextRevision);
            if (broadcast)
            {
                ServerSendCommanderState(receiver: null);
            }
        }
        /// <summary>
        /// 向申请者单独发送申请结果。
        /// </summary>
        /// <param name="receiver"></param>
        /// <param name="accepted"></param>
        /// <param name="reason"></param>
        private static void ServerSendClaimResult(Client receiver, bool accepted, string reason)
        {
            TLRtsClaimResult result = new()
            {
                Accepted = accepted,
                Reason = reason,
                CommanderState = CloneState(commanderState)
            };
            string json = JsonSerializer.Serialize(result);
            CallLua(
                "TLRtsNet",
                "ServerSendClaimResult",
                receiver,
                json);
        }
        /// <summary>
        /// receiver 为 null 时广播给所有客户端；
        /// </summary>
        /// <param name="receiver"></param>
        private static void ServerSendCommanderState(Client? receiver)
        {
            string json = JsonSerializer.Serialize(commanderState);
            CallLua(
                "TLRtsNet",
                "ServerSendCommanderState",
                receiver,
                json);
        }
#endif
        #endregion
        #region 客户端接口
#if CLIENT
        /// <summary>
        /// 当前本机玩家是否是服务器确认的 RTS 指挥官。
        /// 即使进入 RTS 模式后 Character.Controlled 被设为 null，
        /// GameMain.Client.Character 仍然是该客户端在服务器上的角色，
        /// 所以这里不能使用 Character.Controlled 判断。
        /// </summary>
        public static bool IsLocalCommander
        {
            get
            {
                Character? character = GameMain.Client?.Character;
                return commanderState.HasCommander && character!=null && character.ID == commanderState.CommanderCharacterId;
            }
        }
        /// <summary>
        /// 客户端申请成为 RTS 指挥官。
        /// </summary>
        /// <param name="device"></param>
        /// <returns></returns>
        public static bool ClientRequestCommander(Item device)
        {
            if (GameMain.NetworkMember is not { IsClient: true }) return false;
            if (device == null || device.Removed || !device.HasTag(CommandDeviceTag)) return false;
            TLRtsClaimRequest request = new()
            {
                DeviceItemId = device.ID
            };
            string json = JsonSerializer.Serialize(request);
            return CallLua("TLRtsNet", "ClientSendClaimRequest", json);
        }
        /// <summary>
        /// 当前客户端主动放弃 RTS 指挥权。
        /// </summary>
        public static bool ClientRequestRelease()
        {
            if (GameMain.NetworkMember is not { IsClient: true }) return false;
            return CallLua("TLRtsNet", "ClientSendReleaseRequest");
        }
        /// <summary>
        /// 请求服务器发送当前指挥官状态。
        /// </summary>
        /// <returns></returns>
        public static bool ClientRequestSnapshot()
        {
            if (GameMain.NetworkMember is not { IsClient: true }) return false;
            return CallLua("TLRtsNet", "ClientRequestCommanderSnapshot");
        }
        /// <summary>
        /// Lua 网络桥收到服务器状态后调用。
        /// </summary>
        /// <param name="json"></param>
        public static void ClientReceiveCommanderState(string json)
        {
            TLRtsCommanderState? received = Deserialize<TLRtsCommanderState>(json);
            if (received == null) return;
            if (received.Revision < commanderState.Revision) return;
            NormalizeState(received);
            commanderState = received;
            CommanderStateChanged?.Invoke(commanderState);
        }
        /// <summary>
        /// Lua 网络桥收到本机申请结果后调用。
        /// </summary>
        /// <param name="json"></param>
        public static void ClientReceiveClaimResult(string json)
        {
            TLRtsClaimResult? result = Deserialize<TLRtsClaimResult>(json);
            if (result == null) return;
            if (result.CommanderState != null && result.CommanderState.Revision >= commanderState.Revision)
            {
                NormalizeState(result.CommanderState);
                commanderState = result.CommanderState;
                CommanderStateChanged?.Invoke(commanderState);
            }
            ClaimResultReceived?.Invoke(result);
        }
#endif
        #endregion
        #region 其它函数
        private static void OnRoundStateCleared()
        {
#if SERVER
            if (GameMain.NetworkMember is { IsServer: true })
            {
                ServerClearCommander(broadcast: true);
                return;
            }
#endif
#if CLIENT
            //客户端先清除本地显示状态。下一回合开始后再向服务器请求最新快照。
            commanderState = CreateEmptyState(commanderState.Revision);
            CommanderStateChanged?.Invoke(commanderState);
#endif
        }
        private static TLRtsCommanderState CreateEmptyState(int revision)
        {
            return new TLRtsCommanderState()
            {
                HasCommander = false,
                CommanderCharacterId = Entity.NullEntityID,
                DeviceItemId = Entity.NullEntityID,
                CommanderName = string.Empty,
                Revision = revision
            };
        }
        private static TLRtsCommanderState CloneState(TLRtsCommanderState source)
        {
            return new TLRtsCommanderState
            {
                HasCommander = source.HasCommander,
                CommanderCharacterId = source.CommanderCharacterId,
                DeviceItemId = source.DeviceItemId,
                CommanderName = source.CommanderName ?? string.Empty,
                Revision = source.Revision
            };
        }
        /// <summary>
        /// 防止收到 HasCommander=false，
        /// 但仍然带有旧角色 ID 的不完整状态。
        /// </summary>
        /// <param name="state"></param>
        private static void NormalizeState(TLRtsCommanderState state)
        {
            state.CommanderName ??= string.Empty;
            if (state.HasCommander) return;
            state.CommanderCharacterId = Entity.NullEntityID;
            state.DeviceItemId = Entity.NullEntityID;
            state.CommanderName = string.Empty;
        }
        #endregion

        private static T? Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (Exception exception)
            {
                DebugConsole.ThrowError(
                    $"[TeraDeepOcean] 无法解析 RTS 网络数据：{typeof(T).Name}",
                    exception);

                return null;
            }
        }
        private static bool CallLua(string tableName, string functionName, params object[] arguments)
        {
            try
            {
                Script? lua = LuaCsSetup.Instance.Lua;
                if (lua == null) return false;
                DynValue tableValue = lua.Globals.Get(tableName);
                if (tableValue.Type != DataType.Table) return false;
                DynValue function = tableValue.Table.Get(functionName);
                if (function.Type != DataType.Function && function.Type != DataType.ClrFunction) return false;
                lua.Call(function, arguments);
                return true;
            }
            catch (Exception exception)
            {
                DebugConsole.ThrowError($"[TeraDeepOcean] 调用 Lua 网络函数失败：{tableName}.{functionName}", exception);
                return false;
            }
        }
    }
}

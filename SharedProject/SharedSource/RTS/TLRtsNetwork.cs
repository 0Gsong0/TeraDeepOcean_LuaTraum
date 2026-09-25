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
        /// <summary>
        /// 客户端收到移动或攻击处理结果。
        /// </summary>
        public static event Action<TLRtsCommandResult>? CommandResultReceived;

        private const int MaxUnitsPerCommand = 64;
#if SERVER
        /// <summary>
        /// 服务器保存真正的指挥官客户端对象。
        /// </summary>
        private static Client? commanderClient;
        private static float validationTimer;
        /// <summary>
        /// 服务器最后处理的命令序号。
        /// 当前只有一个 RTS 指挥官，因此只需要保存一份。
        /// </summary>
        private static int lastCommandSequence;
        /// <summary>
        /// 服务端 RTS 单位注册表版本。
        /// 每次发送新快照时递增。
        /// </summary>
        private static int registryRevision;
#endif
#if CLIENT
        /// <summary>
        /// 本客户端递增的 RTS 命令序号。
        /// 移动和攻击共用同一个序列。
        /// </summary>
        private static int nextCommandSequence;
        /// <summary>
        /// 客户端保存的服务器 RTS 单位注册表快照。
        /// 它只用于客户端选择和 UI 显示；
        /// 是否真的允许控制，最终仍由服务器验证。
        /// </summary>
        private static TLRtsRegistrySnapshot registrySnapshot = new();
        public static TLRtsRegistrySnapshot RegistrySnapshot => registrySnapshot;
        public static event Action<TLRtsRegistrySnapshot>? RegistrySnapshotChanged;
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
            lastCommandSequence = 0;
            registryRevision = 0;
#endif
#if CLIENT
            nextCommandSequence = 0;
            registrySnapshot = new TLRtsRegistrySnapshot();
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
        /// 创建并发送服务器权威的 RTS 单位注册表。
        ///
        /// 只发送与接收者角色同阵营的单位，
        /// 不向客户端暴露其他阵营的可控单位信息。
        /// </summary>
        /// <param name="receiver"></param>
        private static void ServerSendRegistrySnapshot(Client receiver)
        {
            if (receiver == null || receiver.Character == null) return;
            CharacterTeamType receiverTeam = receiver.Character.TeamID;
            TLRtsRegistrySnapshot snapshot = new()
            {
                Revision = ++registryRevision
            };
            foreach (TLRtsUnitRegistration registration in TLRtsUnitRegistryPermission.Registrations.Values)
            {
                if (!registration.CanControl || registration.TeamId != receiverTeam) continue;
                if (Entity.FindEntityByID(registration.CharacterId) is not Character character || character.Removed || character.IsDead || !character.Enabled) continue;
                snapshot.Units.Add(new TLRtsUnitNetState
                {
                    CharacterId = registration.CharacterId,
                    CanControl = registration.CanControl,
                    TeamId = (int)registration.TeamId,
                    UnitClass = registration.UnitClass.Value ?? string.Empty,
                    IsSpawnedByRts = registration.IsSpawnByRts
                });
            }
            string json = JsonSerializer.Serialize(snapshot);
            CallLua("TLRtsNet", "ServerSendRegistrySnapshot", receiver, json);
        }
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
                /*
                 * 多人模式下单位注册必须发生在服务器。
                 * 客户端之后只接收服务器发来的注册表快照。
                 */
                TLRtsSystem.RegisterCurrentCrewBots();
                TLRtsSystem.RegisterCurrentTeamCreatures(character.TeamID);
                TLRtsUnitRegistryPermission.Unregister(character);
                /*
                 * 服务器完成单位注册后，将当前可控单位列表
                 * 单独发送给新指挥官。
                 */
                ServerSendRegistrySnapshot(sender);
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
        /// 所有客户端都能取得指挥官状态；
        /// 只有当前指挥官能取得可控单位注册表。
        /// </summary>
        /// <param name="sender"></param>
        public static void ServerReceiveSnapshotRequest(Client sender)
        {
            if (GameMain.NetworkMember is not { IsServer: true } || sender == null) return;
            ServerSendCommanderState(sender);
            if (sender == commanderClient && commanderState.HasCommander)
            {
                Character commander = commanderClient.Character;
                if (commander != null)
                {
                    TLRtsUnitRegistryPermission.Unregister(commander);
                    TLRtsSystem.RegisterCurrentCrewBots();
                    TLRtsSystem.RegisterCurrentTeamCreatures(commander.TeamID);
                    ServerSendRegistrySnapshot(sender);
                }
            }
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
            lastCommandSequence = 0;
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
        /// <summary>
        /// 处理客户端移动请求。
        /// 客户端提交的单位 ID、目标 Hull 和坐标都不可信，必须由服务器重新验证。
        /// </summary>
        /// <param name="json"></param>
        /// <param name="sender"></param>
        public static void ServerReceiveMoveRequest(string json, Client sender)
        {
            TLRtsMoveRequest? request = Deserialize<TLRtsMoveRequest>(json);
            if (request == null)
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Move, 0, "移动请求数据无效。"));
                return;
            }
            if (!ValidateCommandSender(sender, request.Sequence, out string reason))
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Move, request.Sequence, reason));
                return;
            }
            if (!TryResolveCommandUnits(request.UnitIds, sender.Character.TeamID, out List<Character> units, out reason))
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Move, request.Sequence, reason));
                return;
            }
            if (!float.IsFinite(request.TargetLocalX) || !float.IsFinite(request.TargetLocalY))
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Move, request.Sequence, "移动目标坐标无效。"));
                return;
            }
            if (Entity.FindEntityByID(request.TargetSubId) is not Submarine targetSubmarine || targetSubmarine.Removed || !TLRtsRoundSubContext.Contains(targetSubmarine))
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Move, request.Sequence, "移动目标潜艇无效。"));
                return;
            }
            if (Entity.FindEntityByID(request.TargetHullId) is not Hull targetHull || targetHull.Removed || targetHull.Submarine != targetSubmarine)
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Move, request.Sequence, "移动目标房间无效。"));
                return;
            }
            Vector2 targetWorldPosition = targetSubmarine.Position + new Vector2(request.TargetLocalX, request.TargetLocalY);
            if (Hull.FindHull(targetWorldPosition) != targetHull)
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Move, request.Sequence, "移动目标不在指定房间内。"));
                return;
            }
            bool issued = TLRtsSystem.IssueMove(units, targetWorldPosition, out IReadOnlyList<Vector2> resolvedSlots);
            if (!issued)
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Move, request.Sequence, "服务器无法执行移动命令。"));
                return;
            }
            TLRtsCommandResult result = new()
            {
                CommandType = TLRtsNetCommandType.Move,
                Sequence = request.Sequence,
                Accepted = true,
                TargetSubmarineId = targetSubmarine.ID,
                TargetLocalX = request.TargetLocalX,
                TargetLocalY = request.TargetLocalY
            };
            foreach (Vector2 slotWorldPosition in resolvedSlots)
            {
                Vector2 localPosition = slotWorldPosition - targetSubmarine.Position;
                result.Slots.Add(new TLRtsNetPosition
                {
                    SubmarineId = targetSubmarine.ID,
                    LocalX = localPosition.X,
                    LocalY = localPosition.Y
                });
            }
            ServerSendCommandResult(sender, result);
        }
        /// <summary>
        /// 处理客户端强制攻击请求。
        /// </summary>
        public static void ServerReceiveAttackRequest(string json, Client sender)
        {
            TLRtsAttackRequest? request = Deserialize<TLRtsAttackRequest>(json);
            if (request == null)
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Attack, 0, "攻击请求数据无效。"));
                return;
            }
            if (!ValidateCommandSender(sender, request.Sequence, out string reason))
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Attack, request.Sequence, reason));
                return;
            }
            if (!TryResolveCommandUnits(request.UnitIds, sender.Character.TeamID, out List<Character> units, out reason))
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Attack, request.Sequence, reason));
                return;
            }
            if (Entity.FindEntityByID(request.TargetCharacterId) is not Character target || target.Removed || target.IsDead || target.Submarine == null || !TLRtsRoundSubContext.Contains(target.Submarine))
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Attack, request.Sequence, "攻击目标无效。"));
                return;
            }
            bool issued = TLRtsSystem.IssueAttack(units, target);
            if (!issued)
            {
                ServerSendCommandResult(sender, CreateRejectedResult(TLRtsNetCommandType.Attack, request.Sequence, "没有单位可以攻击该目标。"));
                return;
            }
            ServerSendCommandResult(sender, new TLRtsCommandResult
            {
                CommandType = TLRtsNetCommandType.Attack,
                Sequence = request.Sequence,
                Accepted = true,
                TargetCharacterId = target.ID
            });
        }
        /// <summary>
        /// 服务器验证
        /// </summary>
        private static bool ValidateCommandSender(Client sender, int sequence, out string reason)
        {
            reason = string.Empty;
            if (sender == null || commanderClient == null || sender != commanderClient || !commanderState.HasCommander)
            {
                reason = "你不是当前 RTS 指挥官。";
                return false;
            }
            if (!TLRtsRoundSubContext.Refresh())
            {
                reason = "RTS 活动区域已经失效。";
                return false;
            }
            if (!ValidateCurrentCommander(out reason))
            {
                ServerClearCommander(broadcast: true);
                return false;
            }
            if (sequence <= lastCommandSequence)
            {
                reason = "RTS 命令已经过期或重复。";
                return false;
            }
            lastCommandSequence = sequence;
            return true;
        }
        /// <summary>
        /// 尝试解析命令单位。
        /// </summary>
        /// <param name="unitIds"></param>
        /// <param name="commanderTeam"></param>
        /// <param name="units"></param>
        /// <param name="reason"></param>
        /// <returns></returns>
        private static bool TryResolveCommandUnits(List<ushort>? unitIds, CharacterTeamType commanderTeam, out List<Character> units, out string reason)
        {
            units = new List<Character>();
            reason = string.Empty;
            if (unitIds == null || unitIds.Count == 0)
            {
                reason = "没有选择 RTS 单位。";
                return false;
            }
            if (unitIds.Count > MaxUnitsPerCommand)
            {
                reason = $"被命令单位不能超过{MaxUnitsPerCommand}个";
                return false;
            }
            foreach (var unitId in unitIds.Distinct())
            {
                if (Entity.FindEntityByID(unitId) is not Character character) continue;
                if (character.Removed || character.IsDead || character.IsIncapacitated || !character.Enabled || character.TeamID != commanderTeam || character.Submarine == null || !TLRtsRoundSubContext.Contains(character.Submarine)) continue;
                if (!TLRtsUnitRegistryPermission.TryGetRegistration(character, out TLRtsUnitRegistration? registration) || registration == null || !registration.CanControl || registration.TeamId != commanderTeam) continue;
                units.Add(character);
            }
            if (units.Count == 0)
            {
                reason = "选中的单位不存在、无权控制或不在 RTS 区域。";
                return false;
            }
            return true;
        }
        /// <summary>
        /// 创建被拒结果
        /// </summary>
        /// <returns></returns>
        private static TLRtsCommandResult CreateRejectedResult(TLRtsNetCommandType commandType, int sequence, string reason)
        {
            return new TLRtsCommandResult
            {
                CommandType = commandType,
                Sequence = sequence,
                Accepted = false,
                Reason = reason ?? string.Empty
            };
        }
        /// <summary>
        /// 服务器发送命令结果
        /// </summary>
        private static void ServerSendCommandResult(Client receiver, TLRtsCommandResult result)
        {
            if (receiver == null) return;
            string json = JsonSerializer.Serialize(result);
            CallLua("TLRtsNet", "ServerSendCommandResult", receiver, json);
        }
        /// <summary>
        /// 处理指挥官发来的“释放全部 AI”请求。
        /// 服务器负责验证身份并实际清除 RTS 状态。
        /// </summary>
        public static void ServerReceiveReleaseAllAiRequest(string json,Client sender)
        {
            TLRtsReleaseAllAiRequest? request = Deserialize<TLRtsReleaseAllAiRequest>(json);

            if (request == null)
            {
                ServerSendCommandResult(
                    sender,
                    CreateRejectedResult(
                        TLRtsNetCommandType.ReleaseAllAi,
                        0,
                        "释放 AI 请求数据无效。"));

                return;
            }

            if (!ValidateCommandSender(
                    sender,
                    request.Sequence,
                    out string reason))
            {
                ServerSendCommandResult(
                    sender,
                    CreateRejectedResult(
                        TLRtsNetCommandType.ReleaseAllAi,
                        request.Sequence,
                        reason));

                return;
            }

            int releasedCount =
                TLRtsSystem.ReleaseAllUnitsToVanillaAi();

            ServerSendCommandResult(
                sender,
                new TLRtsCommandResult
                {
                    CommandType =
                        TLRtsNetCommandType.ReleaseAllAi,

                    Sequence = request.Sequence,
                    Accepted = true,
                    ReleasedUnitCount = releasedCount
                });
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
        public static bool IsLocalCommander => IsCommanderStateOwnedByLocalClient(commanderState);
        /// <summary>
        /// 判断一份指挥官状态是否属于当前客户端。
        /// </summary>
        private static bool IsCommanderStateOwnedByLocalClient(TLRtsCommanderState state)
        {
            if (!state.HasCommander)
            {
                return false;
            }

            // 优先读取网络客户端保存的角色 ID，
            // 不要求 Character 实体已经在客户端创建完成。
            ushort localCharacterId = GameMain.Client?.MyClient?.CharacterID ?? Entity.NullEntityID;

            // CharacterID 尚未可用时，再尝试从角色实例取得 ID。
            if (localCharacterId == Entity.NullEntityID)
            {
                localCharacterId =
                    GameMain.Client?.MyClient?.Character?.ID
                    ?? GameMain.Client?.Character?.ID
                    ?? Entity.NullEntityID;
            }

            return localCharacterId != Entity.NullEntityID && state.CommanderCharacterId == localCharacterId;
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
        /// 接收服务器广播的 RTS 指挥官状态。
        /// </summary>
        /// <param name="json"></param>
        public static void ClientReceiveCommanderState(string json)
        {
            TLRtsCommanderState? received = Deserialize<TLRtsCommanderState>(json);
            if (received == null) return;
            if (received.Revision < commanderState.Revision) return;
            NormalizeState(received);
            commanderState = received;
            if (!IsLocalCommander)
            {
                ClearClientRegistrySnapshot();
            }
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
                if (!IsLocalCommander)
                {
                    ClearClientRegistrySnapshot();
                }
                CommanderStateChanged?.Invoke(commanderState);
            }
            ClaimResultReceived?.Invoke(result);
        }
        /// <summary>
        /// 客户端请求移动
        /// </summary>
        /// <param name="selectedCharacters"></param>
        /// <param name="clickedWorldPosition"></param>
        /// <returns></returns>
        public static bool ClientRequestMove(IEnumerable<Character> selectedCharacters, Vector2 clickedWorldPosition)
        {
            if (GameMain.NetworkMember is not { IsClient: true } || !IsLocalCommander) return false;
            Hull? targetHull = Hull.FindHull(clickedWorldPosition);
            if (targetHull?.Submarine == null || !TLRtsRoundSubContext.Contains(targetHull.Submarine)) return false;
            List<ushort> unitIds = selectedCharacters
                .Where(character =>
                    character != null && !character.Removed && !character.IsDead)
                .Select(character => character.ID)
                .Distinct()
                .Take(MaxUnitsPerCommand)
                .ToList();
            if (unitIds.Count == 0) return false;
            Vector2 targetLocalPosition = clickedWorldPosition - targetHull.Submarine.Position;
            TLRtsMoveRequest request = new()
            {
                Sequence = ++nextCommandSequence,
                UnitIds = unitIds,
                TargetSubId = targetHull.Submarine.ID,
                TargetHullId = targetHull.ID,
                TargetLocalX = targetLocalPosition.X,
                TargetLocalY = targetLocalPosition.Y
            };
            string json = JsonSerializer.Serialize(request);
            return CallLua("TLRtsNet", "ClientSendMoveRequest", json);
        }
        /// <summary>
        /// 客户端请求攻击
        /// </summary>
        /// <param name="selectedCharacters"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        public static bool ClientRequestAttack(IEnumerable<Character> selectedCharacters, Character target)
        {
            if (GameMain.NetworkMember is not { IsClient: true } || !IsLocalCommander || target == null || target.Removed || target.IsDead) return false;
            List<ushort> unitIds = selectedCharacters
                .Where(character =>
                    character != null && !character.Removed && !character.IsDead)
                .Select(character => character.ID)
                .Distinct()
                .Take(MaxUnitsPerCommand)
                .ToList();
            if (unitIds.Count == 0) return false;
            TLRtsAttackRequest request = new()
            {
                Sequence = ++nextCommandSequence,
                UnitIds = unitIds,
                TargetCharacterId = target.ID
            };
            string json = JsonSerializer.Serialize(request);
            return CallLua("TLRtsNet", "ClientSendAttackRequest", json);
        }
        /// <summary>
        /// 客户端接收命令结果
        /// </summary>
        /// <param name="json"></param>
        public static void ClientReceiveCommandResult(string json)
        {
            TLRtsCommandResult? result =Deserialize<TLRtsCommandResult>(json);

            if (result == null)
            {
                return;
            }

            CommandResultReceived?.Invoke(result);
        }
                /// <summary>
        /// 接收服务器发送的 RTS 单位注册表。
        /// </summary>
        public static void ClientReceiveRegistrySnapshot(string json)
        {
            TLRtsRegistrySnapshot? received = Deserialize<TLRtsRegistrySnapshot>(json);
            if (received == null) return;
            received.Units ??= new List<TLRtsUnitNetState>();
            if (received.Revision < registrySnapshot.Revision) return;
            registrySnapshot = received;
            RegistrySnapshotChanged?.Invoke(registrySnapshot);
        }
        /// <summary>
        /// 查询角色是否存在于服务器同步的可控单位列表中。
        /// </summary>
        public static bool IsSyncedUnitControllable(ushort characterId, CharacterTeamType? requiredTeam = null)
        {
            foreach (TLRtsUnitNetState unit in registrySnapshot.Units)
            {
                if (unit.CharacterId != characterId || !unit.CanControl) continue;
                if (requiredTeam.HasValue && unit.TeamId != (int)requiredTeam.Value) continue;
                return true;
            }
            return false;
        }
        /// <summary>
        /// 清除客户端单位快照。
        /// 用于回合结束或失去指挥权。
        /// </summary>
        private static void ClearClientRegistrySnapshot()
        {
            int nextRevision = registrySnapshot.Revision;
            registrySnapshot = new TLRtsRegistrySnapshot
            {
                Revision = nextRevision
            };
            RegistrySnapshotChanged?.Invoke(registrySnapshot);
        }
        /// <summary>
        /// 请求服务器清除全部 RTS 命令，让原版 AI 重新接管。
        /// </summary>
        public static bool ClientRequestReleaseAllAi()
        {
            if (GameMain.NetworkMember is not { IsClient: true } ||
                !IsLocalCommander)
            {
                return false;
            }

            TLRtsReleaseAllAiRequest request = new()
            {
                Sequence = ++nextCommandSequence
            };

            string json =
                JsonSerializer.Serialize(request);

            return CallLua(
                "TLRtsNet",
                "ClientSendReleaseAllAiRequest",
                json);
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
            nextCommandSequence = 0;
            ClearClientRegistrySnapshot();
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

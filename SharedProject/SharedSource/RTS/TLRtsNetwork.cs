using Barotrauma.Networking;
using System;
using System.Collections.Generic;
using System.Text;

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
        public static event Action<TLRtsCommanderState>? OnCommanderStateChanged;
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
        #region 其它函数
        private static void OnRoundStateCleared()
        {
#if CLIENT
            commanderState = CreateEmptyState(0);
            OnCommanderStateChanged?.Invoke(commanderState);
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
#endregion
    }
}

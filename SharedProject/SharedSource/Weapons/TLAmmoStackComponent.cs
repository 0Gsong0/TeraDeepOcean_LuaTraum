using Barotrauma.Items.Components;
using Barotrauma.Networking;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public sealed class TLAmmoStackComponent : ItemComponent, IServerSerializable
    {
        // 预制体省略 capacity 时使用的每堆容量。
        private const int DefaultCapacity = 60;
        // 所有散弹堆共用的容量上限，也是编辑器输入上限。
        private const int MaximumCapacity = 999;
        // 当前实际发数。所有写入都通过 RoundCount 校验。
        private int roundCount;
        /// <summary>
        /// 弹种以散弹物品的预制体标识区分。
        /// </summary>
        public Identifier AmmoType => Item.Prefab.Identifier;
        /// <summary>
        /// 当前弹药堆的容量。
        /// 基类构造时会先读取 RoundCount，因此先用全局上限，
        /// 再由本类构造函数设定 XML 中的实际容量。
        /// </summary>
        public int Capacity { get; } = MaximumCapacity;
        /// <summary>
        /// 当前发数。预制体缺少 roundcount 时默认为 1，0 表示已耗尽。
        /// 原版负责保存、读档、复制，以及在潜艇编辑器中编辑这个属性。
        /// </summary>
        [Serialize(1, IsPropertySaveable.Yes)]
        [Editable(MinValueInt = 0, MaxValueInt = MaximumCapacity)]
        public int RoundCount
        {
            get => roundCount;
            private set => roundCount = Math.Clamp(value, 0, Capacity);
        }
        public TLAmmoStackComponent(Item item, ContentXElement element) : base(item, element)
        {
            Capacity = Math.Clamp(element.GetAttributeInt("capacity", DefaultCapacity), 1, MaximumCapacity);
            RoundCount = roundCount;
        }
        /// <summary>
        /// 在单机或服务器的游戏线程上提交新的发数。
        /// 调用方先检查操作权限、来源和目标。
        /// </summary>
        /// <returns>成功更新返回 true。</returns>
        public bool TrySetRoundCount(int newRoundCount)
        {
            if (GameMain.NetworkMember is { IsClient: true } ||
                Item.Removed ||
                Item.IsInRemoveQueue)
            {
                return false;
            }
            if (newRoundCount < 0 || newRoundCount > Capacity)
            {
                return false;
            }
            RoundCount = newRoundCount;
#if SERVER
            QueueRoundCountSync();
#endif
            return true;
        }
#if SERVER
        /// <summary>
        /// 为当前物品排入一个原版组件事件。
        /// 调用时机是物品已注册、回合完成加载或物品生成事件已排入之后。
        /// </summary>
        internal void QueueRoundCountSync()
        {
            if (GameMain.Server is null || Item.Removed || Item.IsInRemoveQueue)
            {
                return;
            }

            // 事件标识保持一致，让原版合并尚未发送的重复通知。
            Item.CreateServerEvent(this);
        }

        /// <summary>发送事件时读取当前余量，供在线和中途加入的玩家使用。</summary>
        /// <param name="msg">原版物品事件的写入缓冲区。</param>
        /// <param name="c">接收该事件的客户端。</param>
        /// <param name="extraData">原版事件参数，此事件从组件直接取值。</param>
        public void ServerEventWrite(IWriteMessage msg,Client c,NetEntityEvent.IData? extraData = null)
        {
            msg.WriteRangedInteger(RoundCount, 0, MaximumCapacity);
        }
#elif CLIENT
    /// <summary>应用服务器发来的数量，供物品栏角标读取。</summary>
    /// <param name="msg">原版物品事件的读取缓冲区。</param>
    /// <param name="sendingTime">原版提供的事件发送时间。</param>
    public void ClientEventRead(IReadMessage msg, float sendingTime)
    {
        // 接收端直接应用权威状态，游戏内操作仍通过 TrySetRoundCount。
        RoundCount = msg.ReadRangedInteger(0, MaximumCapacity);
    }
#endif
    }
}

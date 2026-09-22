using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// RTS 网络命令类型。
    /// </summary>
    public enum TLRtsNetCommandType
    {
        Move,
        Attack,
    }
    /// <summary>
    /// 客户端申请成为 RTS 指挥官。
    /// 服务器会重新验证该物品是否真的由申请者持有。
    /// </summary>
    public sealed class TLRtsClaimRequest
    {
        public ushort DeviceItemId { get; set; }
    }
    /// <summary>
    /// 服务器公布的唯一指挥官状态。
    /// </summary>
    public sealed class TLRtsCommanderState
    {
        public bool HasCommander { get; set; }
        public ushort CommanderCharacterId { get; set; } = Entity.NullEntityID;
        public ushort DeviceItemId { get; set; } = Entity.NullEntityID;
        public string CommanderName { get; set; } = string.Empty;
        /// <summary>
        /// 状态版本。
        /// 客户端可以用它拒绝过期的网络消息。
        /// </summary>
        public int Revision { get; set; }
    }
    /// <summary>
    /// 服务器对指挥官申请的单独回复。
    /// </summary>
    public sealed class TLRtsClaimResult
    {
        public bool Accepted { get; set; }
        /// <summary>
        /// 拒绝理由。
        /// </summary>
        public string Reason { get; set; } = string.Empty;
        public TLRtsCommanderState CommanderState { get; set; } = new();
    }
    /// <summary>
    /// 客户端发送的移动请求。
    /// 客户端只发送点击位置，不发送最终编队槽位。
    /// 编队槽位必须由服务器计算。
    /// </summary>
    public sealed class TLRtsMoveRequest
    {
        /// <summary>
        /// 客户端递增的命令序号。
        /// 用于过滤重复或乱序命令。
        /// </summary>
        public int Sequence { get; set; }
        public List<ushort> UnitIds { get; set; } = new();
        public ushort TargetSubId { get; set; } = Entity.NullEntityID;
        public ushort TargetHullId { get; set; } = Entity.NullEntityID;
        /// <summary>
        /// 相对于目标潜艇的显示坐标。
        /// 使用局部坐标可以避免潜艇移动后目标点错位。
        /// </summary>
        public float TargetLocalX { get; set; }
        public float TargetLocalY { get; set; }
    }
    /// <summary>
    /// 客户端发送的攻击请求。
    /// </summary>
    public sealed class TLRtsAttackRequest
    {
        public int Sequence { get; set; }
        public List<ushort> UnitIds { get; set; } = new();
        public ushort TargetCharacterId { get; set; } = Entity.NullEntityID;
    }
    /// <summary>
    /// 网络中的潜艇局部坐标。
    /// 用于返回服务器计算出的编队槽位。
    /// </summary>
    public sealed class TLRtsNetPosition
    {
        public ushort SubmarineId { get; set; } = Entity.NullEntityID;
        public float LocalX { get; set; }
        public float LocalY { get; set; }
    }
    /// <summary>
    /// 服务器处理 Move 或 Attack 后返回的结果。
    /// </summary>
    public sealed class TLRtsCommandResult
    {
        public TLRtsNetCommandType CommandType
        {
            get;
            set;
        }
        public int Sequence { get; set; }
        public bool Accepted { get; set; }
        public string Reason { get; set; } = string.Empty;
        #region 移动处理结果
        public ushort TargetSubmarineId { get; set; } = Entity.NullEntityID;
        public float TargetLocalX { get; set; }
        public float TargetLocalY { get; set; }
        /// <summary>
        /// 服务器实际分配给各单位的最终槽位。
        /// 第一版只用于客户端绘制命令标记。
        /// </summary>
        public List<TLRtsNetPosition> Slots { get; set; } = new();
        #endregion
        #region 攻击处理结果
        public ushort TargetCharacterId { get; set; } = Entity.NullEntityID;
        #endregion
    }
    /// <summary>
    /// 一个可控 RTS 单位的网络快照。
    /// 客户端只用它进行选择和 UI 显示，
    /// </summary>
    public sealed class TLRtsUnitNetState
    {
        public ushort CharacterId { get; set; } = Entity.NullEntityID;
        public bool CanControl { get; set; }
        public int TeamId { get; set; }
        public string UnitClass { get; set; } = string.Empty;
        public bool IsSpawnedByRts { get; set; }
    }
    /// <summary>
    /// 服务器发送给客户端的完整可控单位列表。
    /// 用于初次进入、单位变化和中途加入。
    /// </summary>
    public sealed class TLRtsRegistrySnapshot
    {
        public int Revision { get; set; }
        public List<TLRtsUnitNetState> Units { get; set; } = new();
    }
}

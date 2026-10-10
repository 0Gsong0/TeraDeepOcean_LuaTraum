using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 客户端提交的单位部署请求。
    ///
    /// 客户端只提供“想部署什么、部署在哪里”。
    /// 指挥官身份、预制体内容、潜艇归属和费用均由服务端重新验证。
    /// </summary>
    public sealed class TLRtsDeployRequest
    {
        /// <summary>
        /// 与现有移动、攻击命令共用的递增序号。
        /// 用于拒绝重复或过期的请求。
        /// </summary>
        public int Sequence { get; set; }
        /// <summary>
        /// 注册表中的部署预制体标识。
        /// 客户端不发送完整预制体或费用，以服务端注册表为准。
        /// </summary>
        public string PrefabIdentifier { get; set; } = string.Empty;
        /// <summary>
        /// 请求部署到的潜艇实体 ID。
        /// 第一版只接受指挥官所属队伍的主潜艇。
        /// </summary>
        public ushort TargetSubId { get; set; } = Entity.NullEntityID;
        /// <summary>
        /// 鼠标所在房间的实体 ID。
        /// 服务端会检查坐标是否真的位于这个房间。
        /// </summary>
        public ushort TargetHullId { get; set; } = Entity.NullEntityID;

        /// <summary>
        /// 相对于目标潜艇的位置。
        /// 与现有移动命令保持同一种坐标格式。
        /// </summary>
        public float TargetLocalX { get; set; }
        public float TargetLocalY { get; set; }
    }
}

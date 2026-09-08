using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public enum TLRTSUnitState
    {
        Move,
        Guard,
    }
    /// <summary>
    /// 单位当前执行的 RTS 命令。
    /// </summary>
    public class TLRtsUnitState
    {
        public ushort CharacterId { get; init; }
        public TLRTSUnitState Mode { get; set; }
        public ushort TargetSubId { get; set; } = Entity.NullEntityID;
        public ushort TargetHullId { get; set; } = Entity.NullEntityID;
        public ushort TargetWaypointId { get; set; } = Entity.NullEntityID;
        /// <summary>
        /// 相对于目标潜艇的显示坐标。
        /// </summary>
        public Vector2 TargetLocalPosition { get; set; }
        public float ArrivalDistance { get; set; } = 80.0f;
        public Submarine? GetTargetSubmarine()
        {
            return Entity.FindEntityByID(TargetSubId) as Submarine;
        }
        public Hull? GetTargetHull()
        {
            return Entity.FindEntityByID(TargetHullId) as Hull;
        }
        public WayPoint? GetTargetWaypoint()
        {
            return Entity.FindEntityByID(TargetWaypointId) as WayPoint;
        }
        public Vector2 GetGetTargetWorldPosition()
        {
            if(Entity.FindEntityByID(TargetSubId) is Submarine submarine)
            {
                return submarine.Position + TargetLocalPosition;
            }
            return TargetLocalPosition;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public enum TLRTSUnitAIState
    {
        Move,
        Guard,
        /// <summary>
        /// 强制攻击指定角色。
        /// </summary>
        Attack,
        /// <summary>
        /// 原地朝指定方向持续射击。
        ///
        /// 该状态没有角色攻击目标，
        /// 不允许单位向鼠标位置移动。
        /// </summary>
        DirectionalFire
    }
    /// <summary>
    /// 单位当前执行的 RTS 命令。
    /// </summary>
    public class TLRtsUnitState
    {
        public ushort CharacterId { get; init; }
        public TLRTSUnitAIState Mode { get; set; }
        public ushort TargetSubId { get; set; } = Entity.NullEntityID;
        public ushort TargetHullId { get; set; } = Entity.NullEntityID;
        /// <summary>
        /// 仅用于辅助寻路和调试的路径锚点。不是最终移动位置
        /// </summary>
        public ushort TargetWaypointId { get; set; } = Entity.NullEntityID;
        /// <summary>
        /// 相对于目标潜艇的显示坐标。终移动位置
        /// </summary>
        public Vector2 TargetLocalPosition { get; set; }
        /// <summary>
        ///  Attack 状态下的强制攻击目标。
        ///  不保存 Character 引用，避免角色删除后留下失效引用。
        /// </summary>
        public ushort AttackTargetId { get; set; } = Entity.NullEntityID;

        public float ArrivalDistance { get; set; } = 80.0f;
        /// <summary>
        /// Guard 状态下允许离开最终位置的距离。
        /// </summary>
        public float GuardRadius { get; set; } = 90.0f;
        /// <summary>
        /// DirectionalFire 状态下的世界方向。
        ///
        /// 这是归一化方向，不是世界坐标。
        /// 所有选中单位都使用相同方向，实现平行射击。
        /// </summary>
        public Vector2 DirectionalFireDirection { get; set; } = Vector2.UnitX;
        /// <summary>
        /// 方向射击剩余持续时间。
        ///
        /// 进入正式射击阶段后才开始递减。
        /// </summary>
        public float DirectionalFireRemaining { get; set; }
        /// <summary>
        /// 开火前的短暂瞄准时间。
        ///
        /// 避免武器还没有转向目标方向就立刻射出第一发。
        /// </summary>
        public float DirectionalFireAimDelayRemaining { get; set; }
        /// <summary>
        /// 本次方向射击选择的远程武器实体 ID。
        ///
        /// 不直接保存 Item 引用，避免物品被删除后留下失效引用。
        /// </summary>
        public ushort DirectionalFireWeaponItemId { get; set; } = Entity.NullEntityID;
        /// <summary>
        /// 获取本次方向射击使用的武器。
        /// </summary>
        public Item? GetDirectionalFireWeapon()
        {
            return Entity.FindEntityByID(
                DirectionalFireWeaponItemId) as Item;
        }
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
        public Character? GetAttackTarget()
        {
            return Entity.FindEntityByID(AttackTargetId) as Character;
        }
        public Vector2 GetTargetWorldPosition()
        {
            if(Entity.FindEntityByID(TargetSubId) is Submarine submarine)
            {
                return submarine.Position + TargetLocalPosition;
            }
            return TargetLocalPosition;
        }
    }
}

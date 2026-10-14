using Barotrauma.Items.Components;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 电路运动控制器：负责解释信号和配置，不负责逐帧移动实体。
    /// </summary>
    public sealed class TLLinkedMoverComponent : ItemComponent
    {
        // 连续高电平(持续相同的信号)只触发一次；收到明确的 "0" 会立即重置
        private long lastHighTick = long.MinValue;
        /// <summary>
        /// 水平位移，单位为游戏显示坐标中的像素。
        /// </summary>

        [Serialize(0f, IsPropertySaveable.Yes),Editable(MinValueFloat = - 4096f,MaxValueFloat = 4096f , DecimalCount = 1)]
        public float OffsetX { get; set; } = 0f;
        /// <summary>
        /// 垂直位移；正值向上，负值向下。
        /// </summary>
        [Serialize(0f, IsPropertySaveable.Yes), Editable(MinValueFloat = -4096f, MaxValueFloat = 4096f, DecimalCount = 1)]
        public float OffsetY { get; set; } = 0f;
        /// <summary>
        /// 移动速度，单位为像素/游戏秒。
        /// </summary>
        [Serialize(100f, IsPropertySaveable.Yes), Editable(MinValueFloat = 1f, MaxValueFloat = 4096f, DecimalCount = 1)]
        public float MoveSpeed { get; set; } = 100f;
        /// <summary>
        /// 是否移动墙体
        /// </summary>
        [Serialize(true, IsPropertySaveable.Yes), Editable]
        public bool MoveWalls { get; set; } = true;
        /// <summary>
        /// 是否移动物品
        /// </summary>
        [Serialize(true, IsPropertySaveable.Yes), Editable]
        public bool MoveItems { get; set; } = true;
        public TLLinkedMoverComponent(Item item, ContentXElement element) : base(item, element)
        {
            //IsActive = true;
        }
        /// <summary>
        /// 处理电路输入。
        /// set_in：1 前往目标，0 返回原位。
        /// trigger_in：每次新的高电平脉冲切换目标。
        /// 不调用基类的 ReceiveSignal，因为基类会把 trigger_in 当作物品 Use。
        /// </summary>
        /// <param name="signal"></param>
        /// <param name="connection"></param>
        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            if (connection == null || String.IsNullOrWhiteSpace(signal.value)) return;
            if (!TLLinkedMotionSystem.IsAuthority) return;
            switch (connection.Name)
            {
                case "set_in":
                    Submit(signal.value != "0");
                    break;
                case "trigger_in":
                    if (signal.value == "0")
                    {
                        lastHighTick = long.MinValue;
                        return;
                    }
                    long currentTick = TLLinkedMotionSystem.Tick;
                    bool continuousHigh = lastHighTick == currentTick || lastHighTick == currentTick - 1;
                    lastHighTick = currentTick;
                    if (!continuousHigh)
                    {
                        Submit(null);
                    }
                    break;
                default:
                    break;
            }
        }
        private void Submit(bool? go)
        {

        }
    }
}

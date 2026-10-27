using Barotrauma.Items.Components;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 联动旋转控制器。
    ///
    /// 提供旋转配置和旋转中心选择。
    /// </summary>
    public sealed class TLLinkedRotatorComponent : ItemComponent
    {
        /// <summary>
        /// 相对于初始姿态的目标角度，单位为度。
        /// 后续执行器应统一使用数学坐标约定：正角度逆时针。
        /// </summary>
        [Serialize(90f, IsPropertySaveable.Yes),Editable(MinValueFloat = -360f, MaxValueFloat = 360f, DecimalCount = 1)]
        public float TargetAngle { get; set; } = 90f;
        /// <summary>
        /// 旋转速度，单位为度/游戏秒。
        /// 后续执行器还需要进行运行时有效性检查。
        /// </summary>
        [Serialize(30f, IsPropertySaveable.Yes),Editable(MinValueFloat = 1f, MaxValueFloat = 360f, DecimalCount = 1)]
        public float AngularSpeed { get; set; } = 30f;
        /// <summary>
        /// 是否使用玩家设置的旋转中心。
        /// false 表示使用控制器自身的位置。
        ///
        /// 不添加 Editable，避免与按钮操作重复。
        /// Serialize 仍会让它保存到潜艇文件中。
        /// </summary>
        [Serialize(false, IsPropertySaveable.Yes)]
        public bool UseCustomPivot { get; set; }
        /// <summary>
        /// 自定义中心相对于控制器的水平偏移，单位为显示单位。
        /// 这是潜艇局部坐标轴方向的偏移，不随控制器朝向旋转。
        /// </summary>
        [Serialize(0f, IsPropertySaveable.Yes)]
        public float PivotOffsetX { get; set; }
        /// <summary>
        /// 自定义中心相对于控制器的垂直偏移。
        /// </summary>
        [Serialize(0f, IsPropertySaveable.Yes)]
        public float PivotOffsetY { get; set; }
        /// <summary>
        /// 提供控制器物品引用，供选点工具检查对象和读取位置。
        /// 这是运行时访问属性，不需要序列化。
        /// </summary>
        public Item ControllerItem => item;

        public TLLinkedRotatorComponent(Item item, ContentXElement element) : base(item, element)
        {
        }
        /// <summary>
        /// 返回旋转中心在控制器所属坐标空间中的位置。
        /// </summary>
        /// <returns></returns>
        public Vector2 GetPivotLocalPosition()
        {
            if(!UseCustomPivot || !float.IsFinite(PivotOffsetX) || !float.IsFinite(PivotOffsetY))
            {
                return item.Position;
            }
            return item.Position + new Vector2(PivotOffsetX, PivotOffsetY);
        }
        /// <summary>
        /// 返回世界显示坐标中的旋转中心。
        /// 可供之后的中心标记绘制或调试使用。
        /// </summary>
        public Vector2 GetPivotWorldPosition()
        {
            Vector2 localPosition = GetPivotLocalPosition();

            return item.Submarine is null
                ? localPosition
                : localPosition + item.Submarine.Position;
        }
        /// <summary>
        /// 根据世界显示坐标设置旋转中心。
        ///
        /// 鼠标转换得到世界坐标后，与控制器世界位置相减，
        /// 得到可保存的相对偏移，避免保存潜艇当前世界位置。
        /// </summary>
        public bool SetPivotFromWorldPosition(Vector2 worldPosition)
        {
            if(!float.IsFinite(worldPosition.X)||!float.IsFinite(worldPosition.Y))
            {
                return false;
            }
            Vector2 offset = worldPosition - item.WorldPosition;
            if(!float.IsFinite(offset.X) || !float.IsFinite(offset.Y))
            {
                return false;
            }
            PivotOffsetX = offset.X;
            PivotOffsetY = offset.Y;
            UseCustomPivot = true;
            return true;
        }
    }
}

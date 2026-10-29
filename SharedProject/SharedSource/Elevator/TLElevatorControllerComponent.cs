using Barotrauma.Items.Components;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public class TLElevatorControllerComponent : ItemComponent, IDrawableComponent
    {
        /// <summary>
        /// 基准线的总长度
        /// </summary>
        private const float ReferenceLineLength = 640f;
        /// <summary>
        /// 基准线中央短竖线的半高。
        /// </summary>
        private const float ReferenceLineCenterHeight = 10f;
        /// <summary>
        /// 轿厢基准偏移的存储字段。
        /// 通过属性设置，以便检查数值并刷新可见范围缓存。
        /// </summary>
        private float cabinOffsetY;
        /// <summary>
        /// 轿厢基准相对于主控制器中心的垂直偏移。
        /// 例如控制器安装在门底上方 100 的位置，
        /// 想把基准设在门底，就填写 -100。
        /// </summary>
        [Serialize(0f, IsPropertySaveable.Yes), Editable(MinValueFloat = -16384f, MaxValueFloat = 16384f, DecimalCount = 1)]
        public float CabinOffsetY
        {
            get => cabinOffsetY;
            set
            {
                float safeValue = float.IsFinite(value) ? Math.Clamp(value, -16384f, 16384f) : 0f;
                if(cabinOffsetY == safeValue) return;
                cabinOffsetY = safeValue;
#if CLIENT
                item.ResetCachedVisibleSize();
#endif
            }
        }
        /// <summary>
        /// 主控制器所属物品。
        /// 后续用于读取 link、检查实体身份和确定坐标空间。
        /// </summary>
        public Item ControllerItem => item;
        public float GetCabinLocalY()
        {
            return item.Position.Y + CabinOffsetY;
        }
        public float GetCabinWorldY()
        {
            return item.WorldPosition.Y + CabinOffsetY;
        }
        public TLElevatorControllerComponent(Item item, ContentXElement element) : base(item, element)
        {
        }
#if CLIENT
                public Vector2 DrawSize
        {
            get
            {
                // 游戏运行时不画基准线，无需扩大绘制范围。
                if (Screen.Selected != GameMain.SubEditorScreen)
                {
                    return new Vector2(item.Rect.Width, item.Rect.Height);
                }

                float width = MathF.Max(
                    item.Rect.Width,
                    ReferenceLineLength + 10f);

                // 半高需要覆盖偏移距离、中央标记和边距。
                float requiredHalfHeight =
                    MathF.Abs(CabinOffsetY) +
                    ReferenceLineCenterHeight +
                    10f * 0.5f;

                float height = MathF.Max(
                    item.Rect.Height,
                    requiredHalfHeight * 2f);

                return new Vector2(width, height);
            }
        }
        public void Draw(SpriteBatch spriteBatch,bool editing,float itemDepth = -1,Color? overrideColor = null)
        {
            if (!editing ||
                Screen.Selected != GameMain.SubEditorScreen ||
                item.Removed ||
                item.ParentInventory is not null)
            {
                return;
            }

            bool selected = MapEntity.SelectedList.Contains(item);

            Vector2 controllerWorldPosition = item.WorldPosition;

            // 世界坐标正 Y 朝上，绘图坐标正 Y 朝下。
            Vector2 controllerDrawPosition = new(
                controllerWorldPosition.X,
                -controllerWorldPosition.Y);

            Vector2 referenceDrawPosition = new(
                controllerWorldPosition.X,
                -GetCabinWorldY());

            // 主控制器用绿色，与外呼面板的青色区分。
            Color lineColor = selected
                ? Color.LimeGreen
                : Color.LimeGreen * 0.45f;

            float halfLength = ReferenceLineLength * 0.5f;

            // 水平线标记轿厢当前基准高度。
            GUI.DrawLine(
                spriteBatch,
                referenceDrawPosition - Vector2.UnitX * halfLength,
                referenceDrawPosition + Vector2.UnitX * halfLength,
                lineColor,
                width: 2f);

            // 中央短竖线标记控制器的水平位置。
            GUI.DrawLine(
                spriteBatch,
                referenceDrawPosition -
                    Vector2.UnitY * ReferenceLineCenterHeight,
                referenceDrawPosition +
                    Vector2.UnitY * ReferenceLineCenterHeight,
                lineColor,
                width: 2f);

            // 选中时显示控制器中心与基准线之间的偏移。
            if (selected && MathF.Abs(CabinOffsetY) > 0.01f)
            {
                GUI.DrawLine(
                    spriteBatch,
                    controllerDrawPosition,
                    referenceDrawPosition,
                    lineColor * 0.4f,
                    width: 1f);
            }
        }
#endif
    }
}

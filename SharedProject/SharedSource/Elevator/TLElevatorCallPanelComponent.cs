using Barotrauma.Items.Components;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    public sealed class TLElevatorCallPanelComponent : ItemComponent
        , IDrawableComponent
    {
        /// <summary>
        /// 编辑器基准线的总长度
        /// </summary>
        private const float ReferenceLineLength = 640f;
        /// <summary>
        /// 基准线中央标记的半高
        /// </summary>
        private const float ReferenceLineCenterHeight = 10f;
        private float stopOffsetY;
        /// <summary>
        /// 此面板所属楼层的编号。
        /// </summary>
        [Serialize(1, IsPropertySaveable.Yes), Editable(MinValueInt = -99, MaxValueInt = 99)]
        public int Floor { get; set; } = 1;
        /// <summary>
        /// 停靠高度相对于外呼面板中心的垂直偏移。
        /// </summary>
        [Serialize(0f, IsPropertySaveable.Yes), Editable(MinValueFloat = -16384, MaxValueFloat = 16384)]
        public float StopOffestY
        {
            get => stopOffsetY;
            set
            {
                float saveNum = float.IsFinite(value) ? Math.Clamp(value, -16384, 16384) : 0;
                if (StopOffestY == saveNum) return;
                stopOffsetY = saveNum;
#if CLIENT
                item.ResetCachedVisibleSize();
#endif
            }
        }
        public Item PanelItem => item;
        /// <summary>
        /// 获取此楼层的停靠高度。本地坐标
        /// </summary>
        /// <returns></returns>
        public float GetStopLocalY()
        {
            return item.Position.Y + stopOffsetY;
        }
        /// <summary>
        /// 获取此楼层的停靠高度。世界坐标
        /// </summary>
        /// <returns></returns>
        public float GetStopWorldY()
        {
            return item.WorldPosition.Y + stopOffsetY;
        }
        public TLElevatorCallPanelComponent(Item item, ContentXElement element) : base(item, element)
        {
        }
#if CLIENT
        /// <summary>
        /// 此组件需要的绘制范围，供原版进行屏幕可见性判断。
        /// </summary>
        public Vector2 DrawSize
        {
            get
            {
                if (Screen.Selected != GameMain.SubEditorScreen)
                {
                    return new Vector2(item.Rect.Width, item.Rect.Height);
                }
                float width = Math.Max(item.Rect.Width, ReferenceLineLength + 10f);
                float height = Math.Max(item.Rect.Height, ReferenceLineCenterHeight * 2f + Math.Abs(StopOffestY) * 2f + 10f);
                return new Vector2(width, height);
            }
        }
        public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null)
        {
            if(!editing||Screen.Selected != GameMain.SubEditorScreen||item.Removed||item.ParentInventory is not null)
            {
                return;
            }
            bool selected = MapEntity.SelectedList.Contains(item);
            Vector2 panelWorldPosition = item.WorldPosition;
            // 世界坐标正 Y 朝上，SpriteBatch 绘图坐标正 Y 朝下。
            // 在这里统一转换，后面的绘制全部使用绘图坐标。
            Vector2 panelDarwPos = new(panelWorldPosition.X,-panelWorldPosition.Y);
            Vector2 referenceDrawPosition = new(panelWorldPosition.X, -GetStopWorldY());
            // 选中时更醒目；未选中时降低亮度，减少对编辑的干扰。
            Color lineColor = selected ? Color.Cyan : Color.Cyan * 0.45f;
            float halfLength = ReferenceLineLength * 0.5f;
            // 水平线表示最终计算出的停靠高度。
            GUI.DrawLine(
                spriteBatch,
                referenceDrawPosition - Vector2.UnitX * halfLength,
                referenceDrawPosition + Vector2.UnitX * halfLength,
                lineColor,
                width: 2f);
            // 中央短竖线标记此外呼面板对应的水平位置。
            GUI.DrawLine(
                spriteBatch,
                referenceDrawPosition - Vector2.UnitY * ReferenceLineCenterHeight,
                referenceDrawPosition + Vector2.UnitY * ReferenceLineCenterHeight,
                lineColor,
                width: 2f);
            // 选中时画出面板到基准线的连接线，方便理解偏移方向。
            if (selected && MathF.Abs(stopOffsetY) > 0.01f)
            {
                GUI.DrawLine(
                    spriteBatch,
                    panelDarwPos,
                    referenceDrawPosition,
                    lineColor * 0.4f,
                    width: 1f);
            }
        }
#endif
    }
}

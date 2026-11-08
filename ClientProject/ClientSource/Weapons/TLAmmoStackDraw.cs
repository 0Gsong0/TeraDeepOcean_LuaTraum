using Barotrauma.Plugins;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeraDeepOcean
{
    public sealed class TLAmmoStackDraw
    {
        public static void Init(IHarmonyProvider HarmonyProvider)
        {
            HarmonyProvider.GetHarmony().Patch(AccessTools.Method(typeof(Inventory), nameof(Inventory.DrawSlot)),postfix: new HarmonyMethod(typeof(TLAmmoStackDraw), nameof(DrawAmmoCount)));
        }
        private static void DrawAmmoCount(SpriteBatch spriteBatch,VisualSlot slot,Item? item,bool drawItem)
        {
            // 只给当前显示图标的散弹堆画角标。
            if (!drawItem || item?.GetComponent<TLAmmoStackComponent>() is not { } ammoStack)
            {
                return;
            }

            // rect 跟随原版格子的绘制偏移和高亮缩放。
            Rectangle rect = slot.Rect;
            rect.Location += slot.DrawOffset.ToPoint();

            if (slot.HighlightColor.A > 0)
            {
                // 按原版公式计算格子高亮时的放大量。
                float inflateAmount = slot.HighlightColor.A / 255f * slot.HighlightScaleUpAmount * 0.5f;
                rect.Inflate(rect.Width * inflateAmount, rect.Height * inflateAmount);
            }

            // 用实际发数组成角标，例如 x30。减去文字尺寸后与右下角对齐。
            LocalizedString countText = $"x{ammoStack.RoundCount}";
            Vector2 countPosition = new Vector2(rect.Right, rect.Bottom)
                - GUIStyle.SmallFont.MeasureString(countText)
                - new Vector2(4, 2);

            // 先画一像素黑影，再画白字。
            GUIStyle.SmallFont.DrawString(spriteBatch, countText, countPosition + Vector2.One, Color.Black);
            GUIStyle.SmallFont.DrawString(spriteBatch, countText, countPosition, Color.White);
        }
    }
}

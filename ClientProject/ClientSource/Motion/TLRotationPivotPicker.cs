using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeraDeepOcean
{
    /// <summary>
    /// 潜艇编辑器中的旋转中心选点工具。
    /// 同一时刻只允许一个控制器等待选点。
    /// </summary>
    public static class TLRotationPivotPicker
    {
        /// <summary>
        /// 当前等待选择旋转中心的控制器。
        /// null 表示没有进行选点。
        /// </summary>
        private static TLLinkedRotatorComponent? pendingComponent;
        /// <summary>
        /// 开始选点时所在的界面。
        /// 用于发现界面切换，防止把之后的点击用于旧请求。
        /// </summary>
        private static Screen? startingScreen;
        /// <summary>
        /// 是否已经观察到左键松开。
        /// 避免把点击“设置旋转中心”按钮的操作当作场景选点。
        /// </summary>
        private static bool waitingForRelease;
        public static void Init(Harmony harmony)
        {
            Cancel();
            var updateMethod = AccessTools.Method(typeof(SubEditorScreen), nameof(SubEditorScreen.Update), new[] { typeof(double) });
            if (updateMethod is null)
            {
                throw new InvalidOperationException("未找到 SubEditorScreen.Update，无法安装旋转中心选点工具。");
            }
            harmony.Patch(updateMethod, prefix: new HarmonyMethod(typeof(TLRotationPivotPicker), nameof(BeforeEditorUpdate)));
        }
        /// <summary>
        /// 判断指定控制器是否正在等待选点。
        /// 属性面板使用它显示操作提示。
        /// </summary>
        public static bool IsPicking(TLLinkedRotatorComponent component)
        {
            return ReferenceEquals(pendingComponent, component);
        }
        /// <summary>
        /// 开始一次选点。
        /// 新请求会替换之前尚未完成的请求。
        /// </summary>
        public static void Begin(TLLinkedRotatorComponent component)
        {
            Cancel();
            if (Screen.Selected != GameMain.SubEditorScreen || component.ControllerItem.Removed) return;
            pendingComponent = component;
            startingScreen = Screen.Selected;
            waitingForRelease = true;
        }
        /// <summary>
        /// 取消选点，不修改控制器原有配置。
        /// 重置按钮、取消键和插件卸载时都可以调用。
        /// </summary>
        public static void Cancel()
        {
            pendingComponent = null;
            startingScreen = null;
            waitingForRelease = false;
        }
        /// <summary>
        /// 每次客户端 GUI 更新时检查选点请求是否仍然有效。
        /// 即使已经离开编辑器，这个检查仍然可以清理旧请求。
        /// </summary>
        public static void Update()
        {
            if (pendingComponent is null) return;
            Item controller = pendingComponent.ControllerItem;
            if (!ReferenceEquals(Screen.Selected, startingScreen) ||
                controller.Removed ||
                !ReferenceEquals(Entity.FindEntityByID(controller.ID), controller) ||
                !MapEntity.SelectedList.Contains(controller))
            {
                Cancel();
            }
        }
        /// <summary>
        /// 在编辑器处理场景输入之前完成鼠标选点。
        ///
        /// 返回 true：继续原来的编辑器更新。
        /// 返回 false：本次点击已用于选点或取消，跳过这一帧编辑器更新，
        /// 避免同一点击又触发场景选择、拖动或其他编辑动作。
        /// </summary>
        private static bool BeforeEditorUpdate()
        {
            Update();
            if (pendingComponent is null) return true;
            // 取消不修改之前保存的旋转中心。
            if (PlayerInput.KeyHit(Keys.Escape) || PlayerInput.SecondaryMouseButtonClicked())
            {
                Cancel();
                return false;
            }
            // 必须先结束按钮点击，再接受下一次场景点击。
            if (waitingForRelease)
            {
                if (!PlayerInput.PrimaryMouseButtonHeld())
                {
                    waitingForRelease = false;
                }
                return true;
            }
            if (!PlayerInput.PrimaryMouseButtonClicked()) return true;
            // 点击属性面板或其他界面不算场景选点。
            if (GUI.MouseOn is not null || GUI.InputBlockingMenuOpen) return true;
            // ScreenToWorld 已处理相机缩放、平移和屏幕 Y 轴方向。
            // 不要再自行把 worldPosition.Y 取反。
            Vector2 worldPosition = GameMain.SubEditorScreen.Cam.ScreenToWorld(PlayerInput.MousePosition);
            TLLinkedRotatorComponent component = pendingComponent;
            if (component.SetPivotFromWorldPosition(worldPosition))
            {
                Cancel();
            }
            // 无论是否因非法坐标拒绝，本次场景点击都由选点工具处理。
            return false;
        }
    }
}

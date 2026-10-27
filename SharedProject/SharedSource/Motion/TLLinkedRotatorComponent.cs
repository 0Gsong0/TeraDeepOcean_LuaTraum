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
        [Serialize(90f, IsPropertySaveable.Yes), Editable(MinValueFloat = -360f, MaxValueFloat = 360f, DecimalCount = 1)]
        public float TargetAngle { get; set; } = 90f;
        /// <summary>
        /// 旋转速度，单位为度/游戏秒。
        /// 后续执行器还需要进行运行时有效性检查。
        /// </summary>
        [Serialize(30f, IsPropertySaveable.Yes), Editable(MinValueFloat = 1f, MaxValueFloat = 360f, DecimalCount = 1)]
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
        /// <summary>
        /// 上次收到非零触发信号时的全局更新序号。
        /// </summary>
        private long lastHighTick = long.MinValue;
        /// <summary>
        /// 是否已经报告过命令被拒绝。
        /// 持续输入信号时，避免每帧重复打印同一个错误。
        /// </summary>
        private bool commandRejectionReported;
        /// <summary>
        /// 是否旋转直接 link 的结构。
        /// </summary>
        [Serialize(true, IsPropertySaveable.Yes), Editable]
        public bool RotateWalls { get; set; } = true;
        /// <summary>
        /// 是否旋转直接 link 的物品。
        /// 复杂组件仍需通过旋转系统的适配检查。
        /// </summary>
        [Serialize(true, IsPropertySaveable.Yes), Editable]
        public bool RotateItems { get; set; } = true;
        public TLLinkedRotatorComponent(Item item, ContentXElement element) : base(item, element)
        {
        }
        /// <summary>
        /// 返回旋转中心在控制器所属坐标空间中的位置。
        /// </summary>
        /// <returns></returns>
        public Vector2 GetPivotLocalPosition()
        {
            if (!UseCustomPivot || !float.IsFinite(PivotOffsetX) || !float.IsFinite(PivotOffsetY))
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
            if (!float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Y))
            {
                return false;
            }
            Vector2 offset = worldPosition - item.WorldPosition;
            if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y))
            {
                return false;
            }
            PivotOffsetX = offset.X;
            PivotOffsetY = offset.Y;
            UseCustomPivot = true;
            return true;
        }
        /// <summary>
        /// 恢复默认中心，即控制器当前位置。
        /// 同时清空偏移，避免保存无意义的旧数值。
        /// </summary>
        public void ResetPivot()
        {
            UseCustomPivot = false;
            PivotOffsetX = 0;
            PivotOffsetY = 0;
        }
        /// <summary>
        /// 接收电路命令。
        ///
        /// set_in：
        /// "0" 返回初始姿态；其他非空信号前往配置角度。
        ///
        /// trigger_in：
        /// 新的非零触发切换目标；连续高电平只触发一次。
        /// 收到 "0" 立即重置触发检测。
        ///
        /// 这里沿用移动组件的按更新序号判断方式。
        /// 若上游被其他模组降低发送频率，间断高电平仍可能被视为新触发。
        /// </summary>
        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            if (connection is null || string.IsNullOrWhiteSpace(signal.value) || !TLLinkedMotionSystem.IsAuthority) return;
            switch (connection.Name)
            {
                case "set_in":
                    SubmitRotation(signal.value != "0");
                    break;
                case "trigger_in":
                    HandleRotationTrigger(signal.value);
                    break;
            }
        }
        /// <summary>
        /// 判断非零输入是否属于一次新的触发。
        /// 与现有移动组件保持相同的触发规则。
        /// </summary>
        private void HandleRotationTrigger(string value)
        {
            if (value == "0")
            {
                lastHighTick = long.MinValue;
                return;
            }
            long tick = TLLinkedMotionSystem.Tick;
            bool continuousHigh =
               lastHighTick == tick ||
               lastHighTick == tick - 1;
            lastHighTick = tick;
            if (!continuousHigh)
            {
                SubmitRotation(null);
            }
        }
        /// <summary>
        /// 将配置交给统一运动系统。
        /// true 为前往配置角度，false 为返回，null 为切换目标。
        /// </summary>
        private void SubmitRotation(bool? goal)
        {
            bool accepted = TLLinkedMotionSystem.CommandRotation(
                controller: item,
                pivot: GetPivotLocalPosition(),
                angleDegrees: TargetAngle,
                angularSpeed: AngularSpeed,
                rotateWalls: RotateWalls,
                rotateItems: RotateItems,
                goal: goal);
            if (accepted)
            {
                commandRejectionReported = false;
                return;
            }
            if (!commandRejectionReported)
            {
                commandRejectionReported = true;

                Plugin.DebugConsole.NewMessage(
                    $"[TLMotion] 旋转控制器 {item.ID} 拒绝命令：" +
                    "请检查角度、速度、link、目标占用，以及目标组件是否支持旋转。",
                    Color.Orange);
            }
        }
#if CLIENT
        /// <summary>
        /// 在物品配置面板中添加中心选择按钮和状态说明。
        /// 运行中的游戏属性面板会显示按钮，但禁止操作。
        /// </summary>
        public override void CreateEditingHUD(SerializableEntityEditor editor)
        {
            base.CreateEditingHUD(editor);
            // AddCustomContent 会把这行加入属性编辑器的布局。
            var buttonRow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0f), editor.RectTransform)
            {
                MinSize = new Point(0, 36)
            }, isHorizontal: true)
            {
                RelativeSpacing = 0.04f
            };
            var chooseButton = new GUIButton(new RectTransform(new Vector2(0.48f, 1f), buttonRow.RectTransform), "设置旋转中心");
            var resetButton = new GUIButton(new RectTransform(new Vector2(0.48f, 1f), buttonRow.RectTransform), "重置");
            // 先限定为编辑器配置。
            // 运行中修改不能只写客户端属性，否则服务器不会得到新中心。
            bool canEdit = !editor.Readonly && Screen.Selected == GameMain.SubEditorScreen;
            chooseButton.Enabled = canEdit;
            resetButton.Enabled = canEdit;
            chooseButton.ToolTip = "点击后，在场景中选择旋转中心。Esc 或右键取消。";
            resetButton.ToolTip = "恢复为控制器自身的位置。";
            chooseButton.OnClicked = (_, _) =>
            {
                if (Screen.Selected != GameMain.SubEditorScreen)
                {
                    return false;
                }

                TLRotationPivotPicker.Begin(this);
                return true;
            };
            resetButton.OnClicked = (_, _) =>
            {
                if (Screen.Selected != GameMain.SubEditorScreen)
                {
                    return false;
                }

                TLRotationPivotPicker.Cancel();
                ResetPivot();
                return true;
            };
            editor.AddCustomContent(buttonRow, editor.ContentCount);
            var statusText = new GUITextBlock(new RectTransform(new Vector2(1f, 0f), editor.RectTransform)
            {
                MinSize = new Point(0, 48)
            }, "", font: GUIStyle.SmallFont, wrap: true);
            // 动态读取状态，选择完成后无需重新打开属性面板。
            statusText.TextGetter = () =>
            {
                if (TLRotationPivotPicker.IsPicking(this))
                {
                    return "请选择场景位置；Esc 或右键取消。";
                }

                return UseCustomPivot
                    ? $"中心偏移：X={PivotOffsetX:0.0}，Y={PivotOffsetY:0.0}"
                    : "旋转中心：控制器自身位置";
            };
            editor.AddCustomContent(statusText, editor.ContentCount);
        }
#endif
    }
}

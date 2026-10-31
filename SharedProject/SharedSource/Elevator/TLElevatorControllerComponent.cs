using Barotrauma.Items.Components;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TeraDeepOcean
{
    public class TLElevatorControllerComponent : ItemComponent, IDrawableComponent
    {
        /// <summary>
        /// 当前回合是否已经尝试建立布局。
        /// 成功或失败后都设为 true。
        /// </summary>
        private bool layoutInitializationAttempted;
        /// <summary>
        /// 当前回合建立成功的电梯布局。
        ///
        /// 单人游戏和服务器负责建立；多人客户端暂时不建立。
        /// null 表示尚未初始化，或初始化失败。
        /// </summary>
        public TLElevatorLayoutBuilder.ElevatorLayout? Layout { get; private set; }
        /// <summary>
        /// 最近一次布局初始化的错误信息。
        /// 成功或尚未初始化时为空字符串。
        /// 后续可供操作界面显示配置错误。
        /// </summary>
        public string LayoutError { get; private set; } = string.Empty;
        /// <summary>
        /// 布局是否已经建立成功。
        /// 这里只代表配置登记成功。
        /// </summary>
        public bool HasLayout => Layout is not null;

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
        /// 此电梯的 服务器端 楼层请求队列。
        /// 布局建立成功后创建，换回合时清空。
        /// 多人客户端之后通过同步信息显示状态，不自行维护调度队列。
        /// </summary>
        private TLElevatorRequestQueue? requestQueue;
        /// <summary>
        /// 电路上一次成功提交的楼层。
        /// 持续发送相同数字只提交一次，避免完成服务后又被同一信号反复呼叫。
        /// 明确收到空信号时重置；发送另一个楼层也可以产生新请求。
        /// </summary>
        private int? lastSignalFloor;
        /// <summary>
        /// 最近一次报告失败的信号文本。
        /// </summary>
        private string? lastRejectedFloorSignal;
        /// <summary>
        /// 等待处理的楼层数量，不包含已经选定的活动请求。
        /// </summary>
        public int PendingRequestCount => requestQueue?.PendingCount ?? 0;
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
        /// <summary>
        /// 查询某楼层是否已经排队或正在处理。
        /// </summary>
        public bool IsFloorRequested(int floorNumber)
        {
            return requestQueue?.IsRequested(floorNumber) ?? false;
        }
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
            IsActive = true;
        }
        /// <summary>
        /// 地图加载完成时清除旧的登记状态。
        ///
        /// 此时不立即读取全部楼层：
        /// 留到回合已经运行的 Update 中建立布局。
        /// </summary>
        public override void OnMapLoaded()
        {
            base.OnMapLoaded();

            // 重新加载地图时，不继承上一份布局的请求。
            requestQueue?.Clear();
            requestQueue = null;

            // 清除电路输入的去重及错误报告记录。
            lastSignalFloor = null;
            lastRejectedFloorSignal = null;

            layoutInitializationAttempted = false;
            Layout = null;
            LayoutError = string.Empty;

            // 确保组件能够在回合开始后获得初始化机会。
            IsActive = true;
        }
        /// <summary>
        /// 在运行中的回合内尝试一次布局初始化。
        /// </summary>
        public override void Update(float deltaTime, Camera cam)
        {
            // 编辑器预览或回合加载期间，不建立运行布局。
            GameSession? session = GameMain.GameSession;

            if (session is null || !session.IsRunning)
            {
                return;
            }

            // 多人游戏由服务器登记和决策。
            // 客户端未来通过网络接收楼层信息及运行状态。
            if (!TLLinkedMotionSystem.IsAuthority)
            {
                IsActive = false;
                return;
            }

            if (!layoutInitializationAttempted)
            {
                InitializeLayoutOnce();
            }
            IsActive = false;
        }
        /// <summary>
        /// 提交楼层请求的统一入口。
        /// 电路、外呼面板以及 UI 最终都通过这里提交。
        /// </summary>
        public bool TryRequestFloor(int floorNumber, out string error)
        {
            error = string.Empty;
            if (!TLLinkedMotionSystem.IsAuthority)
            {
                error = "楼层请求需要由服务器处理。";
                return false;
            }
            if (GameMain.GameSession is not { IsRunning: true })
            {
                error = "当前没有运行中的回合。";
                return false;
            }
            if (item.Removed || item.ParentInventory is not null)
            {
                error = "电梯主控制器已被移除，或位于库存中。";
                return false;
            }
            // 电路信号可能先于此组件的首次 Update 到达。
            // 此时主动完成一次初始化，仍然不会重复建立布局。
            if (!layoutInitializationAttempted)
            {
                InitializeLayoutOnce();
            }
            if (Layout is null || requestQueue is null)
            {
                error = string.IsNullOrWhiteSpace(LayoutError) ? "电梯布局尚未建立。": LayoutError;
                return false;
            }
            if (!ReferenceEquals(item.Submarine, Layout.CoordinateSubmarine))
            {
                error = "主控制器的所属潜艇发生变化，需要重新建立电梯。";
                return false;
            }
            // 重复请求仍视为接受成功，但不重复输出新增日志。
            bool alreadyRequested = requestQueue.IsRequested(floorNumber);
            if (!requestQueue.TryEnqueue(floorNumber, out error))
            {
                return false;
            }
            if (!alreadyRequested)
            {
                Plugin.DebugConsole.NewMessage(
                    $"[TLElevator|电梯] 主控制器 {item.ID} 收到楼层请求：" +
                    $"{floorNumber}，等待楼层数={requestQueue.PendingCount}。",
                    Color.LightGreen);
            }
            return true;
        }
        /// <summary>
        /// 接收电路楼层请求。
        ///
        /// select_floor_in 接受整数楼层号，包括 0 和负数。
        /// 相同的持续楼层信号只提交一次；空信号用于重新准备下一次输入。
        /// 不调用基类，避免无关信号触发默认物品使用行为。
        /// </summary>
        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            if (connection is null ||
                connection.Name != "select_floor_in" ||
                !TLLinkedMotionSystem.IsAuthority)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(signal.value))
            {
                // 空信号只重置电路触发记录，不取消已经排队的请求。
                lastSignalFloor = null;
                lastRejectedFloorSignal = null;
                return;
            }
            string value = signal.value.Trim();
            if (!int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out int floorNumber))
            {
                ReportRejectedFloorSignal(value, "楼层信号必须是整数。");
                return;
            }
            if (lastSignalFloor == floorNumber)
            {
                return;
            }
            if (!TryRequestFloor(floorNumber, out string error))
            {
                // 失败不记录为成功提交。
                // 持续信号之后仍可重试，但相同错误不会反复打印。
                ReportRejectedFloorSignal(value, error);
                return;
            }
            lastSignalFloor = floorNumber;
            lastRejectedFloorSignal = null;
        }
        /// <summary>
        /// 报告被拒绝的电路请求，相同文本只连续报告一次。
        /// 后续 UI 可以直接显示 TryRequestFloor 返回的错误，不使用此日志。
        /// </summary>
        private void ReportRejectedFloorSignal(string value, string error)
        {
            if (lastRejectedFloorSignal == value)
            {
                return;
            }
            lastRejectedFloorSignal = value;
            Plugin.DebugConsole.NewMessage($"[TLElevator] 主控制器 {item.ID} 拒绝楼层信号“{value}”：{error}",Color.Orange);
        }
        /// <summary>
        /// 尝试建立布局并保存结果。
        /// 错误只报告一次；修改搭建后重新开始测试回合再登记。
        /// </summary>
        private void InitializeLayoutOnce()
        {
            // 在调用构建器前就记录已尝试，避免重复执行。
            layoutInitializationAttempted = true;
            // 清除旧队列引用，确保队列与本次布局一致。
            requestQueue?.Clear();
            requestQueue = null;
            Layout = null;
            LayoutError = string.Empty;
            bool succeeded = TLElevatorLayoutBuilder.TryBuild(this,out var layout,out string error);
            if(!succeeded||layout is null)
            {
                LayoutError = string.IsNullOrWhiteSpace(error) ? "布局登记失败，但没有返回具体原因。" : error;
                Plugin.DebugConsole.NewMessage($"[TLElevator|电梯] 电梯主控制器 {item.ID} 登记失败：{LayoutError}", Color.OrangeRed);
                return;
            }
            Layout = layout;
            requestQueue = new TLElevatorRequestQueue(layout);
            PrintLayoutSummary(layout);
        }
        /// <summary>
        /// 输出成功登记的楼层和轿厢信息。
        /// 楼层按照布局中的实际高度顺序输出，便于核对搭建。
        /// </summary>
        private void PrintLayoutSummary(TLElevatorLayoutBuilder.ElevatorLayout layout)
        {
            StringBuilder message = new();
            message.AppendLine($"[TLElevator|电梯] 主控制器 {item.ID} 登记成功。");
            message.AppendLine($"楼层数：{layout.Floors.Count}；" + $"轿厢实体数：{layout.CabinTargets.Count}（包含主控制器）。");
            message.AppendLine($"当前轿厢基准 Y：{GetCabinLocalY():0.0}。");
            foreach (var floor in layout.Floors)
            {
                message.Append($"楼层 {floor.Number}：" + $"停靠 Y={floor.StopLocalY:0.0}，" + $"外呼面板数={floor.Panels.Count}，面板 ID=");
                // 将同楼层的全部面板列出来，确认多个面板已合并。
                for (int i = 0; i < floor.Panels.Count; i++)
                {
                    if (i > 0)
                    {
                        message.Append(", ");
                    }

                    message.Append(floor.Panels[i].PanelItem.ID);
                }
                message.AppendLine();
            }
            // 输出轿厢目标，确认外呼面板没有混入移动列表。
            message.Append("轿厢目标 ID=");
            for (int i = 0; i < layout.CabinTargets.Count; i++)
            {
                if (i > 0)
                {
                    message.Append(", ");
                }

                message.Append(layout.CabinTargets[i].ID);
            }
            Plugin.DebugConsole.NewMessage(message.ToString(),Color.LightGreen);
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

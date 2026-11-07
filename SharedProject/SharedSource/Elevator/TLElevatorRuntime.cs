using System;

namespace TeraDeepOcean
{
    /// <summary>主控的固定速度运行、停靠及输出状态机。仅单人/服务器执行。</summary>
    public partial class TLElevatorControllerComponent
    {
        /// <summary>整体移动速度，显示单位/游戏秒。</summary>
        [Serialize(100f, IsPropertySaveable.Yes), Editable(MinValueFloat = 1f, MaxValueFloat = 1000f)]
        public float TravelSpeed { get; set; } = 100f;
        /// <summary>到站后最短停靠时间，当前不自动控制门。</summary>
        [Serialize(2f, IsPropertySaveable.Yes), Editable(MinValueFloat = 0.1f, MaxValueFloat = 60f)]
        public float StopDuration { get; set; } = 2f;
        /// <summary>整数位移的最大理论误差为半个单位，另留少量浮点余量。</summary>
        public const float DockingTolerance = 0.51f;
        /// <summary>当前确认停靠的楼层；运行或停在楼层之间时为空。</summary>
        public int? DockedFloor { get; private set; }
        /// <summary>本次正在前往的楼层。</summary>
        public int? TargetFloor => travellingTo?.Number;
        /// <summary>任务丢失或配置失效后的故障说明，修正后需重开回合。</summary>
        public string RuntimeError { get; private set; } = string.Empty;
        /// <summary>供测试界面显示的服务器状态。</summary>
        public string RuntimeStatus => !string.IsNullOrEmpty(RuntimeError) ? "故障：" + RuntimeError :
            travellingTo is not null ? $"运行至 {travellingTo.Number} 层" :
            DockedFloor.HasValue ? $"停靠在 {DockedFloor.Value} 层" : "待命（楼层之间）";
        /// <summary>已固定的本段目的站，不因途经楼层而改变。</summary>
        private TLElevatorLayoutBuilder.FloorEntry? travellingTo;
        /// <summary>仍需等待的停靠游戏时间。</summary>
        private float dwellRemaining;
        /// <summary>是否已经持有完整轿厢任务。</summary>
        private bool motionReady;

        /// <summary>登记成功后创建持续占用，并识别初始停靠层，不产生到达脉冲。</summary>
        private void InitializeElevatorRuntime()
        {
            RuntimeError = string.Empty;
            travellingTo = null;
            DockedFloor = null;
            dwellRemaining = 0f;
            string error = "布局或速度/停靠时间无效。";
            if (Layout is null || !ValidMotionSettings() ||
                !TLLinkedMotionSystem.TryCreateElevatorMotion(item, Layout.CabinTargets, TravelSpeed, out error))
            {
                // 短路分支未调用运动系统时，也给出明确的错误。
                FailElevator(error);
                return;
            }
            motionReady = true;
            foreach (var floor in Layout.Floors)
                if (MathF.Abs(floor.StopLocalY - GetCabinLocalY()) <= DockingTolerance)
                { DockedFloor = floor.Number; break; }
            IsActive = true;
        }

        /// <summary>运行配置必须有效，编辑器输入范围不能替代运行时检查。</summary>
        private bool ValidMotionSettings() => float.IsFinite(TravelSpeed) && TravelSpeed > 0f &&
            float.IsFinite(StopDuration) && StopDuration >= 0.1f;

        /// <summary>
        /// 物品更新阶段处理调度；实体位置由共用运动系统在主更新阶段推进。
        /// 每次只允许一个活动目的站，到站确认后仅本次更新发到达脉冲。
        /// </summary>
        private void UpdateElevatorRuntime(float deltaTime)
        {
            if (!float.IsFinite(deltaTime) || deltaTime <= 0f || Layout is null || requestQueue is null) return;
            if (!motionReady || !ValidMotionSettings() ||
                !TLLinkedMotionSystem.TryGetElevatorMotion(item, out bool moving) || !LandingPanelsRemainValid())
            {
                FailElevator("轿厢任务、楼层面板或运行配置已失效。");
                return;
            }
            int? arrivalPulseFloor = null;
            if (travellingTo is not null)
            {
                if (!moving)
                {
                    // 只有目的站且整段运动完成才可停靠；路过其他层不会走到这里。
                    if (MathF.Abs(GetCabinLocalY() - travellingTo.StopLocalY) > DockingTolerance)
                    { FailElevator("运动结束但轿厢未对齐目标高度。"); return; }
                    DockedFloor = travellingTo.Number;
                    arrivalPulseFloor = DockedFloor;
                    requestQueue.CompleteActiveRequest(travellingTo.Number);
                    travellingTo = null;
                    dwellRemaining = StopDuration;
                }
            }
            else if (dwellRemaining > 0f)
            {
                dwellRemaining = MathF.Max(0f, dwellRemaining - deltaTime);
            }
            else if (requestQueue.TryBeginNextRequest(GetCabinLocalY(), out var next) && next is not null)
            {
                if (DockedFloor == next.Number && MathF.Abs(GetCabinLocalY() - next.StopLocalY) <= DockingTolerance)
                {
                    // 本站重复呼叫只延长停靠，不伪造新的到达事件。
                    requestQueue.CompleteActiveRequest(next.Number);
                    dwellRemaining = StopDuration;
                }
                else if (TLLinkedMotionSystem.TryStartElevatorTrip(item, next.StopLocalY - GetCabinLocalY(), TravelSpeed))
                {
                    travellingTo = next;
                    DockedFloor = null;
                }
                else
                { FailElevator("无法启动下一段电梯运动。"); return; }
            }
            EmitLandingOutputs(arrivalPulseFloor);
        }

        /// <summary>固定楼层不得被删除、放入库存或改成另一楼层。</summary>
        private bool LandingPanelsRemainValid()
        {
            if (Layout is null) return false;
            foreach (var floor in Layout.Floors)
                foreach (var panel in floor.Panels)
                    if (panel.PanelItem.Removed || panel.PanelItem.ParentInventory is not null ||
                        !ReferenceEquals(panel.PanelItem.Submarine, Layout.CoordinateSubmarine) || panel.Floor != floor.Number ||
                        !float.IsFinite(panel.GetStopLocalY()) ||
                        MathF.Abs(panel.GetStopLocalY() - floor.StopLocalY) > TLElevatorLayoutBuilder.FloorHeightTolerance)
                        return false;
            return true;
        }

        /// <summary>同一楼层全部面板同时输出；每次更新只调用一次。</summary>
        private void EmitLandingOutputs(int? arrivalPulseFloor)
        {
            if (Layout is null) return;
            foreach (var floor in Layout.Floors)
                foreach (var panel in floor.Panels)
                    panel.EmitElevatorSignals(DockedFloor == floor.Number, arrivalPulseFloor == floor.Number);
        }

        /// <summary>故障时复位输出、清空请求并释放任务，避免报告虚假的停靠状态。</summary>
        private void FailElevator(string error)
        {
            RuntimeError = error;
            DockedFloor = null;
            travellingTo = null;
            EmitLandingOutputs(null);
            requestQueue?.Clear();
            TLLinkedMotionSystem.ReleaseElevatorMotion(item);
            motionReady = false;
            IsActive = false;
            Plugin.DebugConsole.NewMessage($"[TLElevator] 主控制器 {item.ID} 停机：{error}", Color.OrangeRed);
        }

        /// <summary>重新加载或删除主控时释放旧任务，不保留旧站点信号。</summary>
        private void ResetElevatorRuntime()
        {
            if (TLLinkedMotionSystem.IsAuthority)
            {
                DockedFloor = null;
                EmitLandingOutputs(null);
                TLLinkedMotionSystem.ReleaseElevatorMotion(item);
            }
            travellingTo = null;
            motionReady = false;
            dwellRemaining = 0f;
            RuntimeError = string.Empty;
        }
    }
}

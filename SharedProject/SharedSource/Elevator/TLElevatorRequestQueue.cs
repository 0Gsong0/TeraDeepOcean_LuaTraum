using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 一台电梯的楼层请求队列。
    ///
    /// 轿厢选层和楼层外呼最终都调用 TryEnqueue。
    /// 主控制器通过 TryBeginNextRequest 选择下一站，
    /// 完成停靠服务后再调用 CompleteActiveRequest。
    ///
    /// 多人模式下，只由服务器维护这份运行队列。
    /// </summary>
    public sealed class TLElevatorRequestQueue
    {
        /// <summary>
        /// 此队列对应的电梯布局。
        /// 只有布局中已经登记的楼层才能提交请求。
        /// </summary>
        private readonly TLElevatorLayoutBuilder.ElevatorLayout layout;
        /// <summary>
        /// 等待处理的楼层编号。
        /// </summary>
        private readonly HashSet<int> pendingFloors = new();
        /// <summary>
        /// 当前请求的目标楼层。
        /// 当前已经选定、尚未完成服务的楼层。
        /// </summary>
        private TLElevatorLayoutBuilder.FloorEntry? activeFloor;
        /// <summary>
        /// 当前请求的目标楼层。
        /// </summary>
        public TLElevatorLayoutBuilder.FloorEntry? ActiveFloor => activeFloor;
        /// <summary>
        /// 是否存在已经选定、尚未完成的请求。
        /// </summary>
        public bool HasActiveRequest => activeFloor is not null;
        /// <summary>
        /// 等待处理的楼层数量。
        /// 不包含 ActiveFloor。
        /// </summary>
        public int PendingCount => pendingFloors.Count;
        /// <summary>
        /// 当前是否完全没有请求。
        /// </summary>
        public bool IsEmpty => activeFloor is null && pendingFloors.Count == 0;
        /// <summary>
        /// 下一站选择时优先使用的方向。
        ///
        /// 1：向上；
        /// -1：向下；
        /// 0：尚无优先方向，先选择距离最近的楼层。
        /// </summary>
        public int PreferredDirection { get; private set; }
        /// <summary>
        /// 为一份有效布局创建独立的请求队列。
        /// </summary>
        public TLElevatorRequestQueue(TLElevatorLayoutBuilder.ElevatorLayout layout)
        {
            this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        }
        /// <summary>
        /// 提交一个楼层请求。
        /// true 表示楼层有效、请求已接受；
        /// false 表示没有登记这个楼层。
        /// </summary>
        /// <param name="num"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        public bool TryEnqueue(int num,out string error)
        {
            error = string.Empty;
            if(!layout.TryGetFloor(num,out var floor))
            {
                error = $"没有登记这个楼层{num}";
                return false;
            }
            if (activeFloor?.Number == num)
            {
                return true;
            }
            pendingFloors.Add(num);
            return true;
        }
        /// <summary>
        /// 检查某楼层是否已经排队或正在处理。
        /// </summary>
        public bool IsRequested(int num)
        {
            return activeFloor?.Number == num||pendingFloors.Contains(num);
        }
        /// <summary>
        /// 尝试从等待集合中选择并开始下一条请求。
        /// 已经有活动请求、没有等待请求或高度无效时返回 false。
        /// 调用方应传入 GetCabinLocalY()。
        /// </summary>
        /// <param name="cabinLocalY">主控制器当前的轿厢基准高度</param>
        /// <param name="nextFloor"></param>
        /// <returns></returns>
        public bool TryBeginNextRequest(float cabinLocalY, out TLElevatorLayoutBuilder.FloorEntry? nextFloor)
        {
            nextFloor = null;
            if (activeFloor is not null || pendingFloors.Count == 0 || !float.IsFinite(cabinLocalY)) return false;
            var selected = FindNearestPendingFloor(cabinLocalY, PreferredDirection);
            if(selected is null&&PreferredDirection!=0)
            {
                selected = FindNearestPendingFloor(cabinLocalY, -PreferredDirection);
            }
            if (selected is null) return false;
            float heightDifference = selected.StopLocalY - cabinLocalY;
            if(heightDifference > 0)
            {
                PreferredDirection = 1;
            }
            else if (heightDifference < 0f)
            {
                PreferredDirection = -1;
            }
            pendingFloors.Remove(selected.Number);
            nextFloor = selected;
            activeFloor = selected;
            return true;
        }
        /// <summary>
        /// 标记当前楼层请求已经服务完成。
        /// 调用方必须先确认电梯已完成实际停靠服务。
        /// </summary>
        public bool CompleteActiveRequest(int floorNumber)
        {
            if(activeFloor is null||activeFloor.Number != floorNumber) return false;
            activeFloor = null;
            if(pendingFloors.Count == 0)
            {
                PreferredDirection = 0;
            }
            return true;
        }
        /// <summary>
        /// 中断当前请求，并将它放回等待集合。
        /// 例如后续故障处理暂停服务时，保留乘客原来的请求。
        /// </summary>
        public void RequeueActiveRequest()
        {
            if (activeFloor is null) return;
            pendingFloors.Add(activeFloor.Number);
            activeFloor = null;
        }
        /// <summary>
        /// 取得等待楼层的只读副本，按实际高度从低到高排列。
        /// </summary>
        public IReadOnlyList<int> GetPendingFloorNumbers()
        {
            List<int> result = new();
            foreach (var floor in layout.Floors)
            {
                if (pendingFloors.Contains(floor.Number))
                {
                    result.Add(floor.Number);
                }
            }
            return result;
        }
        /// <summary>
        /// 清空全部等待请求、活动请求及优先方向。
        /// 用于明确的系统重置或回合清理。
        /// </summary>
        public void Clear()
        {
            pendingFloors.Clear();
            activeFloor = null;
            PreferredDirection = 0;
        }
        /// <summary>
        /// 在指定方向上寻找距离轿厢最近的等待楼层。
        ///
        /// direction 为 0 时不限制方向。
        /// 与当前高度相同的请求在两个方向上都允许被选择。
        /// </summary>
        private TLElevatorLayoutBuilder.FloorEntry? FindNearestPendingFloor(float cabinLocalY, int direction)
        {
            TLElevatorLayoutBuilder.FloorEntry? nearest = null;
            float nearestDistance = float.PositiveInfinity;
            // Floors 已按高度排序，因此距离相同时优先较低的一层，
            // 不会受面板 link 顺序或 HashSet 顺序影响。
            foreach (var floor in layout.Floors)
            {
                if (!pendingFloors.Contains(floor.Number))
                {
                    continue;
                }
                float difference = floor.StopLocalY - cabinLocalY;
                if (direction > 0 && difference < 0f)
                {
                    continue;
                }
                if (direction < 0 && difference > 0f)
                {
                    continue;
                }
                float distance = MathF.Abs(difference);
                if (nearest is null || distance < nearestDistance)
                {
                    nearest = floor;
                    nearestDistance = distance;
                }
            }
            return nearest;
        }
    }
}

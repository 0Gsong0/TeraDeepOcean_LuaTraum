using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 外呼面板与电梯主控制器的运行时绑定表。
    /// </summary>
    public static class TLElevatorPanelRegistry
    {
        /// <summary>
        /// 一个面板的绑定信息。
        /// 同时记录布局和楼层。
        /// </summary>
        public sealed class PanelBinding
        {
            public TLElevatorControllerComponent Controller { get; }
            public TLElevatorLayoutBuilder.ElevatorLayout Layout { get; }
            public TLElevatorLayoutBuilder.FloorEntry Floor {  get; }
            public PanelBinding(TLElevatorControllerComponent controller, TLElevatorLayoutBuilder.ElevatorLayout layout, TLElevatorLayoutBuilder.FloorEntry floor)
            {
                Controller = controller;
                Layout = layout;
                Floor = floor;
            }
        }
        /// <summary>
        /// 已绑定的 面板组件对象 -》面板的绑定信息
        /// </summary>
        private static readonly Dictionary<TLElevatorCallPanelComponent, PanelBinding> bindings = new();
        private static GameSession? currentSession;
        private static Level? currentLevel;

        /// <summary>
        /// 登记一台电梯布局中的全部外呼面板。
        ///
        /// 先检查全部面板，再一次性写入。
        /// 任意面板已被其他有效电梯认领时，整次登记失败。
        ///
        /// 调用前，主控制器的 Layout 必须已指向这份布局。
        /// </summary>
        public static bool TryRegister(TLElevatorLayoutBuilder.ElevatorLayout layout, out string error)
        {
            error = string.Empty;
            if (!EnsureAuthorityRound(out error)) return false;
            if(layout is null || !ReferenceEquals(layout.Controller.Layout, layout))
            {
                error = "主控制器尚未采用这份电梯布局。";
                return false;
            }
            Dictionary<TLElevatorCallPanelComponent, PanelBinding> checks = new();
            foreach (var floor in layout.Floors)
            {
                foreach (var panel in floor.Panels)
                {
                    var candidate = new PanelBinding(layout.Controller, layout, floor);
                    if(!IsBindingValid(panel, candidate))
                    {
                        error =$"外呼面板 {panel.PanelItem.ID} 的状态或配置已变化，请重新建立电梯布局。";
                        return false;
                    }
                    if (!checks.TryAdd(panel, candidate))
                    {
                        error =$"外呼面板 {panel.PanelItem.ID}在这份布局中被重复登记。";
                        return false;
                    }
                    if(bindings.TryGetValue(panel,out var panelBinding)&& IsBindingValid(panel, panelBinding) && !ReferenceEquals(panelBinding.Controller, layout.Controller))
                    {
                        error = $"外呼面板 {panel.PanelItem.ID} 已属于主控制器 " +$"{panelBinding.Controller.ControllerItem.ID}，不能同时属于另一台电梯。";
                        return false;
                    }
                }
            }
            UnregisterController(layout.Controller);
            foreach (var pair in checks)
            {
                bindings[pair.Key] = pair.Value;
            }
            return true;
        }
        /// <summary>
        /// 通过外呼面板呼叫所属电梯。
        ///
        /// 使用登记时的楼层，不接收调用方自行指定的楼层号。
        /// 未来外呼 UI 的服务器处理函数将调用此入口。
        /// </summary>
        public static bool TryCall(TLElevatorCallPanelComponent panel, out string error)
        {
            if (!TryGetBinding(panel, out var binding, out error)) return false;
            return binding!.Controller.TryRequestFloor(binding.Floor.Number, out error);
        }

        /// <summary>取得经过运行时验证的归属，供服务器生成面板信息。</summary>
        public static bool TryGetBinding(TLElevatorCallPanelComponent panel, out PanelBinding? binding, out string error)
        {
            binding = null;
            error = string.Empty;
            if (!EnsureAuthorityRound(out error)) return false;
            if (panel is null)
            {
                error = "没有提供外呼面板。";
                return false;
            }
            if (!bindings.TryGetValue(panel, out var found))
            {
                error = "此外呼面板尚未绑定到电梯主控制器。";
                return false;
            }
            if (!IsBindingValid(panel, found))
            {
                // 失效绑定不再继续参与后续呼叫。
                bindings.Remove(panel);
                error = "外呼面板的绑定已失效，请重新建立电梯。";
                return false;
            }
            binding = found;
            return true;
        }
        /// <summary>
        /// 移除指定主控制器的全部面板绑定。
        /// 地图重新加载、控制器删除或重新登记时调用。
        /// </summary>
        public static void UnregisterController(TLElevatorControllerComponent controller)
        {
            List<TLElevatorCallPanelComponent> toRemove = new();

            foreach (var pair in bindings)
            {
                if (ReferenceEquals(pair.Value.Controller, controller))
                {
                    toRemove.Add(pair.Key);
                }
            }
            foreach (var panel in toRemove)
            {
                bindings.Remove(panel);
            }
        }
        /// <summary>
        /// 检查面板、主控制器、布局和楼层是否仍然对应。
        /// </summary>
        private static bool IsBindingValid(TLElevatorCallPanelComponent panel, PanelBinding binding)
        {
            Item panelItem = panel.PanelItem;
            Item controllerItem = binding.Controller.ControllerItem;
            if (panelItem.Removed ||
                controllerItem.Removed ||
                panelItem.ParentInventory is not null ||
                controllerItem.ParentInventory is not null)
            {
                return false;
            }
            // 防止实体删除、ID 重用或布局重新建立后继续使用旧引用。
            if (!ReferenceEquals(Entity.FindEntityByID(panelItem.ID), panelItem) ||
                !ReferenceEquals(Entity.FindEntityByID(controllerItem.ID), controllerItem) ||
                !ReferenceEquals(binding.Controller.Layout, binding.Layout))
            {
                return false;
            }
            if (!ReferenceEquals(panelItem.Submarine,binding.Layout.CoordinateSubmarine) ||
                !ReferenceEquals(controllerItem.Submarine,binding.Layout.CoordinateSubmarine))
            {
                return false;
            }
            // 楼层配置在运行中发生变化时，不静默改呼另一个楼层。
            if (panel.Floor != binding.Floor.Number)
            {
                return false;
            }
            float stopY = panel.GetStopLocalY();
            return float.IsFinite(stopY) && MathF.Abs(stopY - binding.Floor.StopLocalY) <= TLElevatorLayoutBuilder.FloorHeightTolerance;
        }
        /// <summary>
        /// 限定为单人或服务器的运行回合。
        /// 会话或关卡改变时，清除上一回合的绑定。
        /// 不要求 Level 非空，因此支持编辑器测试回合。
        /// </summary>
        private static bool EnsureAuthorityRound(out string error)
        {
            error = string.Empty;
            if (!TLLinkedMotionSystem.IsAuthority)
            {
                error = "外呼请求需要由服务器处理。";
                return false;
            }
            GameSession? session = GameMain.GameSession;
            if (session is null || !session.IsRunning)
            {
                Clear();
                error = "当前没有运行中的回合。";
                return false;
            }
            Level? level = session.Level;
            if (!ReferenceEquals(currentSession, session) || !ReferenceEquals(currentLevel, level))
            {
                Clear();
                currentSession = session;
                currentLevel = level;
            }
            return true;
        }
        /// <summary>
        /// 清空全部运行时绑定及回合引用。
        /// </summary>
        public static void Clear()
        {
            bindings.Clear();
            currentSession = null;
            currentLevel = null;
        }
    }
}

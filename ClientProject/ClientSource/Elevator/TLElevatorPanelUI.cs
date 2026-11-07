using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace TeraDeepOcean
{
    /// <summary>
    /// 两种面板共用的最小测试界面。
    /// 只显示服务器返回的可用楼层和已请求标记，不在客户端推断布局。
    /// </summary>
    internal sealed class TLElevatorPanelUI : IDisposable
    {
        /// <summary>本界面对应的物品。</summary>
        private readonly Item item;
        /// <summary>外呼只呼叫本站；主控允许选择全部登记楼层。</summary>
        private readonly bool isCallPanel;
        /// <summary>全部内容的容器，销毁时整体移除。</summary>
        private readonly GUILayoutGroup content;
        /// <summary>可滚动的楼层按钮列表。</summary>
        private readonly GUIListBox floorList;
        /// <summary>操作反馈。</summary>
        private readonly GUITextBlock status;
        /// <summary>当前楼层按钮，用于等待期间禁用点击。</summary>
        private readonly List<GUIButton> floorButtons = new();
        /// <summary>当前待处理的网络请求号。</summary>
        private uint pendingId;
        /// <summary>是否等待请求结果。</summary>
        private bool waiting;
        /// <summary>本地界面等待的游戏时间。</summary>
        private float waitTime;
        /// <summary>避免组件删除后回调继续操作控件。</summary>
        private bool disposed;
        /// <summary>本地等待上限，不代表服务器一定未接受超时请求。</summary>
        private const float TimeoutSeconds = 10f;

        /// <summary>在原版 GuiFrame 内放置标题、按钮列表、刷新和关闭按钮。</summary>
        public TLElevatorPanelUI(Item item, GUIFrame frame, bool isCallPanel)
        {
            this.item = item;
            this.isCallPanel = isCallPanel;
            content = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.88f), frame.RectTransform, Anchor.Center))
            {
                RelativeSpacing = 0.02f
            };
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.12f), content.RectTransform),
                isCallPanel ? "电梯外呼" : "电梯选层", textAlignment: Alignment.Center);
            status = new GUITextBlock(new RectTransform(new Vector2(1f, 0.20f), content.RectTransform),
                "请选择面板以读取楼层。", textAlignment: Alignment.Center, wrap: true);
            floorList = new GUIListBox(new RectTransform(new Vector2(1f, 0.46f), content.RectTransform));
            var row = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.14f), content.RectTransform), isHorizontal: true)
            {
                RelativeSpacing = 0.04f
            };
            var refresh = new GUIButton(new RectTransform(new Vector2(0.48f, 1f), row.RectTransform), "刷新");
            refresh.OnClicked = (_, _) => { if (!waiting) Send(false, 0); return true; };
            var close = new GUIButton(new RectTransform(new Vector2(0.48f, 1f), row.RectTransform), "关闭");
            close.OnClicked = (_, _) => { Close(); return true; };
        }

        /// <summary>每次选择物品时重新查询，丢弃上次打开界面遗留的等待。</summary>
        public void Open()
        {
            TLElevatorUiNetwork.Cancel(pendingId);
            pendingId = 0;
            waiting = false;
            Send(false, 0);
        }

        /// <summary>原版在界面显示时调用；离开交互范围则取消选择。</summary>
        public void Update(Character character, float deltaTime)
        {
            if (disposed || !ReferenceEquals(character, Character.Controlled)) return;
            if (GameMain.GameSession is not { IsRunning: true } || character.Removed || character.IsDead ||
                item.Removed || item.ParentInventory is not null || !character.CanInteractWith(item))
            {
                Close();
                return;
            }
            if (waiting && float.IsFinite(deltaTime) && deltaTime > 0f)
            {
                waitTime += deltaTime;
                if (waitTime >= TimeoutSeconds)
                {
                    TLElevatorUiNetwork.Cancel(pendingId);
                    waiting = false;
                    status.Text = "等待超时，可刷新。此前请求可能已经被接受。";
                    status.TextColor = Color.Orange;
                }
            }
            RefreshButtonState();
        }

        /// <summary>查询或提交。回调可能同步完成，所以先设置等待状态。</summary>
        private void Send(bool submit, int floor)
        {
            if (disposed || waiting) return;
            TLElevatorUiNetwork.Cancel(pendingId);
            waiting = true;
            waitTime = 0f;
            status.Text = submit ? "正在提交……" : "正在读取楼层……";
            status.TextColor = Color.White;
            RefreshButtonState();
            pendingId = TLElevatorUiNetwork.Request(item, isCallPanel, submit, floor,
                reply => ApplyReply(reply, submit));
        }

        /// <summary>用服务器回执更新按钮和标记；接受请求不等于电梯到达。</summary>
        private void ApplyReply(TLElevatorPanelReply reply, bool submitted)
        {
            if (disposed) return;
            waiting = false;
            status.Text = reply.Accepted
                ? (submitted ? "请求已接受。" : "请选择楼层。") + "\n" + reply.Status
                : reply.Error;
            status.TextColor = reply.Accepted ? Color.LightGreen : Color.OrangeRed;
            floorList.Content.ClearChildren();
            floorButtons.Clear();
            var requested = new HashSet<int>(reply.RequestedFloors);
            foreach (int number in reply.Floors)
            {
                int selectedFloor = number;
                string label = isCallPanel ? $"呼叫电梯（本层 {number}）" : $"楼层 {number}";
                if (requested.Contains(number)) label += "  [已请求]";
                var button = new GUIButton(new RectTransform(new Vector2(1f, 0f), floorList.Content.RectTransform)
                {
                    MinSize = new Point(0, 42)
                }, label);
                button.OnClicked = (_, _) => { Send(true, selectedFloor); return true; };
                floorButtons.Add(button);
            }
            RefreshButtonState();
        }

        /// <summary>等待时禁用楼层按钮，回调或超时后恢复。</summary>
        private void RefreshButtonState()
        {
            foreach (var button in floorButtons) button.Enabled = !waiting;
        }

        /// <summary>关闭原版物品选择，取消本地等待，但不清空电梯请求队列。</summary>
        private void Close()
        {
            TLElevatorUiNetwork.Cancel(pendingId);
            waiting = false;
            var character = Character.Controlled;
            if (character is not null && ReferenceEquals(character.SelectedItem, item)) character.SelectedItem = null;
            if (character is not null && ReferenceEquals(character.SelectedSecondaryItem, item)) character.SelectedSecondaryItem = null;
        }

        /// <summary>GUI 重建或组件删除时释放控件和回调。</summary>
        public void Dispose()
        {
            disposed = true;
            TLElevatorUiNetwork.Cancel(pendingId);
            content.RectTransform.Parent = null;
            floorButtons.Clear();
        }
    }

    /// <summary>外呼面板的客户端界面接入，基准线绘制保留在原文件。</summary>
    public sealed partial class TLElevatorCallPanelComponent
    {
        /// <summary>当前客户端界面。</summary>
        private TLElevatorPanelUI? interactionUi;
        /// <summary>分辨率变化后让原版重建 GUI。</summary>
        public override bool RecreateGUIOnResolutionChange => true;
        /// <summary>组件创建时建立控件。</summary>
        partial void InitializeInteractionUI() => CreateGUI();
        /// <summary>组件移除时释放界面。</summary>
        partial void ReleaseInteractionUI() { interactionUi?.Dispose(); interactionUi = null; }
        /// <summary>本地玩家选择后查询服务器绑定的本站楼层。</summary>
        partial void OnInteractionSelected(Character character)
        {
            if (ReferenceEquals(character, Character.Controlled)) interactionUi?.Open();
        }
        /// <summary>在 XML 配置的 GuiFrame 中创建外呼界面。</summary>
        public override void CreateGUI()
        {
            ReleaseInteractionUI();
            if (GuiFrame is not null) interactionUi = new TLElevatorPanelUI(item, GuiFrame, true);
        }
        /// <summary>让共用界面处理交互范围和超时。</summary>
        public override void UpdateHUDComponentSpecific(Character character, float deltaTime, Camera cam) => interactionUi?.Update(character, deltaTime);
    }

    /// <summary>轿厢主控制器的客户端选层界面。</summary>
    public partial class TLElevatorControllerComponent
    {
        /// <summary>当前客户端选层界面。</summary>
        private TLElevatorPanelUI? interactionUi;
        /// <summary>分辨率变化后重建 GUI。</summary>
        public override bool RecreateGUIOnResolutionChange => true;
        /// <summary>组件创建时建立控件。</summary>
        partial void InitializeInteractionUI() => CreateGUI();
        /// <summary>组件移除时清理界面。</summary>
        partial void ReleaseInteractionUI() { interactionUi?.Dispose(); interactionUi = null; }
        /// <summary>本地玩家打开面板时读取服务器登记的楼层。</summary>
        partial void OnInteractionSelected(Character character)
        {
            if (ReferenceEquals(character, Character.Controlled)) interactionUi?.Open();
        }
        /// <summary>建立轿厢选层界面。</summary>
        public override void CreateGUI()
        {
            ReleaseInteractionUI();
            if (GuiFrame is not null) interactionUi = new TLElevatorPanelUI(item, GuiFrame, false);
        }
        /// <summary>更新当前可见界面的交互检查。</summary>
        public override void UpdateHUDComponentSpecific(Character character, float deltaTime, Camera cam) => interactionUi?.Update(character, deltaTime);
    }
}

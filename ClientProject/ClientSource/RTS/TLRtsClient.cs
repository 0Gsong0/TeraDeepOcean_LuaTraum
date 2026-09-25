using FluentResults;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeraDeepOcean
{
    /// <summary>
    /// RTS 客户端输入、FreeCam 和界面表现。
    ///
    /// F8：
    ///     进入/退出 RTS。
    ///
    /// 左键：
    ///     单选或框选单位。
    ///
    /// Shift + 左键：
    ///     添加/移除单个单位。
    ///
    /// Shift + 框选：
    ///     将框内单位添加到当前选择。
    ///
    /// 右键地面：
    ///     移动/编队命令。
    ///
    /// 右键敌人：
    ///     强制攻击命令。
    ///
    /// Escape：
    ///     只清空选择，不退出 RTS。
    /// </summary>
    public static class TLRtsClient
    {
        private const Keys ToggleKey = Keys.F8;

        /// <summary>
        /// 鼠标移动超过该屏幕像素距离后，单击变为框选。
        /// </summary>
        private const float DragThreshold = 8.0f;

        private const float CommandMarkerDuration = 1.35f;
        private const int MinimumCharacterBoxSize = 34;
        private const int CharacterBoxPadding = 12;

        private static readonly HashSet<ushort> selectedCharacterIds = new();

        private static bool initialized;

        public static bool IsActive { get; private set; }

        /// <summary>
        /// 进入 RTS 前由玩家控制的角色。
        /// 弱引用可以避免回合结束后阻止旧角色被释放。
        /// </summary>
        private static WeakReference<Character>? previousControlledCharacter;

        /// <summary>
        /// RTS 模式中的指挥官阵营。
        /// Character.Controlled 被设为 null 后，不能再从它读取阵营。
        /// </summary>
        private static CharacterTeamType? commanderTeam;

        private static bool previousPreventPauseMenuToggle;

        // 左键框选状态。
        private static bool selecting;
        private static Vector2 selectionStart;
        private static Vector2 selectionCurrent;

        /// <summary>
        /// 可以申请 RTS 指挥权的设备标签。
        /// 最终合法性仍由服务器验证。
        /// </summary>
        private static readonly Identifier CommandDeviceTag = "rtscommanddevice".ToIdentifier();
        private static GUIButton? commanderButton;
        /// <summary>
        /// 是否已向服务器请求过当前指挥官状态。
        /// </summary>
        private static bool commanderSnapshotRequested;
        private static float commanderSnapshotRetryTimer;
        /// <summary>
        /// 单人进入 RTS 前，原角色 AI 的启用状态。
        /// 退出 RTS 时恢复原值，避免破坏角色原本状态。
        /// </summary>
        private static bool singleplayerCommanderAiStateCaptured;
        private static bool singleplayerCommanderAiWasEnabled;
        /// <summary>
        /// 客户端最后处理的服务器命令结果序号。
        /// 防止旧结果覆盖新命令的标记。
        /// </summary>
        private static int lastHandledCommandResultSequence;

        /// <summary>
        /// 清除全部 RTS 命令、恢复原版 AI 的按钮。
        /// </summary>
        private static GUIButton? releaseAllAiButton;
        private enum CommandMarkerType
        {
            None,
            Move,
            Attack
        }

        private static CommandMarkerType commandMarkerType;
        private static float commandMarkerTimer;
        private static Vector2 commandWorldPosition;
        private static ushort commandTargetCharacterId = Entity.NullEntityID;
        private static readonly List<Vector2> commandSlots = new();

        public static IReadOnlyCollection<ushort> SelectedCharacterIds =>
            selectedCharacterIds;
        public static void Init(Harmony harmony)
        {
            if (initialized)
            {
                return;
            }

            initialized = true;

            TLRtsSystem.MoveOrderIssued += OnMoveOrderIssued;
            TLRtsSystem.RoundStateCleared += OnRoundStateCleared;
            // 服务器广播指挥官变化时通知客户端 UI。
            TLRtsNetwork.CommanderStateChanged += OnCommanderStateChanged;
            // 服务器单独回复本客户端的申请结果。
            TLRtsNetwork.ClaimResultReceived += OnClaimResultReceived;
            // 服务器返回移动或攻击命令的权威处理结果。
            TLRtsNetwork.CommandResultReceived += OnCommandResultReceived;

            var guiDraw = AccessTools.Method(
                typeof(GUI),
                nameof(GUI.Draw),
                new[]
                {
                    typeof(Camera),
                    typeof(SpriteBatch)
                });
            var gameScreenAddToGui = AccessTools.Method(typeof(GameScreen), nameof(GameScreen.AddToGUIUpdateList));
            if (guiDraw != null)
            {
                harmony.Patch(
                    guiDraw,
                    postfix: new HarmonyMethod(
                        typeof(TLRtsClient),
                        nameof(AfterGuiDraw)));
            }
            if (gameScreenAddToGui != null)
            {
                harmony.Patch(
                    gameScreenAddToGui,
                    postfix: new HarmonyMethod(
                        typeof(TLRtsClient),
                        nameof(AfterGameScreenAddToGUIUpdateList)));
            }
        }
        public static void Update(float deltaTime)
        {
            UpdateCommanderSnapshotRequest(deltaTime);
            if (commandMarkerTimer > 0.0f)
            {
                commandMarkerTimer -= deltaTime;

                if (commandMarkerTimer <= 0.0f)
                {
                    ClearCommandMarker();
                }
            }
            RemoveInvalidSelections();
            if (!GUI.InputBlockingMenuOpen && GUI.KeyboardDispatcher.Subscriber == null && PlayerInput.KeyHit(ToggleKey))
            {
                if (IsActive)
                {
                    ExitRtsMode(restoreControlledCharacter: true);
                }
                else
                {
                    DebugLocalHeldItems();
                    TryEnterRtsMode();
                }

                return;
            }
            if (!IsActive)
            {
                return;
            }
            if (!CanRemainInRtsMode())
            {
                ExitRtsMode(restoreControlledCharacter: true);
                return;
            }
            if (Character.Controlled != null)
            {
                Character.Controlled = null;
            }
            if (!GameMain.IsMultiplayer)
            {
                //单人模式下，FreeCam 期间持续清除原角色的残留输入。
                KeepSingleplayerCommanderIdle();
            }
            Camera cam = GameMain.GameScreen.Cam;
            cam.TargetPos = Vector2.Zero;
            /*
             * GameMain 会在 GUI.Update 之前处理 Escape。
             * 因为进入 RTS 时已经设置 PreventPauseMenuToggle，
             * Escape 不会打开暂停菜单。
             */
            if (PlayerInput.KeyHit(Keys.Escape))
            {
                ClearSelection();
                CancelSelectionDrag();
                return;
            }
            if (GameMain.Instance.Paused ||
             GUI.InputBlockingMenuOpen ||
             GUI.KeyboardDispatcher.Subscriber != null)
            {
                CancelSelectionDrag();
                return;
            }

            HandleLeftMouse(cam);
            HandleRightMouse(cam);
        }
        private static void AfterGameScreenAddToGUIUpdateList()
        {
            UpdateCommanderButton();
            if (commanderButton != null && commanderButton.Visible)
            {
                commanderButton.AddToGUIUpdateList(order: 10);
            }            
            UpdateReleaseAllAiButton();
            if (releaseAllAiButton != null && releaseAllAiButton.Visible)
            {
                releaseAllAiButton.AddToGUIUpdateList(order: 11);
            }
        }
        /// <summary>
        /// 更新“释放全部 AI”按钮。
        /// 只有进入 RTS 模式并且拥有指挥权限时显示。
        /// </summary>
        private static void UpdateReleaseAllAiButton()
        {
            EnsureReleaseAllAiButton();

            if (releaseAllAiButton == null)
            {
                return;
            }

            bool hasAuthority =
                GameMain.IsMultiplayer
                    ? TLRtsNetwork.IsLocalCommander
                    : TLRtsSystem.IsAuthority;

            releaseAllAiButton.Visible =
                IsActive &&
                hasAuthority &&
                Screen.Selected == GameMain.GameScreen &&
                GameMain.GameSession != null &&
                Level.IsLoadedOutpost &&
                !GUI.DisableHUD;

            releaseAllAiButton.Enabled =
                releaseAllAiButton.Visible;
        }

        /// <summary>
        /// 创建“释放全部 AI”按钮。
        /// 按钮位于屏幕顶部 RTS 状态栏下方。
        /// </summary>
        private static void EnsureReleaseAllAiButton()
        {
            if (releaseAllAiButton != null)
            {
                return;
            }

            releaseAllAiButton = new GUIButton(
                new RectTransform(
                    new Point(220, 40),
                    GUI.Canvas,
                    Anchor.TopCenter)
                {
                    ScreenSpaceOffset =
                        new Point(0, 78)
                },
                "释放全部 AI",
                Alignment.Center,
                style: "GUIButton");

            releaseAllAiButton.Visible = false;

            releaseAllAiButton.OnClicked = (_, _) =>
            {
                ShowReleaseAllAiConfirmation();
                return true;
            };
        }
        private static void ShowReleaseAllAiConfirmation()
        {
            if (!IsActive) return;
            GUIMessageBox box = new("RTS 战术系统", "确认释放全部 RTS 单位吗？\n\n" + "所有单位将停止执行移动、防守和强制攻击命令，" + "并重新交给原版 AI 控制。", new LocalizedString[]
            {
                "确定",
                "取消"
            });
            // 确保确认窗口绘制在 RTS 界面和其他 GUI 上层。
            box.DrawOnTop = true;
            // “确定”按钮。
            box.Buttons[0].OnClicked = (_, _) =>
            {
                /*
                 * 先关闭确认框，再执行释放。
                 * ReleaseAllAiFromRts 内部还会再次检查当前模式和网络权限。
                 */
                box.Close();
                ReleaseAllAiFromRts();
                return true;
            };
            // “取消”按钮。
            box.Buttons[1].OnClicked = (_, _) =>
            {
                // 只关闭窗口
                box.Close();
                return true;
            };
        }
        /// <summary>
        /// 单人直接释放；多人向服务器发送权威请求。
        /// </summary>
        private static void ReleaseAllAiFromRts()
        {
            if (!IsActive)
            {
                return;
            }

            if (GameMain.IsMultiplayer)
            {
                bool sent =
                    TLRtsNetwork.ClientRequestReleaseAllAi();

                if (!sent)
                {
                    GUI.AddMessage(
                        "无法向服务器发送释放 AI 请求。",
                        Color.OrangeRed);
                }

                return;
            }

            int releasedCount =
                TLRtsSystem.ReleaseAllUnitsToVanillaAi();

            ClearCommandMarker();

            GUI.AddMessage(
                $"已释放 {releasedCount} 个 RTS 单位，原版 AI 已恢复。",
                Color.LightGreen);
        }
        /// <summary>
        /// 按钮更新逻辑
        /// </summary>
        private static void UpdateCommanderButton()
        {
            bool shouldExistInCurrentView = GameMain.IsMultiplayer && Screen.Selected == GameMain.GameScreen && GameMain.GameSession != null && Level.IsLoadedOutpost && !GUI.DisableHUD;

            if (!shouldExistInCurrentView)
            {
                if (commanderButton != null)
                    commanderButton.Visible = false;
                return;
            }
            EnsureCommanderButton();
            if (commanderButton == null) return;
            Item? device = FindHeldCommandDevice();
            // 只有实际手持设备时才显示。
            commanderButton.Visible = device != null;
            if (device == null) return;
            if (TLRtsNetwork.IsLocalCommander)
            {
                commanderButton.Enabled = true;
                commanderButton.Text = "放弃 RTS 指挥权";
                return;
            }
            if (TLRtsNetwork.CommanderState.HasCommander)
            {
                commanderButton.Enabled = false;
                commanderButton.Text = $"当前指挥官：{TLRtsNetwork.CommanderState.CommanderName}";
                return;
            }
            commanderButton.Enabled = true;
            commanderButton.Text = "申请成为 RTS 指挥官";
        }
        /// <summary>
        /// 按钮创建逻辑
        /// </summary>
        private static void EnsureCommanderButton()
        {
            if (commanderButton != null)
            {
                return;
            }

            commanderButton = new GUIButton(
                new RectTransform(
                    new Point(280, 44),
                    GUI.Canvas,
                    Anchor.BottomCenter)
                {
                    /*
                     * BottomCenter 位于屏幕底部。
                     * 负 Y 将按钮向屏幕上方移动。
                     */
                    ScreenSpaceOffset = new Point(0, -115)
                },
                "申请成为 RTS 指挥官",
                Alignment.Center,
                style: "GUIButton");

            commanderButton.Visible = false;

            commanderButton.OnClicked = (_, _) =>
            {
                if (TLRtsNetwork.IsLocalCommander)
                {
                    ShowReleaseCommanderConfirmation();
                }
                else if (!TLRtsNetwork.CommanderState.HasCommander)
                {
                    ShowClaimCommanderConfirmation();
                }

                return true;
            };
        }
        /// <summary>
        /// 获取本地 RTS 指挥角色。
        /// 多人模式优先使用 MyClient.Character：它会根据服务器同步的 CharacterID 查找当前角色，
        /// </summary>
        /// <returns></returns>
        private static Character? GetLocalCommanderCharacter()
        {
            if (GameMain.IsMultiplayer)
            {
                // 多人本地客户端对应的权威角色引用。
                Character? networkCharacter = GameMain.Client?.MyClient?.Character;
                if (IsUsableCommanderCharacter(networkCharacter)) return networkCharacter;
                // 正常控制角色作为第一备用。
                if (IsUsableCommanderCharacter(Character.Controlled)) return Character.Controlled;
                // GameClient 的缓存角色作为最后备用。
                Character? cachedCharacter = GameMain.Client?.Character;
                if (IsUsableCommanderCharacter(cachedCharacter)) return cachedCharacter;
            }
            else
            {
                if (IsUsableCommanderCharacter(Character.Controlled)) return Character.Controlled;
            }
            // 进入 FreeCam 后使用进入前保存的角色。
            if (previousControlledCharacter != null && previousControlledCharacter.TryGetTarget(out Character? previous) && IsUsableCommanderCharacter(previous)) return previous;
            return null;
        }
        private static bool IsUsableCommanderCharacter(Character? character)
        {
            return character != null &&
                   !character.Removed &&
                   !character.IsDead &&
                   character.Inventory != null;
        }
        /// <summary>
        /// 只检查左右手槽。
        /// </summary>
        /// <returns></returns>
        private static Item? FindHeldCommandDevice()
        {
            Character? character = GetLocalCommanderCharacter();
            if (character == null || character.Removed || character.IsDead || character.Inventory == null) return null;
            return character.HeldItems.FirstOrDefault(item => !item.Removed && item.HasTag(CommandDeviceTag));
        }
        /// <summary>
        /// 申请确认窗口
        /// </summary>
        private static void ShowClaimCommanderConfirmation()
        {
            Item? device = FindHeldCommandDevice();

            if (device == null)
            {
                GUI.AddMessage(
                    "请先把 RTS 指挥设备拿在手中。",
                    Color.OrangeRed);

                return;
            }

            GUIMessageBox box = new(
                "RTS 战术系统",
                "确认申请成为本回合唯一的 RTS 指挥官吗？",
                new LocalizedString[]
                {
            "确认",
            "取消"
                });

            box.DrawOnTop = true;

            box.Buttons[0].OnClicked = (_, _) =>
            {
                /*
                 * 玩家打开窗口后可能已经丢弃或切换了物品，
                 * 所以确认时再次检查。
                 */
                Item? currentDevice =
                    FindHeldCommandDevice();

                if (currentDevice == null ||
                    currentDevice.ID != device.ID)
                {
                    GUI.AddMessage(
                        "RTS 指挥设备已经不在手中。",
                        Color.OrangeRed);

                    ClientDebug(
                        "申请取消：确认时已不再持有原指挥设备。");
                }
                else
                {
                    bool sent =
                        TLRtsNetwork.ClientRequestCommander(
                            currentDevice);

                    ClientDebug(
                        sent
                            ? $"已发送指挥官申请，设备 ID={currentDevice.ID}。"
                            : "指挥官申请发送失败，Lua 网络桥可能尚未就绪。");
                }

                box.Close();
                return true;
            };

            box.Buttons[1].OnClicked = (_, _) =>
            {
                box.Close();
                return true;
            };
        }
        /// <summary>
        /// 释放确认窗口
        /// </summary>
        private static void ShowReleaseCommanderConfirmation()
        {
            GUIMessageBox box = new(
                "RTS 战术系统",
                "确认放弃当前 RTS 指挥权吗？",
                new LocalizedString[]
                {
            "确认",
            "取消"
                });

            box.DrawOnTop = true;

            box.Buttons[0].OnClicked = (_, _) =>
            {
                bool sent =
                    TLRtsNetwork.ClientRequestRelease();

                ClientDebug(
                    sent
                        ? "已向服务器发送释放指挥权请求。"
                        : "释放请求发送失败，Lua 网络桥可能尚未就绪。");

                box.Close();
                return true;
            };

            box.Buttons[1].OnClicked = (_, _) =>
            {
                box.Close();
                return true;
            };
        }
        /// <summary>
        /// 服务器状态事件
        /// </summary>
        /// <param name="state"></param>
        private static void OnCommanderStateChanged(TLRtsCommanderState state)
        {
            ClientDebug(
                $"收到指挥官状态：" +
                $"HasCommander={state.HasCommander}, " +
                $"CharacterId={state.CommanderCharacterId}, " +
                $"DeviceId={state.DeviceItemId}, " +
                $"Name={state.CommanderName}, " +
                $"Revision={state.Revision}");
            if (GameMain.IsMultiplayer && IsActive && !TLRtsNetwork.IsLocalCommander)
            {
                GUI.AddMessage("RTS 指挥权已经失效，正在退出战术模式。", Color.OrangeRed);
                ExitRtsMode(restoreControlledCharacter: true);
            }
            if (state.HasCommander)
            {
                if (TLRtsNetwork.IsLocalCommander)
                {
                    GUI.AddMessage(
                        "你是当前 RTS 指挥官。",
                        Color.DeepSkyBlue);
                }
                else
                {
                    GUI.AddMessage(
                        $"当前 RTS 指挥官：{state.CommanderName}",
                        Color.LightGray);
                }
            }
            else
            {
                GUI.AddMessage(
                    "当前没有 RTS 指挥官。",
                    Color.LightGray);
            }
        }
        /// <summary>
        /// 服务器状态事件
        /// </summary>
        /// <param name="result"></param>
        private static void OnClaimResultReceived(TLRtsClaimResult result)
        {
            ClientDebug(
                $"收到申请结果：" +
                $"Accepted={result.Accepted}, " +
                $"Reason={result.Reason}");

            if (result.Accepted)
            {
                GUI.AddMessage(
                    "RTS 指挥官申请成功。",
                    Color.DeepSkyBlue);
            }
            else
            {
                GUI.AddMessage(
                    string.IsNullOrWhiteSpace(result.Reason)
                        ? "RTS 指挥官申请被服务器拒绝。"
                        : result.Reason,
                    Color.OrangeRed);
            }
        }
        /// <summary>
        /// 处理服务器返回的移动或攻击结果。
        ///
        /// 只有服务器接受命令后，客户端才显示标记。
        /// </summary>
        private static void OnCommandResultReceived(TLRtsCommandResult result)
        {
            if (result == null || !IsActive) return;
            /*
             * 防止延迟到达的旧结果覆盖新命令标记。
             */
            if (result.Sequence <= lastHandledCommandResultSequence)
            {
                return;
            }
            lastHandledCommandResultSequence = result.Sequence;
            if (!result.Accepted)
            {
                GUI.AddMessage(
                    string.IsNullOrWhiteSpace(
                        result.Reason)
                        ? "服务器拒绝了 RTS 命令。"
                        : result.Reason,
                    Color.OrangeRed);

                return;
            }
            switch (result.CommandType)
            {
                case TLRtsNetCommandType.Move:
                    ShowServerMoveResult(result);
                    break;

                case TLRtsNetCommandType.Attack:
                    ShowServerAttackResult(result);
                    break;

                case TLRtsNetCommandType.ReleaseAllAi:
                    ClearCommandMarker();

                    GUI.AddMessage(
                        $"已释放 {result.ReleasedUnitCount} 个 RTS 单位，原版 AI 已恢复。",
                        Color.LightGreen);

                    break;
            }
        }
        private static void ClientDebug(string message)
        {
            DebugConsole.NewMessage(
                $"[TLRTS][CLIENT] {message}",
                Color.Cyan);
        }
        /// <summary>
        /// 客户端进入前哨回合后，向服务器请求一次当前指挥官状态。
        ///
        /// 如果 Lua 网络桥还没加载完成，请求会返回 false，
        /// 一秒后自动重试。
        /// </summary>
        private static void UpdateCommanderSnapshotRequest(
            float deltaTime)
        {
            if (!GameMain.IsMultiplayer ||
                GameMain.NetworkMember is not { IsClient: true } ||
                GameMain.GameSession == null ||
                !Level.IsLoadedOutpost)
            {
                return;
            }

            if (commanderSnapshotRequested)
            {
                return;
            }

            commanderSnapshotRetryTimer -= deltaTime;

            if (commanderSnapshotRetryTimer > 0.0f)
            {
                return;
            }

            commanderSnapshotRetryTimer = 1.0f;

            bool sent = TLRtsNetwork.ClientRequestSnapshot();

            ClientDebug(
                sent
                    ? "已向服务器请求当前指挥官状态。"
                    : "指挥官状态请求尚未发出，将在一秒后重试。");

            commanderSnapshotRequested = sent;
        }
        private static bool TryEnterRtsMode()
        {
            if (Screen.Selected != GameMain.GameScreen || GameMain.GameSession == null || !Level.IsLoadedOutpost)
            {
                GUI.AddMessage("RTS 战术模式只能在前哨站回合中开启。", Color.OrangeRed);
                return false;
            }
            /*
             * 多人必须先通过按钮取得服务器指挥权。
             * 单人则继续使用本地 RTS 权限。
             */
            if (GameMain.IsMultiplayer)
            {
                if (!TLRtsNetwork.IsLocalCommander)
                {
                    Item? heldDevice = FindHeldCommandDevice();
                    string reason;
                    if (TLRtsNetwork.CommanderState.HasCommander)
                    {
                        reason = $"当前 RTS 指挥官是：" + $"{TLRtsNetwork.CommanderState.CommanderName}";
                    }
                    else if (heldDevice == null)
                    {
                        reason = "必须先把 RTS 指挥终端装备到手中。";
                    }
                    else
                    {
                        reason = "已经检测到 RTS 指挥终端，请点击申请按钮取得指挥权。";
                    }
                    GUI.AddMessage(reason, Color.OrangeRed);
                    return false;
                }
            }
            else if (!TLRtsSystem.IsAuthority)
            {
                GUI.AddMessage("当前客户端没有 RTS 控制权限。", Color.OrangeRed);
                return false;
            }
            //单人和多人现在都必须实际手持设备。
            Item? commandDevice = FindHeldCommandDevice();
            if (commandDevice == null)
            {
                GUI.AddMessage("必须把 RTS 指挥终端放在手中。", Color.OrangeRed);
                return false;
            }
            if (!TLRtsRoundSubContext.Refresh())
            {
                GUI.AddMessage("没有找到有效的前哨站停靠网络。", Color.OrangeRed);
                return false;
            }
            Character? controlled = GetLocalCommanderCharacter();
            if (controlled == null || controlled.Removed || controlled.IsDead || controlled.IsIncapacitated)
            {
                GUI.AddMessage("当前角色不可用，无法进入 RTS 模式。", Color.OrangeRed);
                return false;
            }
            if (controlled.Submarine == null || !TLRtsRoundSubContext.Contains(controlled.Submarine))
            {
                GUI.AddMessage("当前角色不在前哨站或已连接潜艇中。", Color.OrangeRed);
                return false;
            }
            previousControlledCharacter = new WeakReference<Character>(controlled);
            commanderTeam = controlled.TeamID;
            if (!GameMain.IsMultiplayer)
            {
                //原玩家角色绝对不能成为可选择 RTS 单位
                TLRtsUnitRegistryPermission.Unregister(controlled);
                //扫描Bot
                int registeredCrewCount = TLRtsSystem.RegisterCurrentCrewBots();
                //扫描非Human角色
                int registeredCreatureCount = TLRtsSystem.RegisterCurrentTeamCreatures(controlled.TeamID);
                //保存并禁用原角色 AI。
                SuppressSingleplayerCommanderAi(controlled);
                ClientDebug(
                    $"RTS 单位扫描完成：" +
                    $"人类 Bot={registeredCrewCount}，" +
                    $"同阵营生物={registeredCreatureCount}");
            }
            Character.Controlled = null;
            ClearCommandMarker();
            ClearSelection();
            CancelSelectionDrag();
            Camera cam = GameMain.GameScreen.Cam;
            cam.TargetPos = Vector2.Zero;
            /*
             * RTS 模式中 Escape 只清空选择，不打开暂停菜单。
             */
            previousPreventPauseMenuToggle = GUI.PreventPauseMenuToggle;
            GUI.PreventPauseMenuToggle = true;
            IsActive = true;
            GUI.AddMessage(GameMain.IsMultiplayer ? "已以服务器认证指挥官身份进入 RTS 测试模式" : "已进入 单人 RTS 战术模式", Color.DeepSkyBlue);
            lastHandledCommandResultSequence = 0;
            return true;
        }
        /// <summary>
        /// 保存原角色 AI 状态，并禁止其自行行动。
        /// </summary>
        private static void SuppressSingleplayerCommanderAi(Character character)
        {
            singleplayerCommanderAiStateCaptured = false;
            singleplayerCommanderAiWasEnabled = false;
            if (character.AIController != null)
            {
                singleplayerCommanderAiWasEnabled = character.AIController.Enabled;
                singleplayerCommanderAiStateCaptured = true;
                character.AIController.Enabled = false;
                // 清除进入 RTS 前可能残留的寻路方向。
                character.AIController.SteeringManager?.Reset();
            }
            character.ClearInputs();
            character.AnimController.TargetMovement = Vector2.Zero;
        }
        /// <summary>
        /// FreeCam 期间持续保证单人原角色不会重新开始移动。
        /// </summary>
        private static void KeepSingleplayerCommanderIdle()
        {
            if (previousControlledCharacter == null || !previousControlledCharacter.TryGetTarget(out Character? character) || character.Removed || character.IsDead) return;
            /*
             * 如果其他游戏逻辑重新启用了 AI，
             * 在 RTS 模式持续期间再次将其关闭。
             */
            if (character.AIController != null)
            {
                character.AIController.Enabled = false;
                character.AIController.SteeringManager?.Reset();
            }
            character.ClearInputs();
            character.AnimController.TargetMovement = Vector2.Zero;
        }
        /// <summary>
        /// 退出 RTS 后恢复进入前记录的 AI 状态。
        /// </summary>
        private static void RestoreSingleplayerCommanderAi()
        {
            if (!singleplayerCommanderAiStateCaptured) return;
            if (previousControlledCharacter != null && previousControlledCharacter.TryGetTarget(out Character? character) && !character.Removed && character.AIController != null)
            {
                character.AIController.Enabled = singleplayerCommanderAiWasEnabled;
            }
            singleplayerCommanderAiStateCaptured = false;
            singleplayerCommanderAiWasEnabled = false;
        }
        private static void ExitRtsMode(bool restoreControlledCharacter)
        {
            if (!IsActive) return;
            IsActive = false;
            ClearSelection();
            CancelSelectionDrag();
            ClearCommandMarker();
            GUI.PreventPauseMenuToggle = previousPreventPauseMenuToggle;
            Character? restoreCharacter = null;
            if (restoreControlledCharacter)
            {
                restoreCharacter = FindCharacterToRestore();
            }
            /*
             * 必须在 previousControlledCharacter 被清空之前恢复 AI 状态。
             */
            RestoreSingleplayerCommanderAi();
            if (restoreControlledCharacter)
            {
                if (restoreCharacter != null)
                {
                    Character.Controlled = restoreCharacter;
                }
                else
                {
                    Character.Controlled = null;
                    GUI.AddMessage(GameMain.IsMultiplayer ? "原角色已经失效，无法恢复控制。" : "所有可控角色已死亡，无法恢复控制。", Color.OrangeRed);
                }
            }
            previousControlledCharacter = null;
            commanderTeam = null;
            GUI.AddMessage("已退出 RTS 战术模式", Color.LightGray);
        }
        private static Character? FindCharacterToRestore()
        {
            if (previousControlledCharacter != null &&
                previousControlledCharacter.TryGetTarget(
                    out Character? previous) &&
                CanRestoreControl(previous))
            {
                return previous;
            }

            /*
             * 多人客户端不能自行接管另一个 Bot。
             * 如果原角色已经失效，只能保持观察状态。
             */
            if (GameMain.IsMultiplayer)
            {
                return null;
            }

            // 以下备用角色接管只允许单人模式使用。
            GUI.AddMessage(
                "原角色已经死亡，正在尝试控制其它角色",
                Color.LightGray);

            return Character.CharacterList.FirstOrDefault(
                character =>
                    CanRestoreControl(character) &&
                    commanderTeam.HasValue &&
                    character.TeamID == commanderTeam.Value &&
                    character.AIController
                        is HumanAIController);
        }
        private static bool CanRestoreControl(Character? character)
        {
            return character != null &&
                   !character.Removed &&
                   !character.IsDead &&
                   !character.IsIncapacitated &&
                   character.Enabled &&
                   character.IsOnPlayerTeam;
        }
        private static bool CanRemainInRtsMode()
        {
            bool hasPermission = GameMain.IsMultiplayer ? TLRtsNetwork.IsLocalCommander : TLRtsSystem.IsAuthority;
            if (!hasPermission || Screen.Selected != GameMain.GameScreen || GameMain.GameSession == null || !Level.IsLoadedOutpost || !TLRtsRoundSubContext.IsValid) return false;
            Character? commander = GetLocalCommanderCharacter();
            if (commander == null || commander.Removed || commander.IsDead || commander.IsIncapacitated || commander.Submarine == null || !TLRtsRoundSubContext.Contains(commander.Submarine)) return false;
            /*
             * 单人和多人都要求指挥设备持续在手中。
             * 单人原角色虽然处于 FreeCam，其左右手槽仍然可以检查。
             */
            return FindHeldCommandDevice() != null;
        }
        /// <summary>
        /// 处理左键
        /// </summary>
        /// <param name="cam"></param>
        private static void HandleLeftMouse(Camera cam)
        {
            /*
             * 只允许在非 GUI 区域开始框选。
             */
            if (PlayerInput.PrimaryMouseButtonDown() &&
                GUI.MouseOn == null &&
                !Inventory.IsMouseOnInventory)
            {
                selecting = true;
                selectionStart = PlayerInput.MousePosition;
                selectionCurrent = selectionStart;
            }
            if (!selecting)
            {
                return;
            }
            selectionCurrent = PlayerInput.MousePosition;
            if (!PlayerInput.PrimaryMouseButtonClicked())
            {
                return;
            }
            bool additive =
                PlayerInput.KeyDown(Keys.LeftShift) ||
                PlayerInput.KeyDown(Keys.RightShift);
            float dragDistanceSquared =
                Vector2.DistanceSquared(
                    selectionStart,
                    selectionCurrent);
            if (dragDistanceSquared >= DragThreshold * DragThreshold)
            {
                ApplyBoxSelection(
                    cam,
                    GetSelectionRectangle(),
                    additive);
            }
            else
            {
                ApplySingleSelection(
                    cam,
                    selectionCurrent,
                    additive);
            }
            CancelSelectionDrag();
        }
        /// <summary>
        /// 单选单位
        /// </summary>
        /// <param name="cam"></param>
        /// <param name="mousePosition"></param>
        /// <param name="additive"></param>
        private static void ApplySingleSelection(Camera cam, Vector2 mousePosition, bool additive)
        {
            Character? candidate = FindSelectableCharacterAtScreenPosition(cam, mousePosition);
            if (!additive)
            {
                selectedCharacterIds.Clear();
            }
            if (candidate == null)
            {
                return;
            }
            if (additive && selectedCharacterIds.Contains(candidate.ID))
            {
                selectedCharacterIds.Remove(candidate.ID);
            }
            else
            {
                selectedCharacterIds.Add(candidate.ID);
            }
        }
        /// <summary>
        /// 框选单位
        /// </summary>
        /// <param name="cam"></param>
        /// <param name="selectionRectangle"></param>
        /// <param name="additive"></param>
        private static void ApplyBoxSelection(Camera cam, Rectangle selectionRectangle, bool additive)
        {
            if (!additive)
            {
                selectedCharacterIds.Clear();
            }
            foreach (Character character in EnumerateSelectableCharacters())
            {
                Rectangle characterBounds = GetCharacterScreenBounds(character, cam);

                if (selectionRectangle.Intersects(characterBounds))
                {
                    selectedCharacterIds.Add(character.ID);
                }
            }
        }
        /// <summary>
        /// 客户端预判目标是否可能是敌人。
        ///
        /// 这只是为了决定右键应该解释成攻击还是移动；
        /// 多人模式的最终敌我判断仍由服务器执行。
        /// </summary>
        private static bool IsLocallyHostileToAny(IEnumerable<Character> attackers, Character target)
        {
            if (target == null ||
                target.Removed ||
                target.IsDead ||
                !target.Enabled)
            {
                return false;
            }
            foreach (Character attacker in attackers)
            {
                if (attacker == null || attacker == target) continue;
                bool friendly = attacker.AIController is HumanAIController ? HumanAIController.IsFriendly(attacker, target) : attacker.IsFriendly(target);
                if (!friendly) return true;
            }
            return false;
        }
        /// <summary>
        /// 单人直接执行攻击；
        /// 多人只向服务器发送攻击请求。
        /// </summary>
        private static bool TryIssueAttackCommand(List<Character> selectedCharacters,Character target)
        {
            if (!IsLocallyHostileToAny(selectedCharacters, target)) return false;
            if (GameMain.IsMultiplayer)
            {
                return TLRtsNetwork.ClientRequestAttack(selectedCharacters, target);
            }
            bool issued = TLRtsSystem.IssueAttack(selectedCharacters, target);
            if (issued)
            {
                /*
                 * 单人没有服务器结果消息，
                 * 因此本地立即显示攻击标记。
                 */
                ShowAttackMarker(target);
            }
            return issued;
        }
        /// <summary>
        /// 单人直接执行移动；
        /// 多人只向服务器发送移动请求。
        /// </summary>
        private static bool TryIssueMoveCommand(List<Character> selectedCharacters,Vector2 targetWorldPosition)
        {
            if (GameMain.IsMultiplayer)
            {
                /*
                 * 多人移动标记不在这里立即显示。
                 * 必须等待服务器返回修正后的编队槽位。
                 */
                return TLRtsNetwork.ClientRequestMove(
                    selectedCharacters,
                    targetWorldPosition);
            }
            /*
             * 单人成功后 TLRtsSystem.MoveOrderIssued
             * 会调用 OnMoveOrderIssued 绘制标记。
             */
            return TLRtsSystem.IssueMove(
                selectedCharacters,
                targetWorldPosition);
        }
        /// <summary>
        /// 在鼠标世界坐标附近搜索敌对角色。
        ///
        /// 只负责客户端输入判定；
        /// 服务器仍会重新验证目标是否合法。
        /// </summary>
        private static Character? FindAttackTargetNearWorldPosition(List<Character> selectedCharacters, Vector2 worldPosition, float hitRadius)
        {
            if (!GameMain.IsMultiplayer)
            {
                return TLRtsSystem.FindAttackTargetAt(
                    selectedCharacters,
                    worldPosition,
                    hitRadius);
            }
            float radiusSquared = hitRadius * hitRadius;
            return Character.CharacterList
                .Where(character =>
                    character != null &&
                    !character.Removed &&
                    !character.IsDead &&
                    !character.IsIncapacitated &&
                    character.Enabled &&
                    character.Submarine != null &&
                    TLRtsRoundSubContext.Contains(
                        character.Submarine))
                .Where(character => IsLocallyHostileToAny(selectedCharacters,character))
                .Select(character => new
                {
                    Character = character,
                    DistanceSquared = Vector2.DistanceSquared(character.WorldPosition, worldPosition)
                })
                .Where(result => result.DistanceSquared <= radiusSquared)
                .OrderBy(result => result.DistanceSquared)
                .Select(result => result.Character)
                .FirstOrDefault();
        }
        /// <summary>
        /// 右键处理
        /// </summary>
        /// <param name="cam"></param>
        private static void HandleRightMouse(Camera cam)
        {
            if (selecting || selectedCharacterIds.Count == 0 || GUI.MouseOn != null || Inventory.IsMouseOnInventory || !PlayerInput.SecondaryMouseButtonClicked())
            {
                return;
            }
            List<Character> selectedCharacters = GetSelectedCharacters();
            if (selectedCharacters.Count == 0) return;
            Vector2 mouseScreenPosition = PlayerInput.MousePosition;
            Vector2 mouseWorldPosition = cam.ScreenToWorld(mouseScreenPosition);
            /*
             * 首先使用角色屏幕边界检测。
             * 这样大型生物不需要点击角色中心点。
             */
            Character? attackTarget = FindCharacterAtScreenPosition(cam, mouseScreenPosition);
            if (attackTarget != null && TryIssueAttackCommand(selectedCharacters, attackTarget))
            {
                return;
            }
            /*
             * 屏幕边界未命中时，再使用共享系统的世界坐标半径检测。
             * 缩放较远时扩大世界检测半径，保证点击手感。
             */
            float worldHitRadius = Math.Max(65.0f, 30.0f / cam.Zoom);
            attackTarget = FindAttackTargetNearWorldPosition(selectedCharacters, mouseWorldPosition, worldHitRadius);
            if (attackTarget != null && TryIssueAttackCommand(selectedCharacters, attackTarget))
            {
                return;
            }
            // 没有成功攻击角色时，将右键解释为移动命令。
            TryIssueMoveCommand(selectedCharacters, mouseWorldPosition);
        }
        /// <summary>
        /// 在屏幕位置查找可选角色
        /// </summary>
        /// <returns></returns>
        private static Character? FindSelectableCharacterAtScreenPosition(Camera cam, Vector2 mousePosition)
        {
            return EnumerateSelectableCharacters()
                .Where(character =>
                {
                    Rectangle bounds =
                        GetCharacterScreenBounds(character, cam);

                    return bounds.Contains(
                        (int)mousePosition.X,
                        (int)mousePosition.Y);
                })
                .OrderBy(character =>
                    Vector2.DistanceSquared(
                        cam.WorldToScreen(character.WorldPosition),
                        mousePosition))
                .FirstOrDefault();
        }
        /// <summary>
        /// 查找鼠标下的任意有效角色。
        /// 是否为敌人最终由 TLRtsSystem.IssueAttack 判断。
        /// </summary>
        private static Character? FindCharacterAtScreenPosition(Camera cam, Vector2 mousePosition)
        {
            return Character.CharacterList
                .Where(character =>
                    character != null &&
                    !character.Removed &&
                    !character.IsDead &&
                    character.Enabled &&
                    character.Submarine != null &&
                    TLRtsRoundSubContext.Contains(character.Submarine))
                .Where(character =>
                {
                    Rectangle bounds = GetCharacterScreenBounds(character, cam);

                    return bounds.Contains(
                        (int)mousePosition.X,
                        (int)mousePosition.Y);
                })
                .OrderBy(character =>
                    Vector2.DistanceSquared(
                        cam.WorldToScreen(character.WorldPosition),
                        mousePosition))
                .FirstOrDefault();
        }
        /// <summary>
        /// 列出可选角色
        /// </summary>
        /// <returns></returns>
        private static IEnumerable<Character> EnumerateSelectableCharacters()
        {
            if (GameMain.IsMultiplayer)
            {
                /*
                 * 多人模式以服务器同步的单位 ID 为入口，
                 * 不扫描所有本地角色，防止客户端显示无权控制的单位。
                 */
                foreach (TLRtsUnitNetState unit in TLRtsNetwork.RegistrySnapshot.Units)
                {
                    if (!unit.CanControl) continue;
                    if (commanderTeam.HasValue && unit.TeamId != (int)commanderTeam.Value) continue;
                    if (Entity.FindEntityByID(unit.CharacterId) is not Character character) continue;
                    if (IsSelectable(character)) yield return character;
                }
                yield break;
            }
            /*
             * 单人模式继续使用本地动态注册表。
             */
            foreach (TLRtsUnitRegistration registration in TLRtsUnitRegistryPermission.Registrations.Values)
            {
                if (!registration.CanControl) continue;
                if (commanderTeam.HasValue && registration.TeamId != commanderTeam.Value) continue;
                if (Entity.FindEntityByID(registration.CharacterId) is not Character character) continue;
                if (IsSelectable(character)) yield return character;
            }
        }
        /// <summary>
        /// 判断角色是否可以被当前 RTS 指挥官选择。
        ///
        /// 单人使用本地动态注册表；
        /// 多人使用服务器同步的注册表快照。
        /// </summary>
        private static bool IsSelectable(Character character)
        {
            if (character == null || character.Removed || character.IsDead || character.IsIncapacitated || !character.Enabled || character.Submarine == null || !TLRtsRoundSubContext.Contains(character.Submarine)) return false;
            if (commanderTeam.HasValue && character.TeamID != commanderTeam.Value) return false;
            if (GameMain.IsMultiplayer)
            {
                /*
                 * 多人客户端不读取本地注册表。
                 * 本地 AIController 在多人客户端可能处于禁用状态，
                 * 因此以服务器同步的单位快照为准。
                 */
                return TLRtsNetwork.IsSyncedUnitControllable(character.ID,commanderTeam);
            }
            return TLRtsUnitRegistryPermission.IsControllable(character);
        }
        private static List<Character> GetSelectedCharacters()
        {
            List<Character> result = new();
            foreach (ushort characterId in selectedCharacterIds)
            {
                if (Entity.FindEntityByID(characterId) is Character character && IsSelectable(character))
                {
                    result.Add(character);
                }
            }
            return result;
        }
        private static void RemoveInvalidSelections()
        {
            selectedCharacterIds.RemoveWhere(characterId =>
            {
                return Entity.FindEntityByID(characterId) is not Character character || !IsSelectable(character);
            });
        }
        private static Rectangle GetCharacterScreenBounds(Character character, Camera cam)
        {
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            bool foundLimb = false;
            foreach (Limb limb in character.AnimController.Limbs)
            {
                if (limb == null || limb.Removed || limb.Hidden || limb.body == null)
                {
                    continue;
                }
                Vector2 screenPosition = cam.WorldToScreen(limb.WorldPosition);
                minX = Math.Min(minX, screenPosition.X);
                minY = Math.Min(minY, screenPosition.Y);
                maxX = Math.Max(maxX, screenPosition.X);
                maxY = Math.Max(maxY, screenPosition.Y);
                foundLimb = true;
            }
            if (!foundLimb)
            {
                Vector2 center = cam.WorldToScreen(character.WorldPosition);
                return new Rectangle(
                    (int)center.X -
                    MinimumCharacterBoxSize / 2,
                    (int)center.Y -
                    MinimumCharacterBoxSize / 2,
                    MinimumCharacterBoxSize,
                    MinimumCharacterBoxSize);
            }
            int width = Math.Max(
                MinimumCharacterBoxSize,
                (int)(maxX - minX) +
                CharacterBoxPadding * 2);
            int height = Math.Max(
                MinimumCharacterBoxSize,
                (int)(maxY - minY) +
                CharacterBoxPadding * 2);
            Vector2 boundsCenter = new(
                (minX + maxX) * 0.5f,
                (minY + maxY) * 0.5f);
            return new Rectangle(
                (int)(boundsCenter.X - width * 0.5f),
                (int)(boundsCenter.Y - height * 0.5f),
                width,
                height);
        }
        private static Rectangle GetSelectionRectangle()
        {
            int left = (int)Math.Min(
               selectionStart.X,
               selectionCurrent.X);
            int top = (int)Math.Min(
                selectionStart.Y,
                selectionCurrent.Y);
            int width = Math.Max(
                1,
                (int)Math.Abs(
                    selectionCurrent.X -
                    selectionStart.X));
            int height = Math.Max(
                1,
                (int)Math.Abs(
                    selectionCurrent.Y -
                    selectionStart.Y));
            return new Rectangle(
                left,
                top,
                width,
                height);
        }
        /// <summary>
        /// 清除选择
        /// </summary>
        private static void ClearSelection()
        {
            selectedCharacterIds.Clear();
        }
        /// <summary>
        /// 取消选择拖拽
        /// </summary>
        private static void CancelSelectionDrag()
        {
            selecting = false;
            selectionStart = Vector2.Zero;
            selectionCurrent = Vector2.Zero;
        }
        /// <summary>
        /// 显示移动标记
        /// </summary>
        /// <param name="clickedWorldPosition"></param>
        /// <param name="resolvedSlots"></param>
        private static void OnMoveOrderIssued(Vector2 clickedWorldPosition, IReadOnlyList<Vector2> resolvedSlots)
        {
            commandMarkerType = CommandMarkerType.Move;
            commandMarkerTimer = CommandMarkerDuration;
            commandWorldPosition = clickedWorldPosition;
            commandTargetCharacterId = Entity.NullEntityID;

            commandSlots.Clear();
            commandSlots.AddRange(resolvedSlots);
        }
        /// <summary>
        /// 显示攻击目标
        /// </summary>
        /// <param name="target"></param>
        private static void ShowAttackMarker(Character target)
        {
            commandMarkerType = CommandMarkerType.Attack;
            commandMarkerTimer = CommandMarkerDuration;
            commandWorldPosition = target.WorldPosition;
            commandTargetCharacterId = target.ID;
            commandSlots.Clear();
        }
        /// <summary>
        /// 清除命令标记
        /// </summary>
        private static void ClearCommandMarker()
        {
            commandMarkerType = CommandMarkerType.None;
            commandMarkerTimer = 0.0f;
            commandWorldPosition = Vector2.Zero;
            commandTargetCharacterId = Entity.NullEntityID;
            commandSlots.Clear();
        }
        private static void OnRoundStateCleared()
        {
            commanderSnapshotRequested = false;
            commanderSnapshotRetryTimer = 0.0f;

            if (commanderButton != null)
            {
                commanderButton.Visible = false;
            }
            ClearSelection();
            CancelSelectionDrag();
            ClearCommandMarker();
            lastHandledCommandResultSequence = 0;
            if (IsActive)
            {
                ExitRtsMode(
                    restoreControlledCharacter: true);
            }
        }
        /// <summary>
        /// GUI.Draw 的 Harmony Postfix。
        /// 此时使用屏幕坐标绘制。
        /// </summary>
        private static void AfterGuiDraw(Camera cam, SpriteBatch spriteBatch)
        {
            if (!IsActive || Screen.Selected != GameMain.GameScreen || GUI.DisableHUD) return;
            DrawSelectedUnits(spriteBatch, cam);
            DrawSelectionRectangle(spriteBatch);
            DrawCommandMarker(spriteBatch, cam);
            DrawTopStatus(spriteBatch);
        }
        private static void DrawSelectedUnits(SpriteBatch spriteBatch, Camera cam)
        {
            Color selectedColor = new Color(30, 220, 255, 235);
            foreach (Character character in GetSelectedCharacters())
            {
                Rectangle bounds = GetCharacterScreenBounds(character, cam);
                GUI.DrawRectangle(
                     spriteBatch,
                     bounds,
                     Color.Black * 0.85f,
                     isFilled: false,
                     thickness: 5.0f);
                GUI.DrawRectangle(
                   spriteBatch,
                   bounds,
                   selectedColor,
                   isFilled: false,
                   thickness: 2.0f);
                /*
                * 在单位脚下画短横线，使选择标记在复杂背景中更明显。
                */
                Vector2 bottomLeft = new(
                    bounds.Left,
                    bounds.Bottom + 4);
                Vector2 bottomRight = new(
                    bounds.Right,
                    bounds.Bottom + 4);
                GUI.DrawLine(
                    spriteBatch,
                    bottomLeft,
                    bottomRight,
                    selectedColor,
                    width: 3.0f);
            }
        }
        private static void DrawSelectionRectangle(SpriteBatch spriteBatch)
        {
            if (!selecting) return;
            if (Vector2.DistanceSquared(selectionStart, selectionCurrent) < DragThreshold * DragThreshold) return;
            Rectangle rect = GetSelectionRectangle();
            Color color = new(50, 200, 255, 220);
            GUI.DrawRectangle(
                spriteBatch,
                rect,
                color * 0.16f,
                isFilled: true);
            GUI.DrawRectangle(
                spriteBatch,
                rect,
                color,
                isFilled: false,
                thickness: 2.0f);
        }
        private static void DrawCommandMarker(SpriteBatch spriteBatch, Camera cam)
        {
            if (commandMarkerType == CommandMarkerType.None || commandMarkerTimer <= 0.0f) return;
            float alpha = MathHelper.Clamp(commandMarkerTimer / CommandMarkerDuration, 0.0f, 1.0f);
            if (commandMarkerType == CommandMarkerType.Attack)
            {
                if (Entity.FindEntityByID(commandTargetCharacterId) is Character target && !target.Removed)
                {
                    // 攻击目标十字跟随目标移动。
                    commandWorldPosition = target.WorldPosition;
                }
                Vector2 screenPosition = cam.WorldToScreen(commandWorldPosition);
                DrawCross(
                       spriteBatch,
                       screenPosition,
                       24.0f,
                       Color.Red * alpha,
                       4.0f);
                GUI.DrawRectangle(
                        spriteBatch,
                        new Rectangle(
                            (int)screenPosition.X - 18,
                            (int)screenPosition.Y - 18,
                            36,
                            36),
                        Color.OrangeRed * alpha,
                        isFilled: false,
                        thickness: 2.0f);
                return;
            }
            // 鼠标原始移动目标。
            DrawCross(
                spriteBatch,
                cam.WorldToScreen(commandWorldPosition),
                20.0f,
                Color.White * alpha,
                3.0f);
            // 每个单位经过 Hull 和路径点修正后的实际槽位。
            foreach (Vector2 slot in commandSlots)
            {
                Vector2 slotScreen = cam.WorldToScreen(slot);
                DrawCross(
                    spriteBatch,
                    slotScreen,
                    10.0f,
                    Color.DeepSkyBlue * alpha,
                    2.0f);
                GUI.DrawRectangle(
                    spriteBatch,
                    new Rectangle(
                        (int)slotScreen.X - 7,
                        (int)slotScreen.Y - 7,
                        14,
                        14),
                    Color.DeepSkyBlue * alpha,
                    isFilled: false,
                    thickness: 1.5f);
            }
        }
        private static void DrawTopStatus(SpriteBatch spriteBatch)
        {
            string status = $"[RTS 战术模式] 已选择：{selectedCharacterIds.Count}";
            string help =
                $"{ToggleKey} 退出  |  左键选择/框选  |  " +
                "右键移动/攻击  |  Escape 清空选择";
            Vector2 statusSize =
                GUIStyle.Font.MeasureString(status);
            Vector2 helpSize =
                GUIStyle.SmallFont.MeasureString(help);
            float panelWidth =
                Math.Max(statusSize.X, helpSize.X) + 36.0f;
            Rectangle panel = new(
                (int)(GameMain.GraphicsWidth * 0.5f -
                      panelWidth * 0.5f),
                14,
                (int)panelWidth,
                58);
            GUI.DrawRectangle(
                spriteBatch,
                panel,
                new Color(5, 15, 24, 205),
                isFilled: true);
            GUI.DrawRectangle(
                spriteBatch,
                panel,
                Color.DeepSkyBlue * 0.85f,
                isFilled: false,
                thickness: 2.0f);
            Vector2 statusPosition = new(
               GameMain.GraphicsWidth * 0.5f -
               statusSize.X * 0.5f,
               panel.Y + 7.0f);
            Vector2 helpPosition = new(
                GameMain.GraphicsWidth * 0.5f -
                helpSize.X * 0.5f,
                panel.Y + 33.0f);
            GUIStyle.Font.DrawString(
                spriteBatch,
                status,
                statusPosition + Vector2.One,
                Color.Black);
            GUIStyle.Font.DrawString(
               spriteBatch,
               status,
               statusPosition,
               Color.DeepSkyBlue);
            GUIStyle.SmallFont.DrawString(
                spriteBatch,
                help,
                helpPosition + Vector2.One,
                Color.Black);
            GUIStyle.SmallFont.DrawString(
                spriteBatch,
                help,
                helpPosition,
                Color.LightGray);
        }
        /// <summary>
        /// 画十字
        /// </summary>
        private static void DrawCross(SpriteBatch spriteBatch, Vector2 center, float size, Color color, float thickness)
        {
            GUI.DrawLine(
                spriteBatch,
                center - Vector2.UnitX * size,
                center + Vector2.UnitX * size,
                color,
                width: thickness);

            GUI.DrawLine(
                spriteBatch,
                center - Vector2.UnitY * size,
                center + Vector2.UnitY * size,
                color,
                width: thickness);
        }

        /// <summary>
        /// 将服务器返回的潜艇局部坐标转换成世界坐标，
        /// 然后显示移动十字和编队槽位。
        /// </summary>
        private static void ShowServerMoveResult(TLRtsCommandResult result)
        {
            if (Entity.FindEntityByID(
                    result.TargetSubmarineId)
                is not Submarine targetSubmarine ||
                targetSubmarine.Removed)
            {
                return;
            }

            Vector2 clickedWorldPosition =
                targetSubmarine.Position +
                new Vector2(
                    result.TargetLocalX,
                    result.TargetLocalY);

            List<Vector2> slotWorldPositions =
                new();

            foreach (TLRtsNetPosition slot
                in result.Slots)
            {
                if (Entity.FindEntityByID(
                        slot.SubmarineId)
                    is not Submarine slotSubmarine ||
                    slotSubmarine.Removed)
                {
                    continue;
                }

                slotWorldPositions.Add(
                    slotSubmarine.Position +
                    new Vector2(
                        slot.LocalX,
                        slot.LocalY));
            }

            OnMoveOrderIssued(
                clickedWorldPosition,
                slotWorldPositions);
        }

        /// <summary>
        /// 显示服务器确认的攻击目标标记。
        /// </summary>
        private static void ShowServerAttackResult(
            TLRtsCommandResult result)
        {
            if (Entity.FindEntityByID(
                    result.TargetCharacterId)
                is not Character target ||
                target.Removed ||
                target.IsDead)
            {
                return;
            }

            ShowAttackMarker(target);
        }
        private static void DebugLocalHeldItems()
        {
            Character? controlled =
                Character.Controlled;

            Character? myClientCharacter =
                GameMain.Client?.MyClient?.Character;

            Character? cachedCharacter =
                GameMain.Client?.Character;

            ClientDebug(
                $"角色引用：" +
                $"Controlled={controlled?.ID.ToString() ?? "null"}, " +
                $"MyClient.Character={myClientCharacter?.ID.ToString() ?? "null"}, " +
                $"GameClient.Character={cachedCharacter?.ID.ToString() ?? "null"}");

            Character? character =
                GetLocalCommanderCharacter();

            if (character == null)
            {
                ClientDebug("找不到本地指挥角色。");
                return;
            }

            Item[] heldItems =
                character.HeldItems.ToArray();

            ClientDebug(
                $"检查角色 ID={character.ID}，" +
                $"HeldItems 数量={heldItems.Length}");

            foreach (Item item in heldItems)
            {
                ClientDebug(
                    $"手持物品：" +
                    $"ID={item.ID}, " +
                    $"Identifier={item.Prefab.Identifier}, " +
                    $"HasRtsTag={item.HasTag(CommandDeviceTag)}");
            }
        }
    }
}

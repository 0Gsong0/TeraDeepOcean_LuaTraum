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

            var guiDraw = AccessTools.Method(
                typeof(GUI),
                nameof(GUI.Draw),
                new[]
                {
                    typeof(Camera),
                    typeof(SpriteBatch)
                });
            if (guiDraw != null)
            {
                harmony.Patch(
                    guiDraw,
                    postfix: new HarmonyMethod(
                        typeof(TLRtsClient),
                        nameof(AfterGuiDraw)));
            }
        }
        public static void Update(float deltaTime)
        {
            if (commandMarkerTimer > 0.0f)
            {
                commandMarkerTimer -= deltaTime;

                if (commandMarkerTimer <= 0.0f)
                {
                    ClearCommandMarker();
                }
            }
            RemoveInvalidSelections();
            if (GUI.KeyboardDispatcher.Subscriber == null && PlayerInput.KeyHit(ToggleKey))
            {
                if (IsActive)
                {
                    ExitRtsMode(restoreControlledCharacter: true);
                }
                else
                {
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
        private static bool TryEnterRtsMode()
        {
            if (!TLRtsSystem.IsAuthority ||
                Screen.Selected != GameMain.GameScreen ||
                GameMain.GameSession == null ||
                !Level.IsLoadedOutpost)
            {
                GUI.AddMessage(
                    "RTS 战术模式只能在单人前哨站回合中开启。",
                    Color.OrangeRed);

                return false;
            }
            if (!TLRtsRoundSubContext.Refresh())
            {
                GUI.AddMessage(
                    "没有找到有效的前哨站停靠网络。",
                    Color.OrangeRed);

                return false;
            }
            Character? controlled = Character.Controlled;
            if (controlled == null ||
                controlled.Removed ||
                controlled.IsDead)
            {
                GUI.AddMessage(
                    "没有可以转换为 RTS 指挥单位的当前角色。",
                    Color.OrangeRed);

                return false;
            }
            if (controlled.Submarine == null ||
                !TLRtsRoundSubContext.Contains(controlled.Submarine))
            {
                GUI.AddMessage(
                    "当前角色不在前哨站或已连接潜艇中。",
                    Color.OrangeRed);

                return false;
            }
            previousControlledCharacter = new WeakReference<Character>(controlled);
            commanderTeam = controlled.TeamID;
            /*
             * RegisterCurrentCrewBots 不包含当前玩家角色，
             * 所以必须在清除 Character.Controlled 前单独注册它。
             */
            if (!TLRtsUnitRegistryPermission.IsRegistered(controlled))
            {
                TLRtsUnitRegistryPermission.Register(
                    controlled,
                    unitClass: "crew".ToIdentifier(),
                    isSpawnByRts: false);
            }
            else
            {
                TLRtsUnitRegistryPermission.SetControllable(
                    controlled,
                    true);
            }
            // 进入 FreeCam。
            Character.Controlled = null;
            // 此时原玩家角色已经成为 Bot，再注册所有普通船员 Bot。
            TLRtsSystem.RegisterCurrentCrewBots();
            /*
             * 原玩家角色失去玩家控制后可能马上执行自主工作。
             * 给它一个当前位置移动命令，使其进入 Guard。
             */
            if (TLRtsUnitRegistryPermission.IsControllable(controlled))
            {
                TLRtsSystem.IssueMove(
                    new[] { controlled },
                    controlled.WorldPosition);
            }
            ClearCommandMarker();
            ClearSelection();
            CancelSelectionDrag();
            Camera cam = GameMain.GameScreen.Cam;
            cam.TargetPos = Vector2.Zero;
            /*
             * Character.Controlled == null 时，原版 Escape 会打开暂停菜单。
             * RTS 模式中要让 Escape 专用于清空选择。
             */
            previousPreventPauseMenuToggle = GUI.PreventPauseMenuToggle;
            GUI.PreventPauseMenuToggle = true;
            IsActive = true;
            GUI.AddMessage(
                "已进入 RTS 战术模式",
                Color.DeepSkyBlue);
            return true;
        }
        private static void ExitRtsMode(bool restoreControlledCharacter)
        {
            if (!IsActive) return;
            IsActive = false;
            ClearSelection();
            CancelSelectionDrag();
            ClearCommandMarker();
            GUI.PreventPauseMenuToggle = previousPreventPauseMenuToggle;
            if (restoreControlledCharacter)
            {
                Character? restoreCharacter = FindCharacterToRestore();
                if (restoreCharacter != null)
                {
                    Character.Controlled = restoreCharacter;
                }
                else
                {
                    Character.Controlled = null;

                    GUI.AddMessage(
                        "所有可控角色已死亡，无法控制",
                        Color.OrangeRed);
                }
            }
            previousControlledCharacter = null;
            commanderTeam = null;
            GUI.AddMessage(
                "已退出 RTS 战术模式",
                Color.LightGray);
        }
        private static Character? FindCharacterToRestore()
        {
            if (previousControlledCharacter != null && previousControlledCharacter.TryGetTarget(out Character? previous) && CanRestoreControl(previous)) return previous;
            /*
             * 原角色死亡时，尝试恢复到同阵营的存活人类船员。
             */
            GUI.AddMessage(
                "原角色已经死亡，正在尝试控制其它角色",
                Color.LightGray);
            return Character.CharacterList.FirstOrDefault(character =>
                CanRestoreControl(character) &&
                commanderTeam.HasValue &&
                character.TeamID == commanderTeam.Value &&
                character.AIController is HumanAIController);
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
            return TLRtsSystem.IsAuthority &&
                   Screen.Selected == GameMain.GameScreen &&
                   GameMain.GameSession != null &&
                   Level.IsLoadedOutpost &&
                   TLRtsRoundSubContext.IsValid;
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
            if (attackTarget != null && TLRtsSystem.IssueAttack(selectedCharacters, attackTarget))
            {
                ShowAttackMarker(attackTarget);
                return;
            }
            /*
             * 屏幕边界未命中时，再使用共享系统的世界坐标半径检测。
             * 缩放较远时扩大世界检测半径，保证点击手感。
             */
            float worldHitRadius = Math.Max(65.0f, 30.0f / cam.Zoom);
            attackTarget = TLRtsSystem.FindAttackTargetAt(selectedCharacters, mouseWorldPosition, worldHitRadius);
            if (attackTarget != null && TLRtsSystem.IssueAttack(selectedCharacters, attackTarget))
            {
                ShowAttackMarker(attackTarget);
                return;
            }
            // 没有成功攻击角色时，将右键解释为移动命令。
            TLRtsSystem.IssueMove(selectedCharacters, mouseWorldPosition);
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
            foreach (TLRtsUnitRegistration registration in TLRtsUnitRegistryPermission.Registrations.Values)
            {
                if (!registration.CanControl)
                {
                    continue;
                }
                if (commanderTeam.HasValue && registration.TeamId != commanderTeam.Value)
                {
                    continue;
                }
                if (Entity.FindEntityByID(registration.CharacterId) is not Character character)
                {
                    continue;
                }
                if (!IsSelectable(character))
                {
                    continue;
                }
                yield return character;
            }
        }
        private static bool IsSelectable(Character character)
        {
            return character != null &&
                  !character.Removed &&
                  !character.IsDead &&
                  !character.IsIncapacitated &&
                  character.Enabled &&
                  character.Submarine != null &&
                  TLRtsRoundSubContext.Contains(
                      character.Submarine) &&
                  TLRtsUnitRegistryPermission.IsControllable(
                      character) &&
                  (!commanderTeam.HasValue ||
                   character.TeamID == commanderTeam.Value);
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
            ClearSelection();
            CancelSelectionDrag();
            ClearCommandMarker();

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
    }
}

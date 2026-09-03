using Barotrauma.Networking;
using System.Timers;

namespace TeraDeepOcean
{
    public static class TLConfigMenu
    {
        private enum ConfigTab
        {
            Home,System,Cyber
        }
        private static GUIFrame? rootFrame;
        private static GUIListBox? rootList;
        private static GUIComponent? buttonsRow;

        private static GUIFrame? leftPanel;
        private static GUIFrame? rightPanel;
        private static GUITextBlock? rightTitle;
        private static GUIListBox? rightList;

        private static GUIButton? homeTabButton;
        private static GUIButton? systemTabButton;
        private static GUIButton? cyberTabButton;

        private static ConfigTab currentTab = ConfigTab.Home;

        private static readonly Color NormalTabColor = new Color(40, 48, 58, 255);
        private static readonly Color HoverTabColor = new Color(70, 100, 130, 255);
        private static readonly Color SelectedTabColor = new Color(85, 145, 190, 255);
        private static readonly Color PressedTabColor = new Color(55, 115, 165, 255);

        private static bool waitingConfig;
        private static bool receivedConfig;
        private static bool introVideoFinished;
        private static GUITextBlock? loadingText;

        private static bool introCountdownActive;
        private static float introCountdownTimer;
        private static float introCountdownDuration;
        public static void Open(GUIFrame frame, GUIListBox list, GUIComponent buttons)
        {
            rootFrame = frame;
            rootList = list;
            buttonsRow = buttons;

            waitingConfig = GameMain.IsMultiplayer;
            receivedConfig = !GameMain.IsMultiplayer;
            introVideoFinished = false;

            ClearChildren(rootList.Content.RectTransform);
            ClearChildren(buttonsRow.RectTransform);

            if (GameMain.IsMultiplayer)
            {
                TLConfigBridge.ClientRequestUpdate();
            }
            else
            {
                TLConfigService.LoadLocal();
                receivedConfig = true;
            }
            //TLVideoPlayer.PlayLocalVideoFromMod(Path.Combine("Content", "UI", "Menu", "tsm_logo_opus.webm"));
            StartIntroCountdown(1f);
        }
        private static void Close()
        {
            introCountdownActive = false;

            if (rootFrame == null) { return; }
            rootFrame.RectTransform.Parent = null;
            rootFrame = null;
            rootList = null;
            buttonsRow = null;
        }
        public static void OnConfigReceived()
        {
            receivedConfig = true;
            TryShowConfigAfterIntro();
        }
        private static void TryShowConfigAfterIntro()
        {
            if (!introVideoFinished) { return; }
            if (waitingConfig && !receivedConfig)
            {
                if (loadingText != null)
                {
                    loadingText.Text = "正在等待服务器配置...";
                }
                return;
            }
            ClearChildren(rootList.Content.RectTransform);
            ClearChildren(buttonsRow.RectTransform);
            DrawLayOut(rootList.Content);
            DrtawButtons(buttonsRow);
            RefreshTabVisuals();
            RefreshRightPanel();
        }
        private static void DrawLayOut(GUIComponent parent)
        {
            GUIFrame contentRoot = new GUIFrame(new RectTransform(new Vector2(1, 1), parent.RectTransform, anchor: Anchor.Center), style: null);

            leftPanel = new GUIFrame(new RectTransform(new Vector2(0.25f, 1f), contentRoot.RectTransform, Anchor.CenterLeft), style: "InnerFrame");
            rightPanel = new GUIFrame(new RectTransform(new Vector2(0.75f,1f),contentRoot.RectTransform,Anchor.CenterRight), style: "InnerFrame");

            GUILayoutGroup leftLayOut = new GUILayoutGroup(new RectTransform(new Vector2(0.88f, 0.95f), leftPanel.RectTransform, Anchor.Center), isHorizontal: false)
            {
                RelativeSpacing = 0.03f,
            };
            AddLeftTitle(leftLayOut);

            homeTabButton = AddTabButton(leftLayOut, TextManager.Get("config.Home").ToString(), ConfigTab.Home);
            systemTabButton = AddTabButton(leftLayOut, TextManager.Get("config.TL_header_System").ToString(), ConfigTab.System);
            cyberTabButton = AddTabButton(leftLayOut, TextManager.Get("config.Homeconfig.TL_header_cyb").ToString(), ConfigTab.Cyber);

            rightTitle = new GUITextBlock(new RectTransform(new Vector2(0.9f, 0.07f), rightPanel.RectTransform, Anchor.TopCenter)
            {
                AbsoluteOffset = new Point(0,10)
            },"",textAlignment:Alignment.CenterLeft, style: "GUITextBlock");
            rightTitle.TextScale = 1.25f;

            rightList = new GUIListBox(new RectTransform(new Vector2(0.92f, 0.8f), rightPanel.RectTransform, Anchor.BottomCenter)
            {
                AbsoluteOffset = new Point(0, 20)
            }, style: "GUIListBox");
        }
        private static void AddLeftTitle(GUIComponent parent)
        {
            GUITextBlock text = new GUITextBlock(new RectTransform(new Vector2(1f, 0.08f), parent.RectTransform), TextManager.Get("config.TeraDeepOcean").ToString(), textAlignment: Alignment.TopCenter, style: "GUITextBlock");

            text.TextScale = 1.25f;
        }
        private static GUIButton AddTabButton(GUIComponent parent,string text,ConfigTab tab)
        {
            GUIButton button = new GUIButton(new RectTransform(new Vector2(1f, 0.2f), parent.RectTransform), text, style: "GUIButton")
            {
                Color = NormalTabColor,
                HoverColor = HoverTabColor,
                PressedColor = PressedTabColor,
                SelectedColor = SelectedTabColor
            };
            button.OnClicked = (_, _) =>
            {
                currentTab = tab;
                RefreshTabVisuals();
                RefreshRightPanel();
                return true;
            };
            return button;
        }
        private static void RefreshTabVisuals()
        {
            SetTabSelected(homeTabButton, currentTab == ConfigTab.Home);
            SetTabSelected(systemTabButton, currentTab == ConfigTab.System);
            SetTabSelected(cyberTabButton, currentTab == ConfigTab.Cyber);
        }
        private static void SetTabSelected(GUIButton? button,bool selected)
        {
            if (button == null) return;
            button.Selected = selected;
            button.Color = selected ? SelectedTabColor : NormalTabColor;
            button.HoverColor = selected ? SelectedTabColor : HoverTabColor;
            button.PressedColor = PressedTabColor;
            button.SelectedColor = SelectedTabColor;
        }
        private static void RefreshRightPanel()
        {
            if (rightTitle == null || rightList == null) return;
            ClearChildren(rightList.Content.RectTransform);

            switch (currentTab)
            {
                case ConfigTab.Home:
                    DrawHome();
                    break;
                case ConfigTab.System:
                    rightTitle.Text = TextManager.Get("config.TL_header_System").ToString();
                    rightTitle.ToolTip = TextManager.Get("config.des.TL_header_System").ToString();
                    AddSectionTitle(rightList.Content, TextManager.Get("config.System_Run").ToString());
                    AddInt(rightList.Content, "TL_TLUpdateInterval");
                    AddInt(rightList.Content, "TL_TLLateUpdateInterval");
                    AddBool(rightList.Content, "TL_TLDebugModel");
                    break;
                case ConfigTab.Cyber:
                    rightTitle.Text = TextManager.Get("config.TL_header_cyb").ToString();
                    rightTitle.ToolTip = TextManager.Get("config.des.TL_header_cyb").ToString();
                    AddSectionTitle(rightList.Content, TextManager.Get("config.SectionTitle_cyb1").ToString());


                    break;
            }
        }
        private static void DrawHome()
        {
            if (rightTitle == null || rightList == null) return;
            rightTitle.Text = TextManager.Get("config.Home").ToString();
            rightTitle.ToolTip = "";

            string canEdit = CanEdit() ? TextManager.Get("config.configModel.Edit").ToString() : TextManager.Get("config.configModel.CantEdit").ToString();

            new GUITextBlock(new RectTransform(new Vector2(0.96f, 0.08f), rightList.Content.RectTransform), 
                canEdit,textAlignment:Alignment.CenterLeft, style: "GUITextBlock");

            string gameModel = GameMain.IsMultiplayer ? TextManager.Get("config.gameModel.server").ToString() : TextManager.Get("config.gameModel.sing").ToString();

            new GUITextBlock(new RectTransform(new Vector2(0.96f, 0.08f), rightList.Content.RectTransform),
                gameModel, textAlignment: Alignment.CenterLeft, style: "GUITextBlock");

            var hint = new GUITextBlock(new RectTransform(new Vector2(0.96f, 0.08f), rightList.Content.RectTransform),
                TextManager.Get("config.choose").ToString(), textAlignment: Alignment.CenterLeft, style: "GUITextBlock");

            GUIImage logo = new GUIImage(
                new RectTransform(new Vector2(1f, 0.7f), rightList.Content.RectTransform, Anchor.Center)
                {
                    RelativeOffset = new Vector2(0, -0.2f)
                },
                "TLConfigLogo",scaleToFit:true);
        }
        private static void DrtawButtons(GUIComponent parent)
        {
            bool canEdit = CanEdit();
            GUIButton save = new GUIButton(new RectTransform(new Vector2(0.32f, 1f), parent.RectTransform)
            {
                AbsoluteOffset = new Point(0, 15)
            }, TextManager.Get("config.Submit").ToString(), Alignment.Center, style: "TLConfigGUIButton")
            {
                Enabled = canEdit,
                ToolTip = canEdit ? TextManager.Get("config.des.Submit").ToString() : TextManager.Get("config.CantEdit").ToString()
            };
            save.OnClicked = (_, _) =>
            {
                if (!canEdit) return true;
                TLConfigService.SaveOrSubmit();
                GUIMessageBox box = new GUIMessageBox(TextManager.Get("config.Submit.Success").ToString(), GameMain.IsMultiplayer ? TextManager.Get("config.Submit.des.Success").ToString() : TextManager.Get("config.Submit.des.Success.sing").ToString(), new LocalizedString[] { "确定" });
                box.DrawOnTop = true;
                box.Buttons[0].OnClicked = (_, _) =>{
                    box.Close();
                    return true;
                };
                return true;
            };

            GUIButton reset = new GUIButton(new RectTransform(new Vector2(0.32f, 1f), parent.RectTransform)
            {
                AbsoluteOffset = new Point(0, 15)
            }, TextManager.Get("config.Default").ToString(), Alignment.Center, style: "TLConfigGUIButton")
            {
                Enabled = canEdit,
                ToolTip = canEdit ? TextManager.Get("config.des.Default").ToString() : TextManager.Get("config.CantEdit").ToString()
            };
            reset.OnClicked = (_, _) =>
            {
                if (!canEdit) return true;
                GUIMessageBox box = new GUIMessageBox(TextManager.Get("config.des.Default").ToString(), TextManager.Get("config.des.Default2").ToString(), new LocalizedString[] { TextManager.Get("config.yes").ToString(), TextManager.Get("config.no").ToString() });
                box.DrawOnTop = true;
                RefreshRightPanel();

                box.Buttons[0].OnClicked = (_, _) => {
                    TLConfigService.ResetToDefaults();
                    RefreshRightPanel();
                    box.Close();
                    return true;
                };
                box.Buttons[1].OnClicked = (_, _) =>
                {
                    box.Close();
                    return true;
                };
                return true;
            };

            GUIButton close = new GUIButton(new RectTransform(new Vector2(0.32f, 1f), parent.RectTransform)
            {
                AbsoluteOffset = new Point(0, 15)
            }, TextManager.Get("config.close").ToString(), Alignment.Center, style: "TLConfigGUIButton");
            close.OnClicked = (_, _) =>
            {
                Close();
                return true;
            };
        }
        private static void AddSectionTitle(GUIComponent parent, string text)
        {
            GUITextBlock title = new GUITextBlock(new RectTransform(new Vector2(0.96f, 0.08f), parent.RectTransform), text, textAlignment: Alignment.CenterLeft, style: "GUITextBlock")
            {
                TextScale = 1.2f,
                TextColor = new Color(120, 190, 230, 255)
            };
        }
        private static void AddInt(GUIComponent parent,string key)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null) return;

            int currentVaule = TLConfigService.Current.GetInt(key);
            bool canEdit = CanEdit();

            GUITextBlock lable = AddSettingLable(parent, GetName(entry)+":"+TLConfigService.Current.GetInt(key), GetDes(entry));

            GUINumberInput input = new GUINumberInput(new RectTransform(new Vector2(0.96f, 0.085f), parent.RectTransform), NumberType.Int)
            {
                MinValueInt = (int)entry.Min,
                MaxValueInt = (int)entry.Max,
                IntValue = currentVaule,
                Enabled = canEdit,
                ToolTip = GetPermissionToolTip(entry)
            };
            input.OnValueChanged = num =>
            {
                if (!CanEdit()) return;
                TLConfigService.Current.SetInt(key, num.IntValue);
                lable.Text = $"{GetName(entry)}:{TLConfigService.Current.GetInt(key)}";
            };
        }
        private static void AddFloat(GUIComponent parent, string key)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null) return;

            float currentVaule = TLConfigService.Current.GetFloat(key);
            bool canEdit = CanEdit();

            GUITextBlock lable = AddSettingLable(parent, $"{GetName(entry)}:{TLConfigService.Current.GetFloat(entry.Key)}", GetDes(entry));

            GUINumberInput input = new GUINumberInput(new RectTransform(new Vector2(0.96f, 0.085f), parent.RectTransform), NumberType.Float)
            {
                MinValueFloat = entry.Min,
                MaxValueFloat = entry.Max,
                FloatValue = currentVaule,
                Enabled = canEdit,
                ToolTip = GetPermissionToolTip(entry)
            };
            input.OnValueChanged = num =>
            {
                if (!canEdit) return;
                TLConfigService.Current.SetFloat(key, num.FloatValue);
                lable.Text = $"{GetName(entry)}:{TLConfigService.Current.GetFloat(entry.Key)}";
            };
        }
        private static void AddBool(GUIComponent parent, string key)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null) return;

            bool currentVaule = TLConfigService.Current.GetBool(key);
            bool canEdit = CanEdit();

            GUITextBlock lable = AddSettingLable(parent, $"{GetName(entry)}:{TLConfigService.Current.GetBool(entry.Key)}", GetDes(entry));

            GUITickBox tickBox = new GUITickBox(new RectTransform(new Vector2(0.96f, 0.085f), parent.RectTransform), GetName(entry))
            {
                Selected = TLConfigService.Current.GetBool(key),
                Enabled = canEdit,
                ToolTip = GetPermissionToolTip(entry)
            };
            tickBox.OnSelected = box =>
            {
                if (!canEdit) return true;
                TLConfigService.Current.SetBool(key, box.Selected);
                lable.Text = $"{GetName(entry)}:{TLConfigService.Current.GetBool(entry.Key)}";
                return true;
            };
        }
        private static void AddString(GUIComponent parent, string key)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null) return;

            string currentVaule = TLConfigService.Current.GetString(key);
            bool canEdit = CanEdit();

            GUITextBlock lable = AddSettingLable(parent, $"{GetName(entry)}:{TLConfigService.Current.GetString(entry.Key)}", GetDes(entry));

            GUITextBox textBox = new GUITextBox(new RectTransform(new Vector2(0.96f, 0.085f), parent.RectTransform))
            {
                Text = TLConfigService.Current.GetString(key),
                Enabled = canEdit,
                ToolTip = GetPermissionToolTip(entry)
            };
            textBox.OnTextChanged += (textBox, text) =>
            {
                if (!canEdit) return true;
                TLConfigService.Current.SetString(key, text);
                return true;
            };
        }
        private static GUITextBlock AddSettingLable(GUIComponent parent,string text,string toolTip)
        {
            GUITextBlock lable = new GUITextBlock(new RectTransform(new Vector2(0.96f, 0.055f), parent.RectTransform),
                text, textAlignment: Alignment.CenterLeft, style: "GUITextBlock");
            lable.ToolTip = toolTip;
            return lable;
        }
        private static string GetName(TLConfigDataEntry entry)
        {
            return TextManager.Get(entry.NameTextTag).Fallback(entry.Key).Value;
        }
        private static string GetDes(TLConfigDataEntry entry)
        {
            return TextManager.Get(entry.DescriptionTextTag).Fallback("").Value;
        }
        private static string GetPermissionToolTip(TLConfigDataEntry entry)
        {
            string des = GetDes(entry);
            if (CanEdit()) return des;

            return des + $"\n{TextManager.Get("config.CantEdit2").ToString()}";
        }
        private static bool CanEdit()
        {
            if (GameMain.NetworkMember == null) return true;
            return GameMain.Client != null && GameMain.Client.HasPermission(ClientPermissions.ManageCampaign);
        }
        private static void ClearChildren(RectTransform parent)
        {
            List<RectTransform> children = new List<RectTransform>(parent.Children);
            foreach (var child in children)
            {
                child.Parent = null;
            }
        }
        private static void StartIntroCountdown(float durationSeconds)
        {
            introCountdownActive = true;
            introCountdownTimer = 0f;
            introCountdownDuration = durationSeconds;
            introVideoFinished = false;
        }
        public static void Update(float deltaTime)
        {
            if (!introCountdownActive) { return; }
            if (rootFrame == null) { return; }

            introCountdownTimer += deltaTime;

            if (introCountdownTimer < introCountdownDuration)
            {
                return;
            }

            introCountdownActive = false;
            introVideoFinished = true;
            TLVideoPlayer.StopLocalVideo();
            TryShowConfigAfterIntro();
        }
    }
}

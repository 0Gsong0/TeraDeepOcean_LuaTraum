using Barotrauma.Networking;
using HarmonyLib;

namespace TeraDeepOcean;

/// <summary>替代 easysettings.lua：暂停菜单入口、窗口容器和客户端更新。</summary>
internal static class TLConfigMenuEntry
{
    private static GUIFrame? pauseMenu;
    private static GUIButton? entryButton;
    private static GameClient? connectedClient;
    private static bool configReceived;
    private static float requestTimer;

    public static void Init(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.Update), new[] { typeof(float) }),
            postfix: new HarmonyMethod(typeof(TLConfigMenuEntry), nameof(Update)));
    }

    private static void Update(float deltaTime)
    {
        UpdateConnection(deltaTime);
        GUIFrame? current = GUI.PauseMenuOpen ? GUI.PauseMenu : null;
        if (!ReferenceEquals(current, pauseMenu))
        {
            TLConfigMenu.Close();
            if (entryButton != null) entryButton.RectTransform.Parent = null;
            entryButton = null;
            pauseMenu = current;
            if (current != null) AddEntry(current);
        }
        TLConfigMenu.Update(deltaTime);
        TLVideoPlayer.Update();
    }

    private static void AddEntry(GUIFrame parent)
    {
        // 不依赖旧 Lua 中的 children[2]/children[1] 下标。
        // 放在暂停菜单底部，避免挤压原版按钮布局。
        entryButton = new GUIButton(new RectTransform(new Vector2(0.15f, 0.055f),
            parent.RectTransform, Anchor.TopCenter) 
        {
            MinSize = new Point(200, 35),
            RelativeOffset = new Vector2(0, 0.35f)
        }, "TeraDeepOcean - 泰拉渊洋", style: "GUIButtonSmall");
        entryButton.OnClicked = (_, _) =>
        {
            Open(parent);
            return true;
        };
    }

    private static void Open(GUIFrame parent)
    {
        TLConfigMenu.Close();
        var frame = new GUIFrame(new RectTransform(new Vector2(0.62f, 0.72f),
            parent.RectTransform, Anchor.Center), style: "GUIFrame");
        var main = new GUILayoutGroup(new RectTransform(new Vector2(0.96f),
            frame.RectTransform, Anchor.Center), isHorizontal: false);
        _ = new GUITextBlock(new RectTransform(new Vector2(1, 0.08f), main.RectTransform),
            "TeraDeepOcean", font: GUIStyle.LargeFont, textAlignment: Alignment.Center);
        var list = new GUIListBox(new RectTransform(new Vector2(1, 0.82f), main.RectTransform));
        var buttons = new GUILayoutGroup(new RectTransform(new Vector2(1, 0.08f),
            main.RectTransform), isHorizontal: true) { RelativeSpacing = 0.02f };
        TLConfigMenu.Open(frame, list, buttons);
    }

    private static void UpdateConnection(float deltaTime)
    {
        var client = GameMain.Client;
        if (!ReferenceEquals(client, connectedClient) || (client != null && !client.connected))
        {
            bool wasConnected = connectedClient != null;
            connectedClient = client;
            configReceived = false;
            requestTimer = 0;
            if (wasConnected && client == null) TLConfigService.LoadLocal();
        }
        if (client == null || !client.connected || configReceived) return;
        requestTimer -= deltaTime;
        if (requestTimer > 0) return;
        requestTimer = 3f;
        TLConfigBridge.ClientRequestUpdate();
    }

    public static void OnConfigReceived() => configReceived = true;

    public static void Dispose()
    {
        TLConfigMenu.Close();
        if (entryButton != null) entryButton.RectTransform.Parent = null;
        entryButton = null;
        pauseMenu = null;
        connectedClient = null;
        configReceived = false;
    }
}


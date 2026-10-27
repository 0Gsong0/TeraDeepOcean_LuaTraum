using Barotrauma.Networking;

namespace TeraDeepOcean;

[NetworkSerialize]
internal readonly record struct TLCybNoticeMessage(
    ushort TargetId, string HeaderTag, string TextTag, string IconStyle, string EventSpriteId, bool Conversation) : INetSerializableStruct;

// 安装、维修和伤害由权威端执行；这个通道只发送安装提示，不接受客户端修改义体状态。
internal static class TLCybNoticeBridge
{
    internal static void RegisterHandlers()
    {
#if CLIENT
        Plugin.Network.RegisterHandler<TLNetworkHeader, TLCybNoticeMessage>(TLNetworkHeader.CybNotice, Receive);
#endif
    }

    internal static void ShowInstall(Character target, string headerTag, string textTag, string iconStyle = "", string eventSpriteId = "")
        => Send(target, new(target.ID, headerTag, textTag, iconStyle, eventSpriteId, false));

    internal static void ShowConversation(Character target, string textTag, string eventSpriteId)
        => Send(target, new(target.ID, "", textTag, "", eventSpriteId, true));

    private static void Send(Character target, TLCybNoticeMessage message)
    {
#if SERVER
        if (GameMain.Server == null) return;
        foreach (Client client in GameMain.Server.ConnectedClients)
        {
            if (client.Character == target)
                Plugin.Network.SendToClient(client, TLNetworkHeader.CybNotice, message);
        }
#else
        if (!GameMain.IsMultiplayer) Receive(message);
#endif
    }

#if CLIENT
    private static void Receive(TLCybNoticeMessage message)
    {
        Character? target = Character.Controlled;
        if (target == null || target.ID != message.TargetId) return;
        if (message.Conversation)
            TLCybNotice.ShowConversationNotice(target, TextManager.Get(message.TextTag), message.EventSpriteId);
        else
            TLCybNotice.ShowMessageNotice(TextManager.Get(message.HeaderTag), TextManager.Get(message.TextTag),
                iconStyle: message.IconStyle, eventSpriteId: message.EventSpriteId, tag: "TLCybInstallNotice");
    }
#endif
}

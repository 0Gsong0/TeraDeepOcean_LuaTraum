using System;
using System.Collections.Generic;
using System.Text;
using static Barotrauma.ConversationAction;
using static Microsoft.Xna.Framework.Graphics.VertexDeclaration;

namespace TeraDeepOcean
{
    public static class TLCybNotice
    {
        private static ushort nextDialogId;
        public static void ShowConversationNotice(Character? target, LocalizedString text, string eventSpriteId = "")
        {
            if (GameMain.NetworkMember is { IsServer: true }) { return; }

            nextDialogId++;
            Character speaker = target ?? Character.Controlled;
            if (speaker == null) { return; }
            ConversationAction.CreateDialog(text, speaker, Array.Empty<string>(), Array.Empty<int>(), eventSpriteId, nextDialogId, false, DialogTypes.Regular);
        }
        public static void ShowMessageNotice(LocalizedString header, LocalizedString text, string iconStyle = "", string eventSpriteId = "", string tag = "TLCybNotice", float autoCloseTime = 30)
        {
            if (GameMain.NetworkMember is { IsServer: true }) { return; }

            Sprite? backgroundIcon = EventSet.GetEventSprite(eventSpriteId);

            double closeAt = Timing.TotalTime + autoCloseTime;

            new GUIMessageBox(
                RichString.Rich(TextManager.ParseInputTypes(header.ToString(), useColorHighlight: true)),
                RichString.Rich(TextManager.ParseInputTypes(text.ToString(), useColorHighlight: true)),
                Array.Empty<LocalizedString>(),
                type: GUIMessageBox.Type.Tutorial,
                tag: tag,
                iconStyle: iconStyle,
                backgroundIcon: backgroundIcon,
                autoCloseCondition: () => Timing.TotalTime > closeAt,
                hideCloseButton: false)
            {
                FlashOnAutoCloseCondition = true
            };
        }
        public static void ShowInstallConversation(Character? target, TLCybData data)
        {
            if (Character.Controlled != target) return;
            if (data == null || string.IsNullOrWhiteSpace(data.InstallNoticeTextTag)) { return; }
            ShowConversationNotice(target, TextManager.Get(data.InstallNoticeTextTag), data.InstallNoticeBackImageTag);
        }

        public static void ShowInstallMessage(Character? target, TLCybData data, float autoCloseTime = 30)
        {
            if (Character.Controlled != target) return;
            if (data == null) { return; }

            string headerTag = string.IsNullOrWhiteSpace(data.InstallNoticeHeaderTag) ? data.ItemId : data.InstallNoticeHeaderTag;
            string textTag = string.IsNullOrWhiteSpace(data.InstallNoticeTextTag) ? data.InstallAffId : data.InstallNoticeTextTag;

            

            ShowMessageNotice(
                TextManager.Get(headerTag),
                TextManager.Get(textTag),
                eventSpriteId: data.InstallNoticeBackImageTag,
                tag: "TLCybInstallNotice",
                autoCloseTime:autoCloseTime);
        }
        public static void ShowInstallMessage(Character? target, string header, string text,string iconStyle, float autoCloseTime = 30)
        {
            if (Character.Controlled != target) return;

            ShowMessageNotice(
                TextManager.Get(header),
                TextManager.Get(text),
                iconStyle: iconStyle,
                tag: "TLCybInstallNotice",
                autoCloseTime: autoCloseTime);
        }
    }
}

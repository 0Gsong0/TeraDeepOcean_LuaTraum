using System;
using System.Collections.Generic;
using System.Text;
using static Barotrauma.ConversationAction;

namespace TeraDeepOcean
{
    public static class TLCybNotice
    {
        private static ushort nextDialogId;
        private static void ShowConversationNotice(Character? target, LocalizedString text, string eventSpriteId = "")
        {
            if (GameMain.NetworkMember is { IsServer: true }) { return; }

            nextDialogId++;
            Character speaker = target ?? Character.Controlled;
            if (speaker == null) { return; }
            ConversationAction.CreateDialog(text, speaker, Array.Empty<string>(), Array.Empty<int>(), eventSpriteId, nextDialogId, false, DialogTypes.Regular);
        }
    }
}

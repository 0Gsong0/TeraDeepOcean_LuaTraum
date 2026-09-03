using Barotrauma.Networking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeraDeepOcean
{
    public static partial class TLConfigBridge
    {
        public static void OpenTLConfigMenu(GUIFrame frame, GUIListBox list, GUILayoutGroup buttonRow)
        {
            TLConfigMenu.Open(frame, list, buttonRow);
        }
    }
}

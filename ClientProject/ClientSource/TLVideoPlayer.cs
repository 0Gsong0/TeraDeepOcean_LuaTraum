using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeraDeepOcean
{
    public static class TLVideoPlayer
    {
        private static bool isLoaded;
        public static void PlayLocalVideoFromFullPath(string fullPath)
        {
            if (GameMain.NetworkMember is { IsServer: true }) { return; }
            string contentPath = Path.GetDirectoryName(fullPath).Replace('\\', '/') + "/";
            string fileName = Path.GetFileName(fullPath);

            DebugConsole.NewMessage("正在播放："+contentPath + ":" + fileName);
            ObjectiveManager.VideoPlayer.LoadContent(
                contentPath: contentPath,
                videoSettings: new VideoPlayer.VideoSettings(fileName),
                textSettings: null,
                contentId: "tl_local_video".ToIdentifier(),
                startPlayback: true);
            var field = AccessTools.Field(typeof(VideoPlayer), "okButton");
            if (field?.GetValue(ObjectiveManager.VideoPlayer) is GUIButton button)
            {
                button.Visible = false;
                button.Enabled = false;
            }
            isLoaded = true;
        }
        public static void PlayLocalVideoFromMod(string relativePath)
        {
            string fullPath = Path.Combine(Plugin.ModDir, relativePath);
            PlayLocalVideoFromFullPath(fullPath);
        }
        public static void Update()
        {
            if (!isLoaded) { return; }

            ObjectiveManager.VideoPlayer.Update();
            ObjectiveManager.VideoPlayer.AddToGUIUpdateList(order: 100);
        }
        public static void StopLocalVideo()
        {
            ObjectiveManager.VideoPlayer.Stop();
            isLoaded = false;
        }
    }
}

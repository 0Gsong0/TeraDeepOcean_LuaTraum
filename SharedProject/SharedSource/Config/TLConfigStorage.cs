using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    //读写json文件
    public static class TLConfigStorage
    {
        private static string ConfigDirectory =>
            Path.Combine(SaveUtil.DefaultSaveFolder, "ModConfigs");

        private static string ConfigPath =>
            Path.Combine(ConfigDirectory, "TeraDeepOcean.json");

        public static void Load()
        {
            TLConfigService.ResetToDefaults();
            if (!File.Exists(ConfigPath)) return;
            try
            {
                string json = File.ReadAllText(ConfigPath);
                if (!string.IsNullOrWhiteSpace(json)) TLConfigService.ApplyJson(json);
            }
            catch (IOException e) { Plugin.DebugConsole.NewMessage($"读取配置失败：{e.Message}"); }
            catch (UnauthorizedAccessException e) { Plugin.DebugConsole.NewMessage($"读取配置失败：{e.Message}"); }
        }
        public static void Save()
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, TLConfigService.ToJson());
        }
    }
}

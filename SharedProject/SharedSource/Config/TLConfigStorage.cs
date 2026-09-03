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
            string json = File.ReadAllText(ConfigPath);
            if (json.IsNullOrEmpty()) return;
            TLConfigService.ApplyJson(json);
        }
        public static void Save()
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, TLConfigService.ToJson());
        }
    }
}

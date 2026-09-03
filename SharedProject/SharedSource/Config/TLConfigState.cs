using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    //持有配置的真实数据

    /// <summary>
    /// 持有配置的真实底层数据
    /// </summary>
    public sealed class TLConfigState
    {
        private readonly Dictionary<string, object> Values = new();
        public TLConfigState()
        {
            RestToDefaults();
        }
        public void RestToDefaults()
        {
            Values.Clear();
            foreach(TLConfigDataEntry data in TLConfigData.ConfigDatas)
            {
                Values[data.Key] = data.DefaultValue;
            }
        }
        public int GetInt(string key)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null || entry.VauleType != TLConfigVauleType.Int) return 0;
            var vaule = Values.TryGetValue(key, out object? stored) ? stored : entry.DefaultValue;
            return Convert.ToInt32(vaule);
        }
        public float GetFloat(string key)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null || entry.VauleType != TLConfigVauleType.Float) return 0;
            var vaule = Values.TryGetValue(key, out object? stored) ? stored : entry.DefaultValue;
            return Convert.ToSingle(vaule);
        }
        public bool GetBool(string key)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null || entry.VauleType != TLConfigVauleType.Bool) return false;
            var vaule = Values.TryGetValue(key, out object? stored) ? stored : entry.DefaultValue;
            return Convert.ToBoolean(vaule);
        }
        public string GetString(string key)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null || entry.VauleType != TLConfigVauleType.String) return "";
            var vaule = Values.TryGetValue(key, out object? stored) ? stored : "";
            return Convert.ToString(vaule);
        }
        public void SetInt(string key,int vaule)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null || entry.VauleType != TLConfigVauleType.Int) return;
            Values[key] = Math.Clamp(vaule, (int)entry.Min, (int)entry.Max);
        }
        public void SetFloat(string key, float vaule)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null || entry.VauleType != TLConfigVauleType.Float) return;
            Values[key] = Math.Clamp(vaule, entry.Min, entry.Max);
        }
        public void SetBool(string key, bool vaule)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null || entry.VauleType != TLConfigVauleType.Bool) return;
            Values[key] = vaule;
        }
        public void SetString(string key, string vaule)
        {
            TLConfigDataEntry? entry = TLConfigData.Find(key);
            if (entry == null || entry.VauleType != TLConfigVauleType.String) return;
            Values[key] = vaule;
        }
        public IReadOnlyDictionary<string,object> GetAll()
        {
            return Values;
        }
    }
}

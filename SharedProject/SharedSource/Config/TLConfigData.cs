using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 可配置项目的表单
    /// </summary>
    public static class TLConfigData
    {
        /// <summary>
        /// 可配置项目的表单
        /// </summary>
        public static TLConfigDataEntry[] ConfigDatas =
        {
            new TLConfigDataEntry(
                "TL_TLUpdateInterval",
                TLConfigVauleType.Int,
                2,1,5,
                "config.TL_TLUpdateInterval",
                "config.des.TL_TLUpdateInterval"),
            new TLConfigDataEntry(
                "TL_TLLateUpdateInterval",
                TLConfigVauleType.Int,
                5,1,10,
                "config.TL_TLLateUpdateInterval",
                "config.des.TL_TLLateUpdateInterval"),
            new TLConfigDataEntry(
                "TL_TLDebugModel",
                TLConfigVauleType.Bool,
                false,0,1,
                "config.TL_TLDebugModel",
                "config.des.TL_TLDebugModel"),

        };
        public static TLConfigDataEntry? Find(string key)
        {
            foreach (TLConfigDataEntry data in ConfigDatas)
            {
                if(data.Key == key)
                {
                    return data;
                }
            }
            return null;
        }
    }
}

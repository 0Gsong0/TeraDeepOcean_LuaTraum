using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    //可配置项目的真实条目，也是数据类型的标准
    public enum TLConfigVauleType
    {
        Bool,
        Int,
        Float,
        String,
    }
    /// <summary>
    /// 单项配置条目
    /// </summary>
    public sealed class TLConfigDataEntry
    {
        public string Key { get; }
        public TLConfigVauleType VauleType { get; }
        public object DefaultValue { get; }
        public float Min{ get; }
        public float Max{ get; }
        public string NameTextTag { get; }
        public string DescriptionTextTag { get; }

        public TLConfigDataEntry(string key,TLConfigVauleType vauleType,object defaultValue,float min,float max,string nameTextTag,string descriptionTextTag)
        {
            Key = key;
            VauleType = vauleType;
            DefaultValue = defaultValue;
            Min = min;
            Max = max;
            NameTextTag = nameTextTag;
            DescriptionTextTag = descriptionTextTag;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 负责确定 RTS 可以活动的前哨站和所有停靠潜艇。
    /// </summary>
    public static class TLRtsRoundSubContext
    {
        private static readonly HashSet<ushort> allowedSubId = new();
        public static IReadOnlyCollection<ushort> AllowSubId = allowedSubId;
        public static ushort OutPostId { get; private set; } = Entity.NullEntityID;
        public static bool IsValid => OutPostId != Entity.NullEntityID && allowedSubId.Count > 0 && Level.IsLoadedOutpost;

        /// <summary>
        /// 建立或刷新前哨停靠网络。
        /// </summary>
        /// <returns></returns>
        public static bool Refresh()
        {
            if(Level.Loaded == null || !Level.IsLoadedOutpost)
            {
                Clear();
                return false;
            }
            Submarine outpost = Submarine.Loaded.FirstOrDefault(s => s.Info.IsOutpost);
            if(outpost == null)
            {
                Clear();
                return false;
            }
            OutPostId = outpost.ID;
            allowedSubId.Clear();

            /*
             * GetConnectedSubs 会返回：
             * - 前哨自身
             * - 与前哨直接连接的潜艇
             * - 经其他潜艇间接连接的潜艇
             */
            foreach (var sub in outpost.GetConnectedSubs())
            {
                if (sub == null || sub.Removed) continue;
                allowedSubId.Add(sub.ID);
            }
            // 防止特殊情况下 GetConnectedSubs 未包含自身。
            allowedSubId.Add(outpost.ID);
            return true;
        }
        /// <summary>
        /// 判断潜艇是否在 RTS 活动范围内。
        /// </summary>
        /// <param name="sub"></param>
        /// <returns></returns>
        public static bool Contains(Submarine? sub)
        {
            return sub != null && allowedSubId.Contains(sub.ID);
        }
        public static bool Contains(ushort subId)
        {
            return subId != Entity.NullEntityID && allowedSubId.Contains(subId);
        }
        public static Submarine? GetOutPost()
        {
            return Entity.FindEntityByID(OutPostId) as Submarine;
        }
        public static void Clear()
        {
            allowedSubId.Clear();
            OutPostId = Entity.NullEntityID;
        }
    }
}

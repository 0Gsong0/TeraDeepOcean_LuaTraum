using Barotrauma.Items.Components;
using Barotrauma.Networking;
using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;

namespace TeraDeepOcean
{
    public sealed class TLMagazineComponent : ItemComponent, IServerSerializable
    {
        private const int DefaultCapacity = 30;
        private const int MaximumCapacity = 999;
        // 预制体可用这个属性配置初始装填，存档用它记录实际顺序。
        private const string RoundsAttribute = "rounds";
        // 白名单存放散弹物品的预制体 ID，例如 tl_loose_556nato_ball。
        private readonly Identifier[] allowedAmmo;
        /// <summary>
        /// 实际装填弹药
        /// 单机或服务器使用的完整供弹顺序。每发只占一个 Identifier。
        /// </summary>
        private readonly List<Identifier> rounds = new();
        // 联机客户端从组件事件收到的匣顶弹种。Empty 表示空匣。
        private Identifier syncedTopAmmo = Identifier.Empty;
        /// <summary>弹匣容量，供装填逻辑使用。</summary>
        public int Capacity { get; }
        /// <summary>单机或服务器装填时使用的剩余空间。</summary>
        internal int FreeSpace => Capacity - rounds.Count;
#if CLIENT
        /// <summary>在潜艇编辑器中按匣底到匣顶编辑弹种，最后一项是匣顶。</summary>
        [Editable]
        public string EditorRounds
        {
            get => string.Join(",", rounds);
            set
            {
                if (Screen.Selected is not SubEditorScreen || !CanChangeContents())
                {
                    return;
                }

                // 接受中英文逗号。清空文本框会清空弹匣。
                ReplaceRounds((value ?? string.Empty).Replace('，', ',').ToIdentifiers());
                SyncTopAmmo();
            }
        }
#endif
        /// <summary>当前匣顶弹种，供退弹、开火和界面读取。</summary>
        public Identifier TopAmmoType
        {
            get
            {
#if CLIENT
                if (GameMain.NetworkMember is { IsClient: true })
                {
                    return syncedTopAmmo;
                }
#endif
                return rounds.Count == 0 ? Identifier.Empty : rounds[^1];
            }
        }
        /// <summary>读取弹匣容量、兼容弹种和初始装填顺序。</summary>
        public TLMagazineComponent(Item item, ContentXElement element) : base(item, element)
        {
            Capacity = Math.Clamp(element.GetAttributeInt("capacity", DefaultCapacity), 1, MaximumCapacity);
            allowedAmmo = element.GetAttributeIdentifierArray("allowedammo", Array.Empty<Identifier>());
            ReadRounds(element);
        }
        /// <summary>从预制体或存档读取弹序。缺少属性时保留已有初始值。</summary>
        private void ReadRounds(ContentXElement element)
        {
            if (GameMain.NetworkMember is { IsClient: true } ||element.Element.Attribute(RoundsAttribute) is null)
            {
                return;
            }

            ReplaceRounds(element.GetAttributeIdentifierArray(RoundsAttribute, Array.Empty<Identifier>()));
        }
        /// <summary>按输入顺序保留兼容弹种，达到容量后停止。</summary>
        private void ReplaceRounds(IEnumerable<Identifier> ammoTypes)
        {
            rounds.Clear();

            foreach (Identifier ammoType in ammoTypes)
            {
                if (rounds.Count >= Capacity)
                {
                    break;
                }

                if (Accepts(ammoType))
                {
                    rounds.Add(ammoType);
                }
            }
        }
        /// <summary>将指定数量的同种弹压到匣顶。容量不足时整笔失败。</summary>
        /// <param name="ammoType">来源散弹堆的预制体 ID。</param>
        /// <param name="count">本次实际要压入的发数。</param>
        /// <param name="synchronize">转移两端一起提交时，可延迟到提交完成后同步。</param>
        public bool TryPushRounds(Identifier ammoType, int count, bool synchronize = true)
        {
            if (!CanChangeContents() || count <= 0 || count > FreeSpace || !Accepts(ammoType))
            {
                return false;
            }
            int roundsToLoad = Math.Min(count, FreeSpace);
            for (int i = 0; i < roundsToLoad; i++)
            {
                rounds.Add(ammoType);
            }
            if (synchronize)
            {
                SyncTopAmmo();
            }
            return true;
        }
        /// <summary>取出匣顶一发。调用方先确认该发可以放入背包或用于开火。</summary>
        /// <param name="ammoType">成功取出的弹种，失败时为空。</param>
        /// <param name="synchronize">是否立即刷新匣顶显示。</param>
        public bool TryPopRound(out Identifier ammoType, bool synchronize = true)
        {
            ammoType = Identifier.Empty;
            if (!CanChangeContents() || rounds.Count == 0)
            {
                return false;
            }
            int topIndex = rounds.Count - 1;
            ammoType = rounds[topIndex];
            rounds.RemoveAt(topIndex);
            if (synchronize)
            {
                SyncTopAmmo();
            }
            return true;
        }
        /// <summary>将完整供弹顺序写入物品存档，空匣也明确保存为空。</summary>
        public override XElement Save(XElement parentElement)
        {
            // 原版多人战役向客户端传输存档文件。
            // 匣顶可见性由界面和组件事件控制，读取存档文件仍可看到保存的顺序。
            XElement componentElement = base.Save(parentElement);
            componentElement.SetAttributeValue(RoundsAttribute, string.Join(",", rounds));
            return componentElement;
        }
        /// <summary>恢复同一物品的供弹顺序。替换物品预制体时采用新预制体的初始值。</summary>
        public override void Load(ContentXElement? componentElement, bool usePrefabValues, IdRemap idRemap, bool isItemSwap)
        {
            base.Load(componentElement, usePrefabValues, idRemap, isItemSwap);
            if (!isItemSwap && componentElement is not null)
            {
                ReadRounds(componentElement);
            }
        }
        /// <summary>原版复制弹匣物品时，将供弹顺序复制到独立列表。</summary>
        public override void Clone(ItemComponent original)
        {
            base.Clone(original);

            if (CanChangeContents() && original is TLMagazineComponent magazine)
            {
                rounds.Clear();
                rounds.AddRange(magazine.rounds);
            }
        }
        /// <summary>检查弹种是否在此弹匣的兼容列表中。</summary>
        internal bool Accepts(Identifier ammoType) => !ammoType.IsEmpty && allowedAmmo.Contains(ammoType);
        /// <summary>只有单机或服务器能修改真实供弹顺序。</summary>
        private bool CanChangeContents() => GameMain.NetworkMember is not { IsClient: true } && !Item.Removed && !Item.IsInRemoveQueue;
        /// <summary>服务器排入匣顶事件，单机直接刷新当前提示。</summary>
        internal void SyncTopAmmo()
        {
#if SERVER
            if (GameMain.Server is not null && CanChangeContents())
            {
                Item.CreateServerEvent(this);
            }
#elif CLIENT
            if (Inventory.SelectedSlot is { } slot && slot.Item == Item)
            {
                slot.ForceTooltipRefresh = true;
            }
#endif
        }
#if SERVER
        /// <summary>发送时读取当前匣顶，只写一个弹种 ID。</summary>
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData? extraData = null)
        {
            msg.WriteIdentifier(TopAmmoType);
        } 
#endif
        /// <summary>接收服务器的匣顶弹种，并刷新正在显示的物品提示。</summary>
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            syncedTopAmmo = msg.ReadIdentifier();
            SyncTopAmmo();
        }
#if CLIENT
        /// <summary>在物品悬停提示中显示最上面一发的名称。</summary>
        public override void AddTooltipInfo(ref LocalizedString name, ref LocalizedString description)
        {
            Identifier topAmmoType = TopAmmoType;
            if (topAmmoType.IsEmpty)
            {
                description += "\n匣顶弹药类型：空";
                return;
            }
            // 优先显示散弹预制体的本地化名称，找不到时显示其 ID。
            string ammoName = ItemPrefab.Prefabs.TryGet(topAmmoType, out ItemPrefab? prefab)
                ? prefab.Name.Value
                : topAmmoType.Value;
            description += $"\n匣顶弹药类型：{ammoName}";
        }
#endif
    }
}

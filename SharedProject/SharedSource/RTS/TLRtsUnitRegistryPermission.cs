using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 单个 RTS 单位的动态注册信息。
    /// </summary>
    public class TLRtsUnitRegistration
    {
        public ushort CharacterId{ get; init; }
        /// <summary>
        /// 是否允许被 RTS 选择和指挥。
        /// </summary>
        public bool CanControl{ get; set; }
        public CharacterTeamType TeamId{ get; set; }
        /// <summary>
        /// 自定义单位类型
        /// </summary>
        public Identifier UnitClass { get; set; } = Identifier.Empty;
        public bool IsSpawnByRts { get; set; }
    }
    /// <summary>
    /// 动态控制资格系统
    /// </summary>
    public static class TLRtsUnitRegistryPermission
    {
        private static readonly Dictionary<ushort, TLRtsUnitRegistration> registrations = new();
        public static IReadOnlyDictionary<ushort,TLRtsUnitRegistration> Registrations => registrations;
        /// <summary>
        /// 单位注销事件。RTS 系统用它清理正在执行的命令。
        /// </summary>
        public static event Action<ushort>? UnitUnregistered;
        /// <summary>
        /// 动态授予一个角色 RTS 控制资格。
        /// </summary>
        /// <param name="character"></param>
        /// <param name="unitClass"></param>
        /// <param name="isSpawnByRts"></param>
        /// <returns></returns>
        public static bool Register(Character? character, Identifier unitClass = default, bool isSpawnByRts = false)
        {
            if (!IsSupportedAIUnit(character)) return false;
            registrations[character.ID] = new()
            {
                CharacterId = character.ID,
                CanControl = true,
                TeamId = character.TeamID,
                UnitClass = unitClass,
                IsSpawnByRts = isSpawnByRts
            };
            return true;
        }
        /// <summary>
        /// 完全撤销角色的 RTS 身份。
        /// </summary>
        /// <param name="character"></param>
        /// <returns></returns>
        public static bool Unregister(Character? character)
        {
            return character != null && Unregister(character.ID);
        }
        public static bool Unregister(ushort characterId)
        {
            bool removed = registrations.Remove(characterId);
            if (removed)
            {
                UnitUnregistered?.Invoke(characterId);
            }
            return removed;
        }
        /// <summary>
        /// 临时启用或禁止控制。注册信息仍然保留。
        /// </summary>
        /// <param name="character"></param>
        /// <param name="controllable"></param>
        /// <returns></returns>
        public static bool SetControllable(Character? character,bool controllable)
        {
            if (character == null) return false;
            if(!registrations.TryGetValue(character.ID,out TLRtsUnitRegistration registration))
            {
                if (!controllable) return false;
                return Register(character);
            }
            registration.CanControl = controllable;
            if(!controllable) UnitUnregistered?.Invoke(character.ID);
            return true;
        }
        /// <summary>
        /// 已被注册
        /// </summary>
        /// <param name="character"></param>
        /// <returns></returns>
        public static bool IsRegistered(Character? character)
        {
            return character != null && registrations.ContainsKey(character.ID);
        }
        public static bool IsControllable(Character? character)
        {
            if (!IsSupportedAIUnit(character)) return false;
            return registrations.TryGetValue(character.ID,out TLRtsUnitRegistration registration) && registration.CanControl;
        }
        public static bool TryGetRegistration(Character? character,out TLRtsUnitRegistration? registration)
        {
            registration = null;
            return character != null && registrations.TryGetValue(character.ID, out registration);
        }
        /// <summary>
        /// 删除已经不存在的角色记录。
        /// </summary>
        public static void RemoveInvalidEntries()
        {
            foreach(ushort characterId in registrations.Keys.ToArray())
            {
                if(Entity.FindEntityByID(characterId) is not Character character || character.IsDead || character.Removed)
                {
                    Unregister(characterId);
                }
            }
        }
        /// <summary>
        /// 清空所有角色记录。
        /// </summary>
        public static void Clear()
        {
            ushort[] keys = registrations.Keys.ToArray();
            registrations.Clear();
            foreach (ushort key in keys)
            {
                UnitUnregistered?.Invoke(key);
            }
        }
        private static bool IsSupportedAIUnit(Character? character)
        {
            if(character == null|| character.IsDead || character.Removed || !character.Enabled)
            {
                return false;
            }
            return character.AIController switch
            {
                EnemyAIController enemyAI => enemyAI.Enabled,
                HumanAIController humanAI => humanAI.Enabled,
                _ => false,
            };
        }
    }
}

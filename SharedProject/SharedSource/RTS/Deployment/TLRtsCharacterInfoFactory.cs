using System.Reflection;

namespace TeraDeepOcean;

/// <summary>
/// 兼容引用包中的 npcIdentifier 与实际游戏中的 humanPrefabIds 构造参数。
/// 不直接引用旧构造函数，否则 JIT 会在进入部署方法的 try 之前抛出 MissingMethodException。
/// 人类 NPC 仍使用 HumanPrefab.CreateCharacterInfo；此入口仅用于带角色资料的生物。
/// </summary>
internal static class TLRtsCharacterInfoFactory
{
    private static readonly Lazy<ConstructorInfo> Constructor = new(FindConstructor);

    private static ConstructorInfo FindConstructor()
    {
        Type[] prefix =
        {
            typeof(Identifier), typeof(string), typeof(string),
            typeof(Either<Job, JobPrefab>), typeof(int), typeof(Rand.RandSync)
        };
        foreach (var constructor in typeof(CharacterInfo).GetConstructors())
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length != 7) continue;
            if (!parameters.Take(6).Select(p => p.ParameterType).SequenceEqual(prefix)) continue;
            if (parameters[6].ParameterType != typeof(Identifier) &&
                parameters[6].ParameterType != typeof((Identifier, Identifier))) continue;
            if (parameters.Skip(1).All(p => p.IsOptional)) return constructor;
        }
        throw new MissingMethodException("当前游戏的 CharacterInfo 构造签名不受 RTS 兼容入口支持，请更新游戏引用包。");
    }

    public static CharacterInfo Create(Identifier species)
    {
        // 让运行时按照实际构造函数的默认值补齐参数，保留游戏自己的初始化行为。
        object?[] arguments = { species, Type.Missing, Type.Missing, Type.Missing,
            Type.Missing, Type.Missing, Type.Missing };
        return (CharacterInfo)Constructor.Value.Invoke(arguments);
    }
}

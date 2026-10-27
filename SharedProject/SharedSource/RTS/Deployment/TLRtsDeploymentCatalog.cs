namespace TeraDeepOcean;

/// <summary>原 rtsdeployment.lua 中的内置部署定义。客户端和服务器加载相同目录。</summary>
internal static class TLRtsDeploymentCatalog
{
    public static void RegisterDefaults(ContentPackage package)
    {
        Register(package, new TLRtsDeploymentPrefabDefinition
        {
            Identifier = "teradeepocean:test_crawler",
            DisplayName = "测试爬行者",
            Description = "用于测试 RTS 生物部署。",
            UnitType = TLRtsDeploymentUnitType.Creature,
            SpeciesIdentifier = "crawler"
        });
        Register(package, new TLRtsDeploymentPrefabDefinition
        {
            Identifier = "teradeepocean:test_escort",
            DisplayName = "泰拉护航特战队",
            Description = "测试用人类 RTS 单位，使用模组现有 NPC 预设和装备。",
            UnitType = TLRtsDeploymentUnitType.HumanNpc,
            NpcSetIdentifier = "TLnpc",
            NpcIdentifier = "TLnpc2"
        });
    }

    private static void Register(ContentPackage package, TLRtsDeploymentPrefabDefinition definition)
    {
        if (!TLRtsDeploymentBridge.RegisterPrefab(package, definition, out string error))
            Plugin.DebugConsole.NewMessage($"[TLRTS部署] {definition.Identifier} 注册失败：{error}", Color.Red);
    }
}

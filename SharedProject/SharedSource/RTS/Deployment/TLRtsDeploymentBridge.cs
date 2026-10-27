using System.Text.Json;

namespace TeraDeepOcean;

/// <summary>C# 部署注册入口。请在游戏主线程、内容加载完成后调用。</summary>
public static class TLRtsDeploymentBridge
{
    private static bool initialized;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static void Init() => initialized = true;

    // OnContentLoaded 执行时 EnabledPackages 可能还未加入当前内容包，
    // 此时使用官方服务提供的 ContentPackage，避免按名称查找而漏掉注册。
    public static bool RegisterPrefab(ContentPackage sourcePackage, TLRtsDeploymentPrefabDefinition definition, out string error)
    {
        if (!initialized || sourcePackage is null)
        {
            error = "部署注册入口未初始化，或来源内容包为空。";
            return false;
        }
        return TLRtsDeploymentRegistry.TryRegisterPrepared(sourcePackage.Name, definition, out error);
    }

    // 类型化注册不需要先转成 JSON，返回值就是最终注册结果。
    public static bool RegisterPrefab(string sourcePackageName, TLRtsDeploymentPrefabDefinition definition, out string error)
    {
        if (!initialized)
        {
            error = "RTS 部署注册入口尚未初始化。";
            return false;
        }
        return TLRtsDeploymentRegistry.TryRegister(sourcePackageName, definition, out error);
    }

    // 保留 JSON 作为可选的数据格式，完全不依赖脚本运行时。
    public static bool RegisterJson(string sourcePackageName, string json, out string error)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<TLRtsDeploymentPrefabDefinition>(json, JsonOptions);
            if (definition is null)
            {
                error = "部署定义不能为空。";
                return false;
            }
            return RegisterPrefab(sourcePackageName, definition, out error);
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static void DebugPrintRegistry()
    {
        foreach (var prefab in TLRtsDeploymentRegistry.GetAll())
            Plugin.DebugConsole.NewMessage($"[TLRTS部署] {prefab.Identifier}：{prefab.DisplayName}（{prefab.SourcePackageName}）", Color.Cyan);
    }

    public static void Dispose() => initialized = false;
}

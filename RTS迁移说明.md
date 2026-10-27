# RTS 系统迁移说明

旧项目：`E:\ModDev\TeraDeepOcean_LuaTraum_OLD`。本次只修改新项目。

## 已迁入的功能

- F8 战术视角、角色控制恢复、单选/框选、Shift 多选、命令标记。
- 常规分散、密集行进、排枪重叠三种队形；移动、攻击、G 方向射击、释放全部 AI。
- 人类与生物 AI 控制、前哨站停靠范围、单位注册与友军规则。
- 多人指挥权申请/释放、状态快照、可控单位列表、命令回执。
- 人类/生物部署菜单、位置校验、战役费用校验、服务器生成和装备发放。
- 爬行者和泰拉护航特战队两个原有测试部署预设，默认费用仍为 0 马克。

活动范围沿用旧版：前哨站及其停靠网络；部署位置沿用旧版的己方主潜艇限制。

## 主要文件

| 文件 | 用途 |
| --- | --- |
| `SharedProject/SharedSource/RTS/TLRtsSystem.cs` | AI 指令、编队、Harmony 补丁与回合状态 |
| `ClientProject/ClientSource/RTS/TLRtsClient.cs` | 输入、界面、自由视角与断线清理 |
| `SharedProject/SharedSource/TLNetworkMessages.cs` | 全插件共用的消息头，配置 1–3，RTS 10–21 |
| `SharedProject/SharedSource/RTS/TLRtsNetworkTransport.cs` | 官方 IGameNetwork 的注册、发送与接收 |
| `SharedProject/SharedSource/RTS/TLRtsNetwork.cs` | 服务器权限校验、指令处理、客户端状态 |
| `SharedProject/SharedSource/RTS/Deployment/TLRtsDeploymentCatalog.cs` | C# 部署预设入口，替代 rtsdeployment.lua |
| `SharedProject/SharedSource/RTS/Deployment/TLRtsDeploymentBridge.cs` | 其他 C# 模组的部署注册接口 |
| `ContentPackageBuilder/Content/RTS/` | RTS 入口物品、NPC、装备定义及相关文本 |

## 网络流程

`Plugin.Init()` 只调用一次 `Network.RegisterNetworkHeaders<TLNetworkHeader>()`，随后注册配置和 RTS 的收包处理器。官方服务每个插件只允许一套消息头，不能再为 RTS 注册第二个枚举。

客户端操作 → C# 创建请求对象 → `JsonSerializer.Serialize` → `TLRtsJsonMessage` → `Plugin.Network.Send` → 服务器处理器取得真实 `Client sender` → 检查指挥权、序号、单位、位置或费用 → 执行 → `SendToClient` / `Broadcast` → 客户端更新。

JSON 是保留的数据格式，不是 Lua 依赖。JSON 的编码、解码、网络发送和接收均在 C# 内完成。释放指挥权与请求快照使用 `TLRtsEmptyMessage`，不发送无意义的 bool。

只有指挥官状态广播给所有客户端；申请结果、命令结果和单位列表单独发给接收者。客户端不能通过 JSON 指定自己的发送者身份。

客户端收到快照后才停止请求重试。更换服务器或断开连接时清空旧 Revision、Sequence 和指挥官状态。

## 加载与卸载

- `Init()`：取得内容包、安装 AI/界面补丁、注册网络处理器、初始化部署资源处理器。
- `OnContentLoaded()`：注册 C# 部署预设。使用官方服务取得的 `ContentPackage`，不要求此刻该包已经出现在 `EnabledPackages` 列表。
- `GameSession.Update(float)`：更新 RTS AI 状态及服务器指挥官有效性。
- `GUI.Update(float)`：处理 RTS 输入、界面和快照重试。
- 新版回合开始补丁使用 `StartRound(LevelData, bool)`；旧版四参数签名不能直接照搬。
- `Dispose()`：退出战术视角、释放界面资源与事件订阅、还原 AI 控制、清空注册表；官方服务负责释放 Harmony 补丁和网络处理器。

## 注册自己的部署单位

本模组直接编辑 `TLRtsDeploymentCatalog.cs`。其他 C# 插件可在自己的 `OnContentLoaded()` 中调用下面接口；`package` 应在自己的 `Init()` 中通过 `IContentPackageRetrievalService` 获取并保存。

```csharp
var definition = new TLRtsDeploymentPrefabDefinition
{
    Identifier = "yourmod:guard_crawler",
    DisplayName = "护卫爬行者",
    Description = "部署一只同阵营爬行者。",
    UnitType = TLRtsDeploymentUnitType.Creature,
    SpeciesIdentifier = "crawler",
    Costs = new()
    {
        new() { ResourceIdentifier = "marks", Amount = 100 }
    }
};

bool success = TLRtsDeploymentBridge.RegisterPrefab(package, definition, out string error);
```

请在游戏主线程注册，客户端和服务器都需要相同定义。接口返回最终成功/失败结果；小型 C# 对象无需再经 Lua table、JSON 和后台任务绕行。需要从 JSON 数据注册时，可调用 `RegisterJson(packageName, json, out error)`，该名称形式须在来源内容包已启用后调用。

## 内容资源

已提取原指挥终端、TLnpc/TLnpc2、原 NPC 的身份证/服装/步枪/弹匣/弹药，以及两个增益、11 个贴图和音效文件。旧身份证上没有对应实现的 `idcardplus1B.onUse` LuaHook 已移除，现有 XML 状态效果保留。

后续迁移旧项目整个物品库时，应合并这里已提取的同名 identifier，避免在 RtsItems.xml 和旧物品文件中重复定义。无需启用旧项目或安装 LuaCs 来提供 RTS 资源。

## 验证与边界

- Windows 客户端/服务器 Debug 构建通过。
- Windows、Linux、Mac 客户端/服务器 Release 构建通过。
- 使用当前安装游戏的实际客户端及 DedicatedServer 序列化器验证：空请求、中文 JSON、48 KB 级字符串消息编码/解码一致。
- 在独立测试进程中，实际游戏程序集上的 7 个 RTS 核心 Harmony 目标安装成功；客户端 3 个 GUI 目标安装成功。
- 部署注册的可选空字段与不同内容包禁止覆盖同名定义检查通过。
- RTS 范围内不再引用 MoonSharp、LuaCsSetup 或 Lua 网络桥。

验证输出位于 `.migration-check/RTSRelease/TeraDeepOcean`；构建时将部署目录指向该验证目录。日常开发仍使用原 VS2022 启动方式。

编译保留原 AIControl 中的 3 处可空引用警告，以及 Linux/Mac 项目默认 AnyCPU 对 x64 引用的架构警告。本次未改动这些项目的处理器设置。

本机命令行 `dotnet` 当前会默认选择残留的 .NET 10 RC SDK，但对应 RC 运行时不存在，因此直接运行官方工具箱的 Build Release 可能先遇到 SDK 启动错误。本次使用 VS2022 MSBuild 与已安装的 9.0.319 SDK 完成验证，模组目标仍是 net8.0；未修改你的全局 SDK 配置。

上述测试不等于游戏内联机验收：尚未实际运行双客户端游戏，也未在 Linux/Mac 上启动游戏。

## 游戏内验收顺序

1. 重新生成 WindowsClient 和 WindowsServer，让正常部署流程更新 LocalMods；首次增加类型/消息头后重新启动游戏。
2. 单人前哨回合：F8 进入，选择船员，验证三种队形、右键移动/攻击、G 方向射击、释放 AI、退出后恢复角色。
3. 多人前哨回合：将 `tl_rts_command_device_test` 真正拿在手中，申请指挥权；另一个玩家重复申请应被拒绝。
4. 检查指挥官的单位选择与移动标记；非指挥官不能下达有效命令；放下终端后指挥权应撤销。
5. 在己方主潜艇合法房间内分别部署爬行者和护航特战队，另一客户端应能看见角色和装备；非法位置应被拒绝。
6. 使用测试收费预设验证余额不足、银行权限和成功扣费，结束回合、断线重连和切换服务器后再次申请指挥权。

如果原生游戏网络生成/界面行为出现差异，需要依据实际游戏日志继续定位；编译与序列化测试不会覆盖 AI 寻路和联机实体生成时序。

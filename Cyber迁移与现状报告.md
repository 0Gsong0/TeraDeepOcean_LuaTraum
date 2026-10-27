# Cyber 迁移与现状报告

## 结论

Cyber 已从旧框架迁入官方 C# 项目，安装、伤害转换、维修以及客户端安装提示均已接入插件入口。当前属于“可运行的基础义体机制”：已有核心芯片、两种民用义肢、四类损坏与四种维修物品；工业/军用系列、扩展模块安装和完整拆卸流程尚未在这套 C# 代码中实现。

本报告区分已经存在的执行逻辑与仅有 XML/数据定义的内容。编译、真实游戏程序集的接口检查及补丁安装检查已通过，尚未进行游戏内安装、受伤、维修和多人操作验证。

## 本次迁移范围

- 共享代码：`SharedProject/SharedSource/Cyber/` 中原有的五个文件。
- 客户端依赖：`ClientProject/ClientSource/Cyber/TLCybNotice.cs`。
- 框架适配：新增 `TLCybGameApi.cs`，兼容物品移除队列的单参数/双参数签名。
- 多人提示：新增 `TLCybNoticeBridge.cs`，通过现有 `TLNetworkHeader.CybNotice = 23` 发送类型化 C# 消息。
- 插件入口：初始化安装、伤害和维修补丁，并在初始化、回合开始或卸载时清理相应提示记录及计时器。
- 旋转组件：`SharedProject/SharedSource/Motion/TLLinkedRotatorComponent.cs` 与旧文件逐字节一致，未补写功能。它由项目已有的 `RegisterAllItemComponents()` 注册。

现有 Cyber XML、贴图、声音和文本已由你放入新项目。本次检查了相关四个 XML 文件引用的 18 个本地资源路径，均存在；清单已引用义体物品、损伤、声音物品、事件和附加 UI 样式，没有重复导入旧资源。

## 安装流程

实际入口为 `Item.ApplyTreatment(user, character, targetLimb)` 的后置补丁。

没有检测到 Neurotrauma 时：

1. 对目标肢体使用 `TLCyb_Tool`，写入肢体状态 `TLCyb_Tool_Init`，并将工具排入移除队列。
2. 在满足准备状态时使用 `TLCyb_LycorisChip`，向头部写入 `TLCyb_LycorisChip_Init`，消耗芯片。
3. 已有曼珠芯片时，对准备好的适用肢体使用民用机械臂或机械腿，写入对应安装状态、消耗义肢物品并清除该肢体的准备状态。

检测到 Neurotrauma 时：

- 检测依据是 `traumaticshock` Affliction 预制体是否存在，并非对模组加载器的正式查询。
- 使用本模组准备工具会收到“请使用标准手术”的提示，工具仍按旧逻辑消耗。
- 安装入口改为检查目标部位的 `retractedskin` 状态。
- 已有 `ntc_cyberlimb`、`ntc_cyberarm`、`ntc_cyberleg`，或头部的 `ntc_cyberbrain` 时，可以被查询接口识别；这不等于完整兼容所有 NT/NTC 版本和伤害机制。

现有数据表只有：

| 物品 | 安装状态 | 允许部位 | 结构/伺服/电路/系统损伤倍率 |
| --- | --- | --- | --- |
| `TLCyb_CivilianArm` | `TLCyb_CivilianArm_Init` | 左臂、右臂 | 1 / 1 / 1 / 0.85 |
| `TLCyb_CivilianLeg` | `TLCyb_CivilianLeg_Init` | 左腿、右腿 | 1 / 1 / 1 / 0.85 |

安装通过 Affliction 作为状态标记，没有真正替换角色肢体模型或物种。安装音效沿用生成隐藏音效物品的方式。相同角色、相同义肢物品种类的首次提示/音效每回合记录一次，因此左右臂使用同一种物品时通常只提示一次。

本次保留原有安装规则；只修正了找不到安装音效预制体时提前返回、导致已安装义肢却未消耗物品的收尾问题。现在会记录错误并继续完成消耗和清理。

## 损伤机制

`TLCybDamageSystem` 拦截 `CharacterHealth.ApplyDamage`，仅在单人或服务器处理活着的人类角色，且命中部位存在本模组义肢时转换伤害。

已有 14 条映射，覆盖流血、枪伤、钝伤、撕裂伤、烧伤、爆炸伤和内部损伤七类来源，转换为：

| 状态 | 含义 | 现有 XML 效果 |
| --- | --- | --- |
| `TLCyb_StructuralDamage` | 结构损伤 | 降低活力 |
| `TLCyb_ServoDamage` | 伺服系统故障 | 降低步行和游泳速度 |
| `TLCyb_CircuitDamage` | 电路损坏 | 降低步行和游泳速度 |
| `TLCyb_SystemDamage` | 神经系统同步失调 | 已定义状态；该状态自身目前没有进一步的效果配置 |

传给游戏健康系统的转换量为：

`来源 Affliction 强度 × 映射 Weight × 对应义肢损伤倍率 × TLConfig.CybDamageMultiplier`

之后仍经过游戏健康系统自身的抗性、活力等处理，并非直接给最终状态强度赋值。例如枪伤会分别产生结构、电路和伺服损伤。

另外，低频更新会扫描有曼珠芯片的人类角色，将义肢上残留的上述普通伤害转换并清除来源状态。更新间隔沿用配置，默认常规更新为 2 秒、低频更新为 5 秒，损伤倍率默认为 1.5。

需要了解的现有行为：

- `UpdateCharacter` 中目前只有判断和遍历，内部业务为空；不能将这个常规更新入口理解成已实现运行耗能、义肢故障或其他持续模拟。
- 即时伤害转换只检查义肢，低频残留伤害转换还检查曼珠芯片，二者前提并不完全相同。
- 一次 `AttackResult` 只要有任一 Affliction 被转换，前置补丁就会跳过整个原版 `ApplyDamage`。同一次攻击中没有映射的其他 Affliction 也可能随之被跳过。这是旧实现保留的限制，本次没有重写伤害规则。
- 残留伤害清理沿用施加负强度 `-1000` 的方式，并非无条件精确归零；这仍受游戏健康系统处理影响。

## 维修机制

维修同样挂在 `Item.ApplyTreatment` 后置补丁，使用对应物品治疗含有目标损伤状态的肢体。

| 物品 | 维修目标 | 随机基础维修量 | 是否扣物品耐久 |
| --- | --- | --- | --- |
| `steel` | 结构损伤 | 5–10 | 是 |
| `screwdriver` | 伺服故障 | 8–15 | 否 |
| `fpgacircuit` | 电路损坏 | 5–15 | 是 |
| `TLCyb_Neuroleptic` | 系统损伤 | 3–8 | 是 |

基础维修量加上机械技能的 `0.1 倍`；维修系统损伤时，再加医疗技能的 `0.15 倍`。使用耐久的物品按现有公式扣除 Condition，不保证每次整件销毁。

**目前读取的是被维修者 `target` 的技能，而非操作者 `user` 的技能。**本次按旧规则保留。维修还没有成功率、操作时间或严格技能门槛；物品 XML 的 `SkillRequirementHint` 本身不代表 C# 已实现技能限制。

## 多人执行与同步

- 安装状态、维修数值、损伤转换和物品消耗由单人或服务器执行。
- 义体状态使用角色的原生 Affliction 系统，并交给游戏现有健康同步流程；没有新增独立的“义体数据库”或 Lua 桥。
- 安装提示通过官方 `IGameNetwork` 发给当前控制目标角色的客户端；NT 工具提示发送给操作者。客户端只显示提示，不据此修改状态。
- 对没有玩家控制的 NPC 安装，不会向所有玩家广播安装弹窗。
- 客户端本地读取到的 Affliction 可能晚于服务器变化；需要进行真正的多人测试确认交互和提示时序。

## 现有公开 API

### 数据定义

| 类型或成员 | 用途 |
| --- | --- |
| `TLCybCategory` | `Civilian`、`Industrial`、`Military` 分类；后两类尚无内置义肢 |
| `TLCybData` | 定义物品 ID、安装状态 ID、分类、允许部位、四种损伤倍率和安装提示/声音信息 |
| `TLCybDataForm.CybDatas` | 当前义肢定义数组，可替换数组；不是带验证、事件和包归属的正式注册服务 |
| `FindByItemId(string)` | 根据安装物品 ID 查找定义 |
| `FindByInstallAffId(string)` | 根据安装状态 ID 查找定义 |
| `TLCybDamageConvertData` | 单条来源状态、目标损伤、权重定义 |
| `TLCybDamageSystem.DamageConvertDataForm` | 可替换的伤害转换数组 |
| `TLCybRepairData` | 维修物品、目标状态、随机范围及耐久消耗选项 |
| `TLCybRepairSystem.RepairDataForm` | 四项维修定义；字段为 readonly 数组，不提供追加注册方法 |

数据表中的字符串匹配沿用普通字符串比较。调用时应沿用定义中的 ID，不应依赖这些查询自动处理大小写或拼写差异。

### 状态与肢体查询：`TLCybState`

| 方法 | 作用 |
| --- | --- |
| `NormalizeLimbType(LimbType)` | 将前臂/手归一到手臂，将大腿/脚归一到腿 |
| `GetLimbByType(Character, LimbType)` | 查询归一部位对应的角色肢体，找不到返回 null |
| `GetInstallTLCybData(Character, LimbType)` | 查找该部位已安装的本模组义肢定义 |
| `HasTLCyb(Character, LimbType)` | 是否安装本模组义肢 |
| `HasNTCyb(Character, LimbType)` | 是否具有已列出的 NTC 义体标记 |
| `HasAnyCyb(Character, LimbType)` | 上述两类任一存在 |
| `HasNT()` | 通过 `traumaticshock` 预制体存在性判断 NT 环境 |
| `HasLycoris(Character)` | 头部是否具有曼珠核心标记 |
| `HasAffOnLimb(Character, Limb?, string, float min = 0.1f)` | 是否达到指定部位状态强度门槛 |
| `GetAffStrengthOnLimb(Character, Limb, string)` | 查询指定部位状态强度，未找到返回 0 |
| `AddAffOnLimb(Character, Limb, string, float)` | 在权威端通过游戏健康系统增减状态；预制体不存在时直接返回 |
| `InstallCybLimb(Character, Limb, TLCybData)` | 底层安装：检查重复义体和允许部位，然后写入安装标记 |

`InstallCybLimb` 是底层接口，不包含手术准备、曼珠前置、物品消耗、提示和声音，也没有成功/失败返回值。正常玩家使用物品的完整流程应由安装系统处理。调用这些方法不需要重新执行各系统 `Init`。

查询示例：

```csharp
bool installed = TLCybState.HasTLCyb(character, LimbType.LeftArm);
TLCybData? definition = TLCybState.GetInstallTLCybData(character, LimbType.LeftArm);
Limb? limb = TLCybState.GetLimbByType(character, LimbType.LeftArm);
if (limb != null)
{
    float structuralDamage = TLCybState.GetAffStrengthOnLimb(
        character, limb, "TLCyb_StructuralDamage");
}
```

### 客户端提示：`TLCybNotice`

- `ShowConversationNotice`：显示对话式提示。
- `ShowMessageNotice`：显示带标题、背景/图标和自动关闭时间的消息框。
- `ShowInstallConversation`：根据义肢定义显示本地受控角色的安装对话。
- `ShowInstallMessage` 的两个重载：使用义肢定义，或直接传入标题/正文/图标显示安装消息。

这些公开 UI 方法仅位于客户端程序集，直接调用不会自动向其他客户端发送。新增的通知桥为内部迁移设施，不是开放给其他模组的网络接口。

## 尚未完成或需要后续设计的部分

- 工业/军用义肢具体型号、分类差异与配套安装数据。
- 常规更新内的义肢运行逻辑。
- 扩展学习/技能/应激/代谢模块虽有物品或效果定义，但当前 C# 安装代码只处理基础芯片和数据表中的两种义肢，没有这些扩展模块的完整安装分支。
- 完整拆卸、更换、回收接口；旧 Lua 辅助函数没有被当成已经迁移完成的功能。本次范围为指定的 C# Cyber 基础实现。
- 完整 NT/NTC 联合伤害、手术及跨模组适配。
- 正式对外注册服务、安装结果事件和事务式成功/失败反馈。
- 混合 Affliction 攻击、维修技能归属等现有规则仍需后续确认设计。

## 验证及使用

- Windows Client / Server Debug 构建通过；Cyber 没有新增编译警告，现存 AIControl 三处可空警告保留。
- 对实际安装的 Barotrauma / DedicatedServer DLL 进行了方法 JIT 绑定检查，包括客户端 105 个、服务器 98 个 Cyber/旋转相关方法。
- 安装、伤害、维修补丁均成功挂到实际游戏的四个目标方法；其中 ApplyTreatment 同时具有安装与维修两个后置补丁。
- 肢体归一、内置义肢查询、安装提示消息序列化往返和网络头唯一性检查通过。
- 旋转组件新旧文件哈希一致，未新增信号处理、旋转执行器、同步或选点 UI。

验证输出位于 `.migration-check/CyberMigration/`，没有直接覆盖游戏安装目录。请通过项目正常构建部署，重启游戏/服务器后验证：手术准备、芯片、左右义肢安装、各类伤害与维修，以及多人安装提示和物品消耗。

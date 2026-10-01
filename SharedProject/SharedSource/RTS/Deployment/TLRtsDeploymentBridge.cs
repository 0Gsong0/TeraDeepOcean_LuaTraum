using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using static Barotrauma.ModUtils;

namespace TeraDeepOcean
{
    /// <summary>
    /// RTS 部署预制体的 Lua → C# 注册桥。
    /// C# 在后台线程中解析、验证并写入部署预制体注册表。
    /// 不向 Lua 返回异步处理结果
    /// </summary>
    public static class TLRtsDeploymentBridge
    {
        /// <summary>
        /// 最多允许两个注册任务同时处理。
        /// 这个数字不是创建两条固定线程，
        /// 而是限制最多两个任务同时进入注册流程。
        /// </summary>
        private const int MaxConcurrentRegistrations = 2;
        /// <summary>
        /// 控制后台注册任务的并发数量。
        /// 第一个参数：当前可用位置数量。
        /// 第二个参数：最多允许的位置数量。
        /// </summary>
        private static readonly SemaphoreSlim registrationSlots = new(MaxConcurrentRegistrations, MaxConcurrentRegistrations);
        /// <summary>
        /// 用于通知所有后台注册任务停止。
        ///
        /// 插件初始化时创建；
        /// 插件卸载时调用 Cancel()。
        /// </summary>
        private static CancellationTokenSource? cancellationSource;
        /// <summary>
        /// JSON 解析设置。
        /// 允许：
        /// - 属性名称大小写不敏感；
        /// - JSON 最后一项带逗号；
        /// - JSON 中包含注释。
        /// </summary>
        private static readonly JsonSerializerOptions jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        /// <summary>
        /// 初始化部署注册桥。
        /// </summary>
        public static void Init()
        {
            if (cancellationSource != null) return;
            cancellationSource = new CancellationTokenSource();
            LogOnMainThread("[TLRTS部署] 多线程部署预制体注册桥已初始化。", Color.LightGreen);
        }
        /// <summary>
        /// 由 Lua 调用，异步注册一个部署预制体。
        ///
        /// 返回 true 只表示任务已经成功提交，
        /// 不表示预制体最终一定注册成功。
        ///
        /// 最终结果会输出到游戏控制台。
        /// </summary>
        public static bool RegisterJsonAsync(string sourcePackageName, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                DebugConsole.NewMessage("[TLRTS部署] 注册失败：预制体 JSON 不能为空。", Color.Red);
                return false;
            }
            /*
             * ContentPackageManager 必须在主线程访问。
             *
             * Lua 调用此函数时位于游戏主线程，
             * 所以先在这里确认来源内容包，并取得正式名称。
             */
            if (!TLRtsDeploymentRegistry.TryGetCanonicalEnabledPackageName(sourcePackageName, out string canonicalPackageName, out string packageError))
            {
                DebugConsole.NewMessage($"[TLRTS部署] 注册失败：{packageError}", Color.Red);
                return false;
            }
            /*
             * 保存当前令牌。
             * 后台任务通过它得知插件是否已经要求停止。
             */
            CancellationToken token = cancellationSource.Token;
            _ = Task.Run(() =>
            {
                ProcessRegistrationAsync(canonicalPackageName, json, token);
            },token);
            return true;
        }
        /// <summary>
        /// 在后台线程中解析、验证并注册预制体。
        /// </summary>
        private static async Task ProcessRegistrationAsync(string canonicalPackageName, string json, CancellationToken token)
        {
            /*
             * 用于记录当前任务是否已经取得并发位置。
             * 只有取得位置后，finally 才能调用 Release()。
             */
            bool hasRegistrationSlot = false;
            try
            {
                /*
                 * 等待一个可用的注册位置。
                 *
                 * 如果当前已经有两个注册任务正在工作，
                 * 新任务会在这里异步等待。
                 *
                 * 等待期间不会一直占用工作线程。
                 */
                await registrationSlots.WaitAsync(token);
                hasRegistrationSlot = true;
                /*
                * 如果插件已经开始卸载，
                * 在继续解析之前主动停止。
                * 抛出异常
                */
                token.ThrowIfCancellationRequested();
                /*
                 * 在后台线程中将 JSON 转换成 C# 数据对象。
                 *
                 * 这里只创建普通数据对象，
                 */
                TLRtsDeploymentPrefabDefinition? definition = JsonSerializer.Deserialize<TLRtsDeploymentPrefabDefinition>(json,jsonOptions);
                if (definition == null)
                {
                    LogOnMainThread($"[TLRTS部署] 注册失败：{definition.Identifier}（来源：{canonicalPackageName}）:JSON 没有生成有效的单位定义。",Color.Red);
                    return;
                }
                token.ThrowIfCancellationRequested();
                bool success = TLRtsDeploymentRegistry.TryRegisterPrepared(canonicalPackageName, definition, out string error);
                if (!success)
                {
                    LogOnMainThread($"[TLRTS部署] 注册失败：{definition.Identifier}（来源：{canonicalPackageName}）:{error}", Color.Red);
                    return;
                }
                LogOnMainThread($"[TLRTS部署] 注册成功：{definition.Identifier} （来源：{canonicalPackageName}）",Color.LightGreen);
            }
            catch (OperationCanceledException)
            {
                /*
                 * 捕获后离开 catch，整个函数随后结束。
                 * 插件卸载时取消任务属于正常情况，
                 * 不需要作为错误输出。
                 */
            }
            catch (JsonException exception)
            {
                LogOnMainThread($"[TLRTS部署] 注册失败：（{canonicalPackageName}） JSON 格式错误：{exception}", Color.Red);
            }
            catch (Exception exception)
            {
                LogOnMainThread($"[TLRTS部署] 后台多线程注册发生异常：" + exception.Message, Color.Red);
            }
            finally
            {
                /*
                 * 只有成功取得注册位置后才能归还。
                 *
                 * 如果任务在等待期间就被取消，
                 * hasRegistrationSlot 仍然是 false，
                 * 此时不能调用 Release()。
                 */
                if (hasRegistrationSlot)
                {
                    registrationSlots.Release();
                }
            }
        }
        /// <summary>
        /// 将控制台输出安排到游戏主线程执行。
        ///
        /// 后台线程不应直接操作游戏控制台、GUI 或 Lua。
        /// CrossThread 会把这段工作交还给游戏主线程。
        /// </summary>
        private static void LogOnMainThread(string message,Color color)
        {
            CrossThread.RequestExecutionOnMainThread(() =>
            {
                DebugConsole.NewMessage(message, color);
            });
        }
        /// <summary>
        /// 在控制台输出当前所有已注册的 RTS 部署预制体。
        /// </summary>
        public static void DebugPrintRegistry()
        {
            IReadOnlyList<TLRtsDeploymentPrefabDefinition> prefabs = TLRtsDeploymentRegistry.GetAll();
            DebugConsole.NewMessage("========== [TLRTS部署] 注册表检查开始 ==========", Color.Cyan);
            DebugConsole.NewMessage($"[TLRTS部署] 当前已注册预制体数量：{prefabs.Count}", Color.White);
            if (prefabs.Count == 0)
            {
                DebugConsole.NewMessage("[TLRTS部署] 注册表为空。", Color.Yellow);
                DebugConsole.NewMessage("========== [TLRTS部署] 注册表检查结束 ==========",Color.Cyan);
                return;
            }
            foreach (TLRtsDeploymentPrefabDefinition prefab in prefabs)
            {
                string costsText = prefab.Costs.Count == 0 ? "无费用" : string.Join(", ", prefab.Costs.Select(cost => $"{cost.ResourceIdentifier} = {cost.Amount}"));
                DebugConsole.NewMessage($"[TLRTS部署] 预制体：{prefab.Identifier}", Color.LightGreen);
                DebugConsole.NewMessage($"  显示名称：{prefab.DisplayName}", Color.White);
                DebugConsole.NewMessage($"  描述：{prefab.Description}", Color.White);
                DebugConsole.NewMessage($"  来源内容包：{prefab.SourcePackageName}", Color.White);
                DebugConsole.NewMessage($"  标准化图标路径：{prefab.IconPath}", Color.White);
                DebugConsole.NewMessage($"  单位类型：{prefab.UnitType}", Color.White);
                DebugConsole.NewMessage($"  部署费用：{costsText}", Color.White);
                if (TLRtsDeploymentRegistry.TryResolveIconPath(prefab.Identifier, out ContentPath resolvedIconPath, out string iconError))
                {
                    /*
                     * Value 是经过 %ModDir% 替换后的路径。
                     * FullPath 是最终绝对路径。
                     */
                    DebugConsole.NewMessage($"  图标解析路径：{resolvedIconPath.Value}", Color.White);
                    DebugConsole.NewMessage($"  图标绝对路径：{resolvedIconPath.FullPath}",Color.White);
                    /*
                     * 当前这里只检查文件是否存在，
                     * 还没有真正创建 Sprite。
                     */
                    bool iconFileExists = System.IO.File.Exists(resolvedIconPath.FullPath);
                    DebugConsole.NewMessage($"  图标文件存在：{iconFileExists}", iconFileExists ? Color.LightGreen : Color.Red);
                }
                else
                {
                    DebugConsole.NewMessage($"  图标路径解析失败：{iconError}", Color.Red);
                }
                /*
                 * 输出不同单位类型对应的生成来源。
                 */
                if(prefab.UnitType == TLRtsDeploymentUnitType.HumanNpc)
                {
                    DebugConsole.NewMessage($"  NPC Set：{prefab.NpcSetIdentifier}", Color.White);
                    DebugConsole.NewMessage($"  NPC Identifier：{prefab.NpcIdentifier}", Color.White);
                }else if(prefab.UnitType == TLRtsDeploymentUnitType.Creature)
                {
                    DebugConsole.NewMessage($"  生物 SpeciesIdentifier：{prefab.SpeciesIdentifier}", Color.White);
                }
                DebugConsole.NewMessage("  ----------------------------------------", Color.DarkGray);
            }
            DebugConsole.NewMessage("========== [TLRTS部署] 注册表检查结束 ==========",Color.Cyan);
        }
        /// <summary>
        /// 停止部署注册桥。
        ///
        /// 应当在 Plugin.Dispose() 中调用。
        /// </summary>
        public static void Dispose()
        {
            if (cancellationSource == null) return;
            /*
             * 通知仍在等待或执行的后台注册任务停止。
             */
            cancellationSource.Cancel();
            /*
             * 释放取消控制器使用的资源。
             */
            cancellationSource.Dispose();
            cancellationSource = null;
        }
    }
}

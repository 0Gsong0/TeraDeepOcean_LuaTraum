using Barotrauma.Networking;
using System;
using System.Collections.Generic;
using System.Text;

namespace TeraDeepOcean
{
    /// <summary>
    /// 一次部署费用结算所需的上下文。
    /// 多人模式下 Client 是向服务端发送部署请求的指挥官；
    /// 单人模式下 Client 可以为 null。
    /// </summary>
    public class TLRtsDeploymentPaymentContext
    {
        /// <summary>
        /// 多人模式下请求部署的玩家。
        /// 服务端会根据该 Client 查询个人钱包和银行权限。
        /// 单人模式下为 null。
        /// </summary>
        public Client? PurchasingClient { get; }
        /// <summary>
        /// 当前 RTS 指挥官角色。
        /// </summary>
        public Character CommanderCharacter { get; }
        public bool IsAuthoritative => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;
        public TLRtsDeploymentPaymentContext(Client? purchasingClient, Character commanderCharacter)
        {
            PurchasingClient = purchasingClient;
            CommanderCharacter = commanderCharacter;
        }
    }
    /// <summary>
    /// 一种部署资源的费用处理器。
    /// 接口
    /// </summary>
    public interface ITLRtsDeploymentCostHandler
    {
        Identifier ResourceIdentifier { get; }
        /// <summary>
        /// 查询当前可以使用的资源数量。
        /// </summary>
        /// <param name="context"></param>
        /// <param name="amount"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        bool TryGetAvailableAmount(TLRtsDeploymentPaymentContext context, out int amount, out string error);
        /// <summary>
        /// 实际支付指定数量的资源。
        /// </summary>
        /// <param name="context"></param>
        /// <param name="amount"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        bool TryPay(TLRtsDeploymentPaymentContext context, int amount, out string error);
    }
    public sealed class TLRtsMarksCostHandler : ITLRtsDeploymentCostHandler
    {
        public Identifier ResourceIdentifier => TLRtsDeploymentResourceIds.Marks.ToIdentifier();

        public bool TryGetAvailableAmount(TLRtsDeploymentPaymentContext context, out int amount, out string error)
        {
            amount = 0;
            error = string.Empty;
            if (GameMain.GameSession?.Campaign is not { } campaign)
            {
                error = "当前游戏不是战役模式，无法读取马克余额。";
                return false;
            }
            /*
             * 单人模式：
             * PurchasingClient 为 null，
             * CampaignMode.GetBalance(null) 返回战役银行余额。
             *
             * 多人服务端：
             * 传入真正发送部署请求的 Client。
             * 游戏会根据其个人钱包和管理银行权限计算可用余额。
             *
             * 多人客户端：
             * 可以使用本地同步的余额进行 UI 预览，
             * 但不能执行真正扣款。
             */
            amount = campaign.GetBalance(context.PurchasingClient);
            return true;
        }

        public bool TryPay(TLRtsDeploymentPaymentContext context, int amount, out string error)
        {
            error = string.Empty;
            if (amount < 0)
            {
                error = "无法支付负数马克。";
                return false;
            }
            if (amount == 0) return true;
            if (!context.IsAuthoritative)
            {
                error = "多人客户端不能直接扣除部署费用。";
                return false;
            }
            if (GameMain.GameSession?.Campaign is not { } campaign)
            {
                error = "当前游戏不是战役模式，无法扣除马克。";
                return false;
            }
            if (!campaign.TryPurchase(context.PurchasingClient, amount))
            {
                error = $"马克不足，需要 {amount} mk。";
                return false;
            }
            return true;
        }
    }
    public static class TLRtsDeploymentCostSystem
    {
        /// <summary>
        /// 已注册的资源处理器
        /// </summary>
        private static readonly Dictionary<Identifier, ITLRtsDeploymentCostHandler> handlers = new();
        private static readonly object handlerLock = new();
        private static bool isInitialized;
        public static void Init()
        {
            if (isInitialized) return;
            isInitialized = true;
            lock (handlerLock)
            {
                handlers.Clear();
            }
            if (!TryRegisterHandler(new TLRtsMarksCostHandler(), false, out string error))
            {
                DebugConsole.NewMessage($"[TLRTS部署] 马克费用处理器注册失败：{error}", Color.Red);
                return;
            }
            DebugConsole.NewMessage($"[TLRTS部署] 马克费用处理器已注册。", Color.LightGreen);
        }
        /// <summary>
        /// 注册一个资源处理器。
        /// </summary>
        /// <param name="handler"></param>
        /// <param name="replaceExisting"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        public static bool TryRegisterHandler(ITLRtsDeploymentCostHandler handler, bool replaceExisting, out string error)
        {
            error = string.Empty;
            if (handler == null)
            {
                error = "处理器不能为 null。";
                return false;
            }
            Identifier identifier = handler.ResourceIdentifier;
            if (identifier.IsEmpty)
            {
                error = "处理器的 ResourceIdentifier 不能为空。";
                return false;
            }
            lock (handlerLock)
            {
                if (handlers.ContainsKey(identifier) && !replaceExisting)
                {
                    error = $"已存在同名的处理器：{identifier}。";
                    return false;
                }
                handlers[identifier] = handler;
            }
            return true;
        }
        /// <summary>
        /// 查询一种资源当前可用的数量。
        /// </summary>
        /// <param name="resourceIdentifier"></param>
        /// <param name="context"></param>
        /// <param name="amount"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        public static bool TryGetAvailableAmount(string resourceIdentifier, TLRtsDeploymentPaymentContext context, out int amount, out string error)
        {
            amount = 0;
            error = string.Empty;
            Identifier identifier = resourceIdentifier?.Trim().ToIdentifier() ?? String.Empty;
            if (identifier.IsEmpty)
            {
                error = "资源标识符不能为空。";
                return false;
            }
            if (!TryGetHandler(identifier, out ITLRtsDeploymentCostHandler? handler) || handler == null)
            {
                error = $"未找到资源处理器：{identifier}。";
                return false;
            }
            return handler.TryGetAvailableAmount(context, out amount, out error);
        }
        /// <summary>
        /// 检查是否能够支付一个部署预制体的全部费用。
        /// </summary>
        /// <param name="prefab"></param>
        /// <param name="context"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        public static bool CanAfford(TLRtsDeploymentPrefabDefinition prefab, TLRtsDeploymentPaymentContext context, out string error)
        {
            error = string.Empty;
            if (prefab == null)
            {
                error = "部署预制体不能为 null。";
                return false;
            }
            if (prefab.Costs == null)
            {
                error = $"部署预制体“{prefab.Identifier}”的费用列表为空。";
                return false;
            }
            foreach (var cost in prefab.Costs)
            {
                if (cost == null)
                {
                    error = $"部署预制体“{prefab.Identifier}”包含空费用项目";
                    return false;
                }
                if (cost.Amount < 0)
                {
                    error = $"资源“{cost.ResourceIdentifier}”的费用不能是负数。";
                    return false;
                }
                if (cost.Amount == 0) continue;
                Identifier identifier = cost.ResourceIdentifier?.Trim().ToIdentifier() ?? Identifier.Empty;
                if (identifier.IsEmpty)
                {
                    error = $"资源“{cost.ResourceIdentifier}”的标识符不能为空。";
                    return false;
                }
                if(!TryGetHandler(identifier, out ITLRtsDeploymentCostHandler? handler) || handler == null)
                {
                    error = $"未没有注册资源“{identifier}”的费用处理器。";
                    return false;
                }
                if(!handler.TryGetAvailableAmount(context, out int availableAmount, out error))
                {
                    error = $"资源“{identifier}”的获取可用数量失败：{error}";
                    return false;
                }
                if(availableAmount < cost.Amount)
                {
                    error = $"资源“{identifier}”可用数量不足:\n需要 {cost.Amount}，当前只有 {availableAmount}。";
                    return false;
                }
            }
            return true;
        }
        public static bool TryPay(TLRtsDeploymentPrefabDefinition prefab, TLRtsDeploymentPaymentContext context, out string error)
        { 
            error = string.Empty;
            if (!context.IsAuthoritative)
            {
                error = "多人客户端不能直接结算部署费用。";
                return false;
            }
            if(!CanAfford(prefab, context, out error))
            {
                return false;
            }
            foreach (TLRtsDeploymentCostDefinition cost in prefab.Costs)
            {
                if (cost.Amount == 0) continue;
                Identifier identifier = cost.ResourceIdentifier?.Trim().ToIdentifier() ?? Identifier.Empty;
                if (!TryGetHandler(identifier, out ITLRtsDeploymentCostHandler? handler) || handler == null)
                {
                    error = $"没有注册资源“{identifier}”的费用处理器。";
                    return false;
                }
                if (!handler.TryPay(context, cost.Amount, out string spendError))
                {
                    error = spendError;
                    return false;
                }
            }
            return true;
        }
        /// <summary>
        /// 查找一个资源处理器。
        /// </summary>
        /// <param name="identifier"></param>
        /// <param name="handler"></param>
        /// <returns></returns>
        private static bool TryGetHandler(Identifier identifier, out ITLRtsDeploymentCostHandler? handler)
        {
            handler = null;
            lock (handlerLock)
            {
                return handlers.TryGetValue(identifier, out handler);
            }
        }
        public static void Dispose()
        {
            lock (handlerLock)
            {
                handlers.Clear();
            }
            isInitialized = false;
        }
    }
}

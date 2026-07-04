using ItemMarket.Api.Hubs;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Trades;
using Microsoft.AspNetCore.SignalR;

namespace ItemMarket.Api.Infrastructure;

/// <summary>SignalR 그룹명 규칙. 프론트 계약(docs/realtime-contract.md)과 일치해야 한다.</summary>
internal static class MarketGroups
{
    public static string Template(int templateId) => $"tmpl:{templateId}";
    public static string User(Guid playerId) => $"user:{playerId}";
}

/// <summary>
/// 실시간 이벤트 발행기. 엔드포인트 계층에서 주문 등록/취소/체결 후 호출한다
/// (grain은 SignalR에 결합하지 않는다). 발행은 항상 best-effort — 실패해도
/// HTTP 요청을 깨뜨리지 않도록 내부에서 삼켜 로깅만 한다.
/// </summary>
public interface IMarketNotifier
{
    /// <summary>
    /// 주문 등록/취소 이후 실시간 이벤트 일괄 발행:
    /// (1) 갱신된 호가창 스냅샷 → <c>OrderBookUpdated</c>(tmpl 그룹),
    /// (2) 각 체결 → <c>TradeExecuted</c>(tmpl 그룹) + 매수/매도자 <c>WalletChanged</c>,
    /// (3) 행위자(등록/취소자) <c>WalletChanged</c>(에스크로 변동).
    /// </summary>
    Task PublishOrderActivityAsync(OrderBookSnapshotDto snapshot, Guid actingPlayerId, IReadOnlyList<TradeDto> fills);
}

/// <summary><see cref="IHubContext{THub}"/>(MarketHub)를 감싼 발행기 구현.</summary>
public sealed class MarketNotifier(IHubContext<MarketHub> hub, ILogger<MarketNotifier> logger) : IMarketNotifier
{
    public async Task PublishOrderActivityAsync(OrderBookSnapshotDto snapshot, Guid actingPlayerId, IReadOnlyList<TradeDto> fills)
    {
        try
        {
            var tmplGroup = MarketGroups.Template(snapshot.ItemTemplateId);
            await hub.Clients.Group(tmplGroup).SendAsync("OrderBookUpdated", snapshot);

            foreach (var fill in fills)
            {
                await hub.Clients.Group(MarketGroups.Template(fill.ItemTemplateId)).SendAsync("TradeExecuted", fill);
                await hub.Clients.Group(MarketGroups.User(fill.BuyerId)).SendAsync("WalletChanged");
                await hub.Clients.Group(MarketGroups.User(fill.SellerId)).SendAsync("WalletChanged");
            }

            // 등록/취소자는 에스크로가 변동되므로 지갑/인벤 재조회를 알린다.
            await hub.Clients.Group(MarketGroups.User(actingPlayerId)).SendAsync("WalletChanged");
        }
        catch (Exception ex)
        {
            // 실시간 발행 실패가 HTTP 요청을 실패시키면 안 된다. 로깅만 하고 삼킨다.
            logger.LogWarning(ex, "실시간 이벤트 발행 실패 (template {TemplateId})", snapshot.ItemTemplateId);
        }
    }
}

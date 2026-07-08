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

            // TradeExecuted는 체결마다 서로 다른 데이터라 per-fill로 발행한다.
            // 반면 WalletChanged는 값 없는 "재조회 힌트"라 같은 유저에게 여러 번 보내면
            // 클라가 GET /api/wallet 를 그만큼 중복 재조회한다(멀티필 시 신호 폭발).
            // 그래서 이번 활동으로 지갑/에스크로가 변동된 유저를 집합으로 모아
            // per-order로 딱 한 번씩만 보낸다(actingPlayer 포함, 중복 제거).
            var affected = new HashSet<Guid> { actingPlayerId };
            foreach (var fill in fills)
            {
                await hub.Clients.Group(MarketGroups.Template(fill.ItemTemplateId)).SendAsync("TradeExecuted", fill);
                affected.Add(fill.BuyerId);
                affected.Add(fill.SellerId);
            }

            foreach (var playerId in affected)
                await hub.Clients.Group(MarketGroups.User(playerId)).SendAsync("WalletChanged");
        }
        catch (Exception ex)
        {
            // 실시간 발행 실패가 HTTP 요청을 실패시키면 안 된다. 로깅만 하고 삼킨다.
            logger.LogWarning(ex, "실시간 이벤트 발행 실패 (template {TemplateId})", snapshot.ItemTemplateId);
        }
    }
}

using System.Security.Claims;
using ItemMarket.Api.Infrastructure;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Trades;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>주문 — 등록(매칭 엔진 진입점) / 내 주문 조회 / 취소(에스크로 환불).</summary>
public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapPost("/orders", (PlaceOrderRequest req, ClaimsPrincipal u, IGrainFactory gf, IMarketNotifier notifier) =>
            Exec(async () =>
            {
                var pid = CurrentPlayer(u);
                var grain = gf.GetGrain<IOrderBookGrain>(req.ItemTemplateId);
                var result = await grain.PlaceOrder(pid, req);
                // 발행(best-effort): 갱신 호가창 + 체결별 이벤트 + 행위자 지갑 변동.
                await notifier.PublishOrderActivityAsync(await grain.GetSnapshot(), pid, result.Fills);
                return result;
            }));

        api.MapGet("/orders", (ClaimsPrincipal u, MarketRepository repo) =>
            Exec(() => repo.GetOrdersByPlayerAsync(CurrentPlayer(u))));

        api.MapGet("/orders/{id:guid}", (Guid id, ClaimsPrincipal u, MarketRepository repo) => Exec(async () =>
        {
            var pid = CurrentPlayer(u);
            var order = await repo.GetOrderAsync(id)
                ?? throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다.");
            if (order.PlayerId != pid)
                throw new DomainException(ErrorCode.OrderNotOwned, "본인 주문이 아닙니다.");
            return order.ToDto();
        }));

        api.MapDelete("/orders/{id:guid}", (Guid id, ClaimsPrincipal u, IGrainFactory gf, MarketRepository repo, IMarketNotifier notifier) => Exec(async () =>
        {
            var pid = CurrentPlayer(u);
            var order = await repo.GetOrderAsync(id)
                ?? throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다.");
            var grain = gf.GetGrain<IOrderBookGrain>(order.TemplateId);
            var result = await grain.CancelOrder(pid, id, isAdmin: false);
            // 취소는 체결이 없다(에스크로 환불만). 갱신 호가창 + 취소자 지갑 변동을 발행.
            await notifier.PublishOrderActivityAsync(await grain.GetSnapshot(), pid, Array.Empty<TradeDto>());
            return result;
        }));

        return app;
    }
}

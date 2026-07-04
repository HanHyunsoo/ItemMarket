using System.Security.Claims;
using ItemMarket.Contracts.Orders;
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

        api.MapPost("/orders", (PlaceOrderRequest req, ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IOrderBookGrain>(req.ItemTemplateId).PlaceOrder(CurrentPlayer(u), req)));

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

        api.MapDelete("/orders/{id:guid}", (Guid id, ClaimsPrincipal u, IGrainFactory gf, MarketRepository repo) => Exec(async () =>
        {
            var pid = CurrentPlayer(u);
            var order = await repo.GetOrderAsync(id)
                ?? throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다.");
            return await gf.GetGrain<IOrderBookGrain>(order.TemplateId).CancelOrder(pid, id, isAdmin: false);
        }));

        return app;
    }
}

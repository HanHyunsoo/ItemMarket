using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Orders;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>운영(어드민) — admin 롤 필요(없으면 403). 지급/지갑조정/강제취소/전체조회.</summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization("admin");

        admin.MapGet("/players/{id:guid}/wallet", (Guid id, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IWalletGrain>(id).Get()));

        admin.MapPost("/wallet/adjust", (AdminAdjustWalletRequest req, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IWalletGrain>(req.PlayerId).AdminAdjust(req.Delta, req.Reason)));

        admin.MapPost("/grant/stack", (AdminGrantStackRequest req, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IPlayerInventoryGrain>(req.PlayerId).AdminGrantStack(req.TemplateId, req.Quantity)));

        admin.MapPost("/grant/instance", (AdminGrantInstanceRequest req, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IPlayerInventoryGrain>(req.PlayerId).AdminGrantInstance(req.TemplateId, req.Durability, req.Attachments)));

        admin.MapPost("/orders/force-cancel", (AdminForceCancelOrderRequest req, IGrainFactory gf, MarketRepository repo) => Exec(async () =>
        {
            var order = await repo.GetOrderAsync(req.OrderId)
                ?? throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다.");
            return await gf.GetGrain<IOrderBookGrain>(order.TemplateId).CancelOrder(order.PlayerId, req.OrderId, isAdmin: true);
        }));

        admin.MapGet("/orders", (MarketRepository repo, int? templateId, string? status, int page = 1, int size = 20) =>
            Exec(() =>
            {
                // Enum.TryParse는 "7" 같은 숫자 문자열도 (정의 안 된 값으로) 통과시키므로 IsDefined로 걸러낸다.
                OrderStatus? parsed = Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : null;
                return repo.GetOrdersAdminAsync(templateId, parsed, Math.Max(1, page), Math.Clamp(size, 1, 200));
            }));

        admin.MapGet("/trades", (MarketRepository repo, int page = 1, int size = 20) =>
            Exec(() => repo.GetTradesAllAsync(Math.Max(1, page), Math.Clamp(size, 1, 200))));

        return app;
    }
}

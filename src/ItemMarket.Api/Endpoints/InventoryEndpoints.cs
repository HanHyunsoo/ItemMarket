using System.Security.Claims;
using ItemMarket.Grains.Abstractions;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>인벤토리 — 스택형 수량 + 유니크 인스턴스(내구도·부착물) 조회.</summary>
public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Inventory");

        api.MapGet("/inventory", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IPlayerInventoryGrain>(CurrentPlayer(u)).Get()));

        // 아이템 원장: 레이드(RAID_*)/지급(ADMIN_GRANT) 이동 로그를 최신순 페이지네이션으로.
        api.MapGet("/inventory/ledger", (ClaimsPrincipal u, IGrainFactory gf, int page = 1, int size = 20) =>
            Exec(() => gf.GetGrain<IPlayerInventoryGrain>(CurrentPlayer(u)).GetLedger(page, size)));

        return app;
    }
}

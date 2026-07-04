using System.Security.Claims;
using ItemMarket.Grains.Abstractions;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>인벤토리 — 스택형 수량 + 유니크 인스턴스(내구도·부착물) 조회.</summary>
public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api").RequireAuthorization().WithTags("Inventory")
            .MapGet("/inventory", (ClaimsPrincipal u, IGrainFactory gf) =>
                Exec(() => gf.GetGrain<IPlayerInventoryGrain>(CurrentPlayer(u)).Get()));

        return app;
    }
}

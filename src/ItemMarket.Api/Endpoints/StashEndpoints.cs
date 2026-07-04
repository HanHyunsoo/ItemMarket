using System.Security.Claims;
using ItemMarket.Contracts.Stash;
using ItemMarket.Grains.Abstractions;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>스태시 — 고정 그리드(10×12) 위 아이템 배치 조회 / 서버 권위 이동.</summary>
public static class StashEndpoints
{
    public static IEndpointRouteBuilder MapStashEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/stash", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IStashGrain>(CurrentPlayer(u)).GetStash()));

        api.MapPost("/stash/move", (MoveStashItemRequest req, ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IStashGrain>(CurrentPlayer(u)).MoveItem(req)));

        return app;
    }
}

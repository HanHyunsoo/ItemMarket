using System.Security.Claims;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Stash;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>
/// 스태시 — 컨테이너(STASH 10×12 / LOADOUT 6×8) 그리드 위 아이템 배치 조회 / 서버 권위 이동.
/// </summary>
public static class StashEndpoints
{
    public static IEndpointRouteBuilder MapStashEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Stash");

        // 하위호환: 컨테이너 미지정 시 STASH.
        api.MapGet("/stash", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IStashGrain>(CurrentPlayer(u)).GetStash(GridContainer.Stash)));

        // 컨테이너별 조회: /api/stash/stash | /api/stash/loadout (대소문자 무시).
        api.MapGet("/stash/{container}", (string container, ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IStashGrain>(CurrentPlayer(u)).GetStash(ParseContainer(container))));

        // 같은 컨테이너 재배치 + 컨테이너 간 이동(반입/반출)을 모두 처리(From/ToContainer로 구분).
        api.MapPost("/stash/move", (MoveStashItemRequest req, ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IStashGrain>(CurrentPlayer(u)).MoveItem(req)));

        return app;
    }

    private static GridContainer ParseContainer(string raw)
        => Enum.TryParse<GridContainer>(raw, ignoreCase: true, out var c)
            ? c
            : throw new DomainException(ErrorCode.ValidationError, $"알 수 없는 컨테이너: {raw} (stash | loadout).");
}

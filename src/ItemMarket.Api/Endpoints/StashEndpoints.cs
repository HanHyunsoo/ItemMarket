using System.Security.Claims;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Stash;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>
/// 스태시 — 컨테이너(STASH 12×가변(player.stash_rows) / POCKETS 4×1) 그리드 위 아이템 배치 조회 / 서버 권위 이동.
/// </summary>
public static class StashEndpoints
{
    public static IEndpointRouteBuilder MapStashEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Stash");

        // 하위호환: 컨테이너 미지정 시 STASH.
        api.MapGet("/stash", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IStashGrain>(CurrentPlayer(u)).GetStash(GridContainer.Stash)));

        // 컨테이너별 조회: /api/stash/stash | /api/stash/pockets (대소문자 무시).
        api.MapGet("/stash/{container}", (string container, ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IStashGrain>(CurrentPlayer(u)).GetStash(ParseContainer(container))));

        // 같은 컨테이너 재배치 + 컨테이너 간 이동(반입/반출)을 모두 처리(From/ToContainer로 구분).
        api.MapPost("/stash/move", (MoveStashItemRequest req, ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IStashGrain>(CurrentPlayer(u)).MoveItem(req)));

        // 캡으로 스태시 행 확장(+6행). 점증 가격을 잔액에서 차감(캡 싱크) — 단일 트랜잭션.
        api.MapPost("/stash/upgrade", (ClaimsPrincipal u, MarketRepository repo) =>
            Exec(() => repo.UpgradeStashRowsAsync(CurrentPlayer(u))));

        return app;
    }

    // 이 라우트는 STASH/POCKETS만 조회한다. 중첩 컨테이너(CONTAINER)는 특정 인스턴스 id가
    // 필수라 여기로 조회할 수 없다 — 파싱은 되지만 거부한다(장비 조회 GET /api/equipment의
    // containers[]로 노출됨). 그대로 두면 GetStash(Container)가 InstanceId=null로 500(NRE)이 났다.
    private static GridContainer ParseContainer(string raw)
        => Enum.TryParse<GridContainer>(raw, ignoreCase: true, out var c) && c != GridContainer.Container
            ? c
            : throw new DomainException(ErrorCode.ValidationError,
                $"알 수 없는 컨테이너: {raw} (stash | pockets). 중첩 컨테이너는 장비(GET /api/equipment)로 조회합니다.");
}

using System.Security.Claims;
using ItemMarket.Contracts.Raid;
using ItemMarket.Grains.Abstractions;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>
/// 익스트랙션 레이드 — 서비스 계층 세션 상태기계. 실제 통합에서는 게임 서버가 호출한다
/// (게임플레이 틱/전투는 범위 밖). 루프: 로드아웃을 채운다 → StartRaid → Extract | Die.
/// </summary>
public static class RaidEndpoints
{
    public static IEndpointRouteBuilder MapRaidEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/raid").RequireAuthorization().WithTags("Raid");

        // 현재 세션 스냅샷(ACTIVE 우선, 없으면 최근 세션). 이력 없으면 Data=null.
        api.MapGet("", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IRaidSessionGrain>(CurrentPlayer(u)).Get()));

        // 레이드 시작: 로드아웃을 위험(at-risk)으로 잠근다. ACTIVE 세션이 이미 있으면 RaidActive(409).
        api.MapPost("/start", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IRaidSessionGrain>(CurrentPlayer(u)).StartRaid()));

        // 전리품 획득(MVP 시뮬레이션): ACTIVE 세션에 LOOTED 위험 아이템 추가.
        api.MapPost("/loot", (AddLootRequest req, ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IRaidSessionGrain>(CurrentPlayer(u)).AddLoot(req)));

        // 생존 탈출: 반입+획득 전량을 소유로 복귀(→ 다음 GET /api/stash에서 STASH 자동 배치).
        api.MapPost("/extract", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IRaidSessionGrain>(CurrentPlayer(u)).Extract()));

        // 사망: 위험 아이템 전량 소실. 스태시(안전)는 무관.
        api.MapPost("/die", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IRaidSessionGrain>(CurrentPlayer(u)).Die()));

        return app;
    }
}

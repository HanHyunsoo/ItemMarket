using System.Security.Claims;
using ItemMarket.Contracts.Raid;
using ItemMarket.Grains.Abstractions;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>
/// 익스트랙션 레이드 — 서비스 계층 세션 상태기계. 실제 통합에서는 게임 서버가 호출한다
/// (게임플레이 틱/전투는 범위 밖). 루프: 장비를 착용하고 주머니를 채운다 → StartRaid → Extract | Die.
/// </summary>
public static class RaidEndpoints
{
    public static IEndpointRouteBuilder MapRaidEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/raid").RequireAuthorization().WithTags("Raid");

        // 현재 진행 중(ACTIVE) 세션 스냅샷만 반환한다. ACTIVE가 없으면 Data=null
        // (해결된 세션 이력은 GET /api/raid/history).
        api.MapGet("", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IRaidSessionGrain>(CurrentPlayer(u)).Get()));

        // 레이드 이력: 해결된(EXTRACTED/DIED) 과거 세션(아이템 스냅샷 포함), 최신순 페이지네이션.
        api.MapGet("/history", (ClaimsPrincipal u, IGrainFactory gf, int page = 1, int size = 20) =>
            Exec(() => gf.GetGrain<IRaidSessionGrain>(CurrentPlayer(u)).GetHistory(page, size)));

        // 레이드 시작: 스태시 밖 전부(장비+주머니+중첩 컨테이너)를 위험(at-risk)으로 잠근다.
        // 존(zone)이 드롭 등급·사망확률 상승률을 결정한다(요청 바디 optional, 기본 Med).
        // ACTIVE 세션이 이미 있으면 RaidActive(409), 반입할 것이 전혀 없으면 RaidNothingToDeploy(400).
        api.MapPost("/start", (ClaimsPrincipal u, IGrainFactory gf, StartRaidRequest? req) =>
            Exec(() => gf.GetGrain<IRaidSessionGrain>(CurrentPlayer(u)).StartRaid((req ?? new StartRaidRequest()).Zone)));

        // 루팅(scavenge): 서버가 세션 존의 rarity 가중치로 드롭을 결정해 LOOTED로 추가하고 사망확률을 올린다.
        api.MapPost("/loot", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IRaidSessionGrain>(CurrentPlayer(u)).Scavenge()));

        // 생존 탈출: 반입+획득 전량을 소유로 복귀(→ 다음 GET /api/stash에서 STASH 자동 배치).
        api.MapPost("/extract", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IRaidSessionGrain>(CurrentPlayer(u)).Extract()));

        // 사망: 위험 아이템 전량 소실. 스태시(안전)는 무관.
        api.MapPost("/die", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IRaidSessionGrain>(CurrentPlayer(u)).Die()));

        return app;
    }
}

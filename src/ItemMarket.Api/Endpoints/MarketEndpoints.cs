using System.Security.Claims;
using ItemMarket.Contracts.Trades;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>마켓 조회 — 카탈로그 / 호가창 스냅샷 / 종목별 체결 내역.</summary>
public static class MarketEndpoints
{
    public static IEndpointRouteBuilder MapMarketEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Market");

        api.MapGet("/catalog", (MarketRepository repo) => Exec(async () => await repo.GetCatalogAsync()));

        // 전 종목 시세 요약(마켓 카드용): 최우선 호가·최근 체결·활성 주문 수를 한 번에.
        api.MapGet("/market/tickers", (MarketRepository repo) => Exec(() => repo.GetTickersAsync()));

        // 리더보드: 최다 캡 + 최다 생환(탈출) 상위 순위.
        api.MapGet("/leaderboard", (MarketRepository repo) => Exec(() => repo.GetLeaderboardAsync()))
            .WithTags("Leaderboard");

        // NPC 벤더 매입: 보유 아이템을 벤더가(base_value 스프레드)로 즉시 판매(캡 faucet).
        api.MapPost("/market/vendor/sell", (VendorSellRequest req, ClaimsPrincipal u, MarketRepository repo) =>
            Exec(() => repo.VendorSellAsync(CurrentPlayer(u), req)));

        api.MapGet("/market/{templateId:int}/book", (int templateId, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IOrderBookGrain>(templateId).GetSnapshot()));

        api.MapGet("/market/{templateId:int}/trades", (int templateId, MarketRepository repo, int page = 1, int size = 20) =>
            Exec(() => repo.GetTradesByTemplateAsync(templateId, Math.Max(1, page), Math.Clamp(size, 1, 200))));

        return app;
    }
}

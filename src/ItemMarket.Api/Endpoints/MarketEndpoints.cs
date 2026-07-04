using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>마켓 조회 — 카탈로그 / 호가창 스냅샷 / 종목별 체결 내역.</summary>
public static class MarketEndpoints
{
    public static IEndpointRouteBuilder MapMarketEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/catalog", (MarketRepository repo) => Exec(async () => await repo.GetCatalogAsync()));

        api.MapGet("/market/{templateId:int}/book", (int templateId, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IOrderBookGrain>(templateId).GetSnapshot()));

        api.MapGet("/market/{templateId:int}/trades", (int templateId, MarketRepository repo, int page = 1, int size = 20) =>
            Exec(() => repo.GetTradesByTemplateAsync(templateId, Math.Max(1, page), Math.Clamp(size, 1, 200))));

        return app;
    }
}

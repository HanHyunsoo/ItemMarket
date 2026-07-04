using System.Security.Claims;
using ItemMarket.Grains.Abstractions;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>지갑 — 잔액 / 원장(append-only) 조회.</summary>
public static class WalletEndpoints
{
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/wallet", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IWalletGrain>(CurrentPlayer(u)).Get()));

        api.MapGet("/wallet/ledger", (ClaimsPrincipal u, IGrainFactory gf, int page = 1, int size = 20) =>
            Exec(() => gf.GetGrain<IWalletGrain>(CurrentPlayer(u)).GetLedger(page, size)));

        return app;
    }
}

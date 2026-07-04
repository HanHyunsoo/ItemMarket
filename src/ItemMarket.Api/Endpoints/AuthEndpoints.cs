using ItemMarket.Api.Infrastructure;
using ItemMarket.Contracts.Auth;
using ItemMarket.Grains.Data;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>인증 — POST /api/auth/login (개발 스코프: 비밀번호 없이 시드 플레이어 로그인).</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login",
            (LoginRequest req, MarketRepository repo, JwtTokenIssuer issuer) => Exec(async () =>
            {
                var player = await repo.GetPlayerAsync(req.PlayerId)
                    ?? throw new DomainException(ErrorCode.PlayerNotFound, "플레이어를 찾을 수 없습니다.");
                return issuer.Issue(player.Id, player.DisplayName);
            })).AllowAnonymous();

        return app;
    }
}

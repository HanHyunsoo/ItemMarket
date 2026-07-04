using ItemMarket.Api.Infrastructure;
using ItemMarket.Contracts.Auth;
using ItemMarket.Grains.Data;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>
/// 인증 — 로그인 / 리프레시(로테이션) / 로그아웃.
/// 개발 스코프: 비밀번호 없이 시드 플레이어 ID로 로그인한다. 로그인 시 짧은 액세스 토큰과
/// 함께 긴 리프레시 토큰(원문은 클라이언트만 보관, 서버는 해시 저장)을 발급한다.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        // 로그인: 액세스 + 리프레시 쌍 발급.
        auth.MapPost("/login",
            (LoginRequest req, MarketRepository repo, JwtTokenIssuer issuer) => Exec(async () =>
            {
                var player = await repo.GetPlayerAsync(req.PlayerId)
                    ?? throw new DomainException(ErrorCode.PlayerNotFound, "플레이어를 찾을 수 없습니다.");
                return await IssuePairAsync(repo, issuer, player.Id, player.DisplayName);
            }))
            .AllowAnonymous()
            .WithSummary("개발 로그인(비밀번호 없음) — 액세스+리프레시 토큰 발급");

        // 리프레시: 제시된 토큰을 검증 → 로테이션(옛것 폐기, 새 쌍 발급).
        auth.MapPost("/refresh",
            (RefreshRequest req, MarketRepository repo, JwtTokenIssuer issuer) => Exec(async () =>
            {
                var row = await repo.GetRefreshTokenAsync(req.RefreshToken)
                    ?? throw new DomainException(ErrorCode.Unauthorized, "리프레시 토큰이 유효하지 않습니다.");

                // 재사용 탐지: 이미 폐기된 토큰이 다시 제시되면 탈취 정황 → 체인 전체 폐기.
                if (row.Revoked)
                {
                    await repo.RevokeAllRefreshTokensAsync(row.PlayerId);
                    throw new DomainException(ErrorCode.Unauthorized, "리프레시 토큰이 이미 폐기되었습니다.");
                }
                if (row.ExpiresAt <= DateTime.UtcNow)
                    throw new DomainException(ErrorCode.Unauthorized, "리프레시 토큰이 만료되었습니다.");

                // 로테이션: 원자적으로 옛 토큰을 폐기(동시 회전/재사용 레이스 차단).
                if (!await repo.TryRevokeRefreshTokenAsync(row.Id))
                    throw new DomainException(ErrorCode.Unauthorized, "리프레시 토큰을 회전할 수 없습니다.");

                var player = await repo.GetPlayerAsync(row.PlayerId)
                    ?? throw new DomainException(ErrorCode.PlayerNotFound, "플레이어를 찾을 수 없습니다.");
                return await IssuePairAsync(repo, issuer, player.Id, player.DisplayName);
            }))
            .AllowAnonymous()
            .WithSummary("리프레시 토큰으로 액세스 토큰 갱신(로테이션)");

        // 로그아웃: 제시된 리프레시 토큰을 폐기(멱등).
        auth.MapPost("/logout",
            (RefreshRequest req, MarketRepository repo) => Exec(async () =>
            {
                await repo.RevokeRefreshTokenAsync(req.RefreshToken);
                return true;
            }))
            .AllowAnonymous()
            .WithSummary("리프레시 토큰 폐기(로그아웃)");

        return app;
    }

    /// <summary>새 리프레시 토큰을 만들어 저장하고, 액세스+리프레시 봉투를 발급한다.</summary>
    private static async Task<TokenResponse> IssuePairAsync(
        MarketRepository repo, JwtTokenIssuer issuer, Guid playerId, string displayName)
    {
        var raw = RefreshTokens.NewRawToken();
        var expires = DateTime.UtcNow.AddDays(issuer.RefreshTokenDays);
        await repo.StoreRefreshTokenAsync(Guid.NewGuid(), playerId, raw, expires);
        return issuer.Issue(playerId, displayName, raw);
    }
}

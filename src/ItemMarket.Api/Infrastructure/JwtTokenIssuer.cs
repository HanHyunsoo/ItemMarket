using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ItemMarket.Contracts.Auth;
using Microsoft.IdentityModel.Tokens;

namespace ItemMarket.Api.Infrastructure;

/// <summary>
/// JWT(HS256) 발급기. sub=playerId, name=표시명 클레임을 담고,
/// AdminPlayerId와 일치하는 플레이어에게만 admin 롤을 부여한다.
/// 액세스 토큰은 짧게 발급되며(<see cref="AccessTokenMinutes"/>), 리프레시 토큰 원문은
/// 호출자(엔드포인트)가 만들어 DB에 해시로 저장한 뒤 여기에 전달한다.
/// </summary>
public sealed class JwtTokenIssuer(
    SymmetricSecurityKey signingKey,
    string issuer,
    string audience,
    string adminPlayerId,
    int accessTokenMinutes,
    int refreshTokenDays)
{
    /// <summary>리프레시 토큰 수명(일). 엔드포인트가 만료 시각 계산에 사용.</summary>
    public int RefreshTokenDays => refreshTokenDays;

    /// <summary>액세스 토큰 수명(분).</summary>
    public int AccessTokenMinutes => accessTokenMinutes;

    /// <summary>액세스 토큰(JWT) + 이미 발급된 리프레시 토큰 원문으로 응답 봉투를 만든다.</summary>
    public TokenResponse Issue(Guid playerId, string displayName, string refreshToken)
    {
        var isAdmin = string.Equals(playerId.ToString(), adminPlayerId, StringComparison.OrdinalIgnoreCase);
        var claims = new List<Claim>
        {
            new("sub", playerId.ToString()),
            new("name", displayName),
            // jti: 토큰 고유 식별자. 같은 초에 재발급돼도 토큰이 바이트 동일해지지 않게 하고,
            // 향후 토큰 단위 폐기(블랙리스트)의 키로 쓸 수 있다.
            new("jti", Guid.NewGuid().ToString())
        };
        if (isAdmin) claims.Add(new Claim("role", "admin"));

        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(accessTokenMinutes);
        var token = new JwtSecurityToken(issuer, audience, claims, expires: expires, signingCredentials: creds);
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        var roles = isAdmin ? new List<string> { "admin" } : [];
        return new TokenResponse(jwt, "Bearer", accessTokenMinutes * 60L, refreshToken, playerId, displayName, roles);
    }
}

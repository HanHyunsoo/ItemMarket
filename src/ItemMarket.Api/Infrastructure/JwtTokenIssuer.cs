using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ItemMarket.Contracts.Auth;
using Microsoft.IdentityModel.Tokens;

namespace ItemMarket.Api.Infrastructure;

/// <summary>
/// JWT(HS256) 발급기. sub=playerId, name=표시명 클레임을 담고,
/// AdminPlayerId와 일치하는 플레이어에게만 admin 롤을 부여한다.
/// </summary>
public sealed class JwtTokenIssuer(
    SymmetricSecurityKey signingKey,
    string issuer,
    string audience,
    string adminPlayerId,
    int expiresMinutes)
{
    public TokenResponse Issue(Guid playerId, string displayName)
    {
        var isAdmin = string.Equals(playerId.ToString(), adminPlayerId, StringComparison.OrdinalIgnoreCase);
        var claims = new List<Claim>
        {
            new("sub", playerId.ToString()),
            new("name", displayName)
        };
        if (isAdmin) claims.Add(new Claim("role", "admin"));

        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(expiresMinutes);
        var token = new JwtSecurityToken(issuer, audience, claims, expires: expires, signingCredentials: creds);
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        var roles = isAdmin ? new List<string> { "admin" } : [];
        return new TokenResponse(jwt, "Bearer", expiresMinutes * 60L, playerId, displayName, roles);
    }
}

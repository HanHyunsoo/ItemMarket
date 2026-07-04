using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace ItemMarket.Api.Infrastructure;

/// <summary>인증(JWT Bearer, HS256) + 인가(admin 롤) 구성.</summary>
public static class AuthSetup
{
    public static WebApplicationBuilder AddMarketAuth(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var secret = cfg["Auth:Secret"] ?? throw new InvalidOperationException("Auth:Secret 누락");
        var issuer = cfg["Auth:Issuer"] ?? "item-market";
        var audience = cfg["Auth:Audience"] ?? "item-market-client";
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));

        builder.Services.AddSingleton(new JwtTokenIssuer(
            signingKey, issuer, audience,
            adminPlayerId: cfg["Auth:AdminPlayerId"] ?? "33333333-3333-3333-3333-333333333333",
            expiresMinutes: cfg.GetValue("Auth:ExpiresMinutes", 480)));

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false; // "sub"/"role" 클레임명을 그대로 유지
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = signingKey,
                    NameClaimType = "name",
                    RoleClaimType = "role"
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("admin", p => p.RequireRole("admin"));

        return builder;
    }
}

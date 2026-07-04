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

                // WebSocket은 Authorization 헤더를 못 실으므로, 허브 경로(/hubs/*)에 한해
                // ?access_token= 쿼리에서 토큰을 읽는다(docs/realtime-contract.md).
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var accessToken = ctx.Request.Query["access_token"];
                        var path = ctx.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                            ctx.Token = accessToken;
                        return Task.CompletedTask;
                    }
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("admin", p => p.RequireRole("admin"));

        return builder;
    }
}

using System.Threading.RateLimiting;
using ItemMarket.Contracts.Common;
using Microsoft.AspNetCore.RateLimiting;

namespace ItemMarket.Api.Infrastructure;

/// <summary>
/// 레이트 리미팅 — 주문 등록(POST /api/orders)을 플레이어별로 제한한다.
/// 파티션 키는 토큰의 sub 클레임(플레이어), 익명은 원격 IP로 폴백한다.
/// 거부 시 표준 ApiResponse 실패 봉투(ErrorCode.RateLimited)로 429를 반환해
/// 프론트가 기대하는 계약을 깨지 않는다. 한도는 appsettings("RateLimiting")로 조절하며
/// 기본값은 데모/테스트가 throttle되지 않도록 넉넉하게 잡는다.
/// </summary>
public static class RateLimiting
{
    /// <summary>주문 등록에 적용하는 정책 이름.</summary>
    public const string OrdersPolicy = "orders";

    public static WebApplicationBuilder AddMarketRateLimiting(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        // 넉넉한 기본값: 플레이어당 창(window)마다 permit 회 허용. 데모/기존 테스트는 안 걸린다.
        var permitLimit = cfg.GetValue("RateLimiting:Orders:PermitLimit", 1000);
        var windowSeconds = cfg.GetValue("RateLimiting:Orders:WindowSeconds", 10);
        var queueLimit = cfg.GetValue("RateLimiting:Orders:QueueLimit", 0);

        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy(OrdersPolicy, ctx =>
            {
                // 인증 사용자는 sub(플레이어)로, 익명은 IP로 파티션.
                var partitionKey = ctx.User.FindFirst("sub")?.Value
                    ?? ctx.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromSeconds(windowSeconds),
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                });
            });

            // 거부 응답: 표준 봉투 + 429 + Retry-After.
            options.OnRejected = async (context, token) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                await response.WriteAsJsonAsync(
                    ApiResponse<object>.Fail(ErrorCode.RateLimited, "요청이 너무 많습니다. 잠시 후 다시 시도하세요."),
                    ApiResults.JsonOptions, cancellationToken: token);
            };
        });

        return builder;
    }
}

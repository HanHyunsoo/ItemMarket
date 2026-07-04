using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ItemMarket.Contracts.Common;
using ItemMarket.Grains.Data;

namespace ItemMarket.Api.Infrastructure;

/// <summary>엔드포인트 공통 헬퍼: 플레이어 식별 + 도메인 예외 → ApiResponse 봉투 변환.</summary>
public static class ApiResults
{
    /// <summary>예상외 예외 로깅용. Program에서 앱 로거로 초기화한다.</summary>
    internal static ILogger? Logger { get; set; }

    /// <summary>
    /// REST 직렬화와 동일 규칙(enum 문자열 + camelCase). 멱등성 응답을 손수 직렬화하거나
    /// 레이트리미터 거부 봉투를 쓸 때 프레임워크 출력과 바이트 동형(identical)이 되도록 공유한다.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>토큰의 sub 클레임에서 플레이어 식별. 클라이언트가 보낸 id는 신뢰하지 않는다.</summary>
    public static Guid CurrentPlayer(ClaimsPrincipal user)
    {
        var sub = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : throw new DomainException(ErrorCode.Unauthorized, "토큰에 유효한 sub가 없습니다.");
    }

    /// <summary>도메인 오류 코드 → HTTP 상태.</summary>
    internal static int StatusFor(ErrorCode code) => code switch
    {
        ErrorCode.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCode.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCode.PlayerNotFound or ErrorCode.TemplateNotFound or ErrorCode.InstanceNotFound
            or ErrorCode.OrderNotFound => StatusCodes.Status404NotFound,
        ErrorCode.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorCode.IdempotencyInProgress => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    };

    // 도메인 예외를 ApiResponse 실패 봉투 + 적절한 HTTP 상태로 변환.
    // 예상 밖 예외(NpgsqlException 등)도 봉투를 유지한 500으로 감싼다 —
    // 그렇지 않으면 프론트가 기대하는 ApiResponse 계약이 깨진 원시 500이 샌다.
    public static async Task<IResult> Exec<T>(Func<Task<T>> action)
    {
        try
        {
            var data = await action();
            return Results.Ok(ApiResponse<T>.Ok(data));
        }
        catch (DomainException ex)
        {
            return Results.Json(ApiResponse<T>.Fail(ex.Code, ex.Message), statusCode: StatusFor(ex.Code));
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "처리되지 않은 예외");
            return Results.Json(ApiResponse<T>.Fail(ErrorCode.Unknown, "서버 내부 오류가 발생했습니다."),
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// 멱등성 실행: Idempotency-Key 헤더가 있을 때 사용. (player, key) 슬롯을 청구해
    /// 원본이면 action을 실행하고 직렬화된 응답을 저장 후 반환, 중복이면 저장된 응답을
    /// 그대로 반환한다(아직 처리중이면 409). 실패 시 슬롯을 비워 재시도를 허용한다.
    /// </summary>
    public static async Task<IResult> ExecIdempotent<T>(
        MarketRepository repo, ClaimsPrincipal user, string key, Func<Task<T>> action)
    {
        Guid pid;
        try { pid = CurrentPlayer(user); }
        catch (DomainException ex) { return Results.Json(ApiResponse<T>.Fail(ex.Code, ex.Message), statusCode: StatusFor(ex.Code)); }

        // 슬롯 청구(원자적). 실패하면 이미 존재하는 요청.
        var claimed = await repo.TryClaimIdempotencyAsync(pid, key);
        if (!claimed)
        {
            var existing = await repo.GetIdempotencyAsync(pid, key);
            if (existing.ResponseJson is null)
            {
                // 원본이 아직 처리중(response=NULL) → 재실행하지 않고 409로 알린다.
                return Results.Json(
                    ApiResponse<T>.Fail(ErrorCode.IdempotencyInProgress, "동일 Idempotency-Key 요청이 처리 중입니다. 잠시 후 재시도하세요."),
                    statusCode: StatusFor(ErrorCode.IdempotencyInProgress));
            }
            // 저장된 원본 응답을 바이트 그대로 반환(중복 주문 없음).
            return Results.Content(existing.ResponseJson, "application/json", statusCode: StatusCodes.Status200OK);
        }

        // 원본: action 실행 후 성공 응답을 저장.
        try
        {
            var data = await action();
            var envelope = ApiResponse<T>.Ok(data);
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            await repo.StoreIdempotencyResponseAsync(pid, key, json);
            return Results.Content(json, "application/json", statusCode: StatusCodes.Status200OK);
        }
        catch (DomainException ex)
        {
            // 실패는 저장하지 않는다 — 슬롯을 비워 같은 키로 재시도 가능하게.
            await SafeReleaseAsync(repo, pid, key);
            return Results.Json(ApiResponse<T>.Fail(ex.Code, ex.Message), statusCode: StatusFor(ex.Code));
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "처리되지 않은 예외(멱등)");
            await SafeReleaseAsync(repo, pid, key);
            return Results.Json(ApiResponse<T>.Fail(ErrorCode.Unknown, "서버 내부 오류가 발생했습니다."),
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task SafeReleaseAsync(MarketRepository repo, Guid pid, string key)
    {
        try { await repo.ReleaseIdempotencyAsync(pid, key); }
        catch (Exception ex) { Logger?.LogError(ex, "멱등 슬롯 해제 실패 (player={PlayerId}, key={Key})", pid, key); }
    }
}

using System.Security.Claims;
using ItemMarket.Contracts.Common;
using ItemMarket.Grains.Data;

namespace ItemMarket.Api.Infrastructure;

/// <summary>엔드포인트 공통 헬퍼: 플레이어 식별 + 도메인 예외 → ApiResponse 봉투 변환.</summary>
public static class ApiResults
{
    /// <summary>예상외 예외 로깅용. Program에서 앱 로거로 초기화한다.</summary>
    internal static ILogger? Logger { get; set; }

    /// <summary>토큰의 sub 클레임에서 플레이어 식별. 클라이언트가 보낸 id는 신뢰하지 않는다.</summary>
    public static Guid CurrentPlayer(ClaimsPrincipal user)
    {
        var sub = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : throw new DomainException(ErrorCode.Unauthorized, "토큰에 유효한 sub가 없습니다.");
    }

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
            var status = ex.Code switch
            {
                ErrorCode.Unauthorized => StatusCodes.Status401Unauthorized,
                ErrorCode.Forbidden => StatusCodes.Status403Forbidden,
                ErrorCode.PlayerNotFound or ErrorCode.TemplateNotFound or ErrorCode.InstanceNotFound
                    or ErrorCode.OrderNotFound => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status400BadRequest
            };
            return Results.Json(ApiResponse<T>.Fail(ex.Code, ex.Message), statusCode: status);
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "처리되지 않은 예외");
            return Results.Json(ApiResponse<T>.Fail(ErrorCode.Unknown, "서버 내부 오류가 발생했습니다."),
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}

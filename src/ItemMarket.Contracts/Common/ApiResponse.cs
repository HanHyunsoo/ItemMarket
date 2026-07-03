namespace ItemMarket.Contracts.Common;

/// <summary>
/// 모든 API 응답의 공통 봉투(envelope). 프론트는 Success로 분기하고
/// 실패 시 Error.Code(열거형)로 로직 분기, Error.Message로 사용자 표시.
/// </summary>
public sealed record ApiResponse<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public ApiError? Error { get; init; }

    public static ApiResponse<T> Ok(T data) => new() { Success = true, Data = data };
    public static ApiResponse<T> Fail(ApiError error) => new() { Success = false, Error = error };
    public static ApiResponse<T> Fail(ErrorCode code, string message) => Fail(new ApiError(code, message));
}

/// <summary>기계가 분기하는 Code + 사람이 읽는 Message. Details는 검증 오류 목록 등.</summary>
public sealed record ApiError(ErrorCode Code, string Message, IReadOnlyList<string>? Details = null);

using ItemMarket.Contracts.Common;

namespace ItemMarket.Grains.Data;

/// <summary>
/// 도메인 규칙 위반을 나타내는 예외. API 계층에서 잡아 <see cref="ApiResponse{T}"/>
/// 실패 봉투 + 적절한 HTTP 상태코드로 변환한다.
/// </summary>
public sealed class DomainException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code { get; } = code;
}

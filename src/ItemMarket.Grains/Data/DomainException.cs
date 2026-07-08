using ItemMarket.Contracts.Common;
using Orleans;

namespace ItemMarket.Grains.Data;

/// <summary>
/// 도메인 규칙 위반을 나타내는 예외. API 계층에서 잡아 <see cref="ApiResponse{T}"/>
/// 실패 봉투 + 적절한 HTTP 상태코드로 변환한다.
///
/// <para><b>[GenerateSerializer]</b>: 그레인이 던진 이 예외가 <b>실로 경계를 넘을 때</b> Orleans가
/// 코덱으로 <see cref="Code"/>를 보존해 재구성하도록 한다. 이게 없으면 다중 실로(adonet 클러스터링)에서
/// 예외가 일반 예외로 강등돼 API가 도메인 코드 대신 500을 반환한다(M1). 단일 실로(co-host)는 인프로세스라
/// 원래도 정상이지만, 클러스터 경로의 에러 계약을 보장한다. Exception 타입은 JSON 직렬화기 대상에서
/// 제외하고(<c>OrleansHosting</c>) 이 생성 코덱을 쓴다 — JSON은 Exception을 역직렬화할 수 없기 때문.</para>
/// </summary>
[GenerateSerializer]
public sealed class DomainException(ErrorCode code, string message) : Exception(message)
{
    [Id(0)]
    public ErrorCode Code { get; } = code;
}

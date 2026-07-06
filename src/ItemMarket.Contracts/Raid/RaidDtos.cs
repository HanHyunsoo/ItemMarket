using ItemMarket.Contracts.Stash;

namespace ItemMarket.Contracts.Raid;

/// <summary>
/// 익스트랙션 레이드 세션 상태. 서비스 계층 상태기계:
///   Active    = 레이드 진행 중. 로드아웃 아이템이 "위험(at-risk)"으로 잠겨 판매/이동 불가.
///   Extracted = 생존. 반입+획득 아이템이 전량 소유로 복귀(스태시 자동 배치 대상).
///   Died      = 사망. 위험 아이템 전량 소실(스태시(안전)는 무관).
/// </summary>
public enum RaidStatus
{
    Active,
    Extracted,
    Died
}

/// <summary>위험 아이템의 출처. 반입(로드아웃) 또는 레이드 중 획득(전리품).</summary>
public enum RaidItemSource
{
    Brought,
    Looted
}

/// <summary>
/// 레이드 세션의 위험 아이템 한 줄(raid_session_item = 레이드 에스크로 스냅샷).
/// 스택형은 TemplateId+Quantity, 유니크는 InstanceId(+TemplateId, Quantity=1).
/// </summary>
public sealed record RaidSessionItemDto(
    StashEntryKind Kind,
    int TemplateId,
    Guid? InstanceId,
    int Quantity,
    RaidItemSource Source);

/// <summary>레이드 세션 스냅샷. Status와 현재 위험 아이템 목록(Items).</summary>
public sealed record RaidSessionDto(
    Guid Id,
    Guid PlayerId,
    RaidStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt,
    IReadOnlyList<RaidSessionItemDto> Items);

/// <summary>
/// 레이드 이력 한 줄(해결된 세션: EXTRACTED/DIED). 결과 화면/전적 조회용.
/// Items는 그 세션의 반입(BROUGHT)/획득(LOOTED) 스냅샷(source + qty 포함).
/// </summary>
public sealed record RaidHistoryEntryDto(
    Guid Id,
    RaidStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt,
    IReadOnlyList<RaidSessionItemDto> Items);

/// <summary>
/// 레이드 중 전리품 획득 시뮬레이션 요청(MVP — 실제 통합에서는 게임 서버가 호출).
/// 전리품 종류는 요청 <see cref="Kind"/>가 아니라 <b>템플릿의 stackable 플래그로 결정</b>한다
/// (Kind는 사실상 무시 — 게임 서버가 {TemplateId, Quantity}만 보내도 유니크면 인스턴스를 materialize).
/// 스택이면 Quantity(1..max_stack), 유니크면 Quantity 무시(1자루)·Durability/Attachments 선택(미지정 시 템플릿 기본).
/// </summary>
public sealed record AddLootRequest(
    StashEntryKind Kind,
    int TemplateId,
    int? Quantity = null,
    int? Durability = null,
    IReadOnlyList<string>? Attachments = null);

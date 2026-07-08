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
/// 출격 존(리스크/보상 티어). 존이 드롭 rarity 가중치와 loot당 사망확률 상승률을 함께 결정한다:
/// 고위험 존일수록 좋은 등급이 잘 나오지만 사망확률이 빠르게 오른다(리스크/보상).
/// </summary>
public enum RaidZone
{
    Scav,   // 무료 최저 티어(수수료 0, 최저 드롭) — 재기용 진입 장벽 낮춤
    Low,
    Med,
    High
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
    IReadOnlyList<RaidSessionItemDto> Items,
    DateTimeOffset? DeadlineAt = null,   // ACTIVE 세션의 출격 마감(초과 시 탈출 실패=사망). 해결된 세션은 null.
    int DeathChanceBps = 0);             // 현재 누적 사망확률(bps). extract 시 이 확률로 사망 롤. 표시는 min(10000).

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

/// <summary>출격 요청. 존(리스크/보상 티어)을 선택한다. 미지정 시 Med.</summary>
public sealed record StartRaidRequest(RaidZone Zone = RaidZone.Med);

/// <summary>존 메타(출격 화면용): 존별 출격 수수료, loot당 사망확률 상승률, 기본 사망확률(floor).
/// 프론트가 배당(수수료·기본 위험·루팅당 증가)을 표시한다.</summary>
public sealed record ZoneInfoDto(RaidZone Zone, long EntryFee, int DeathChancePerLootBps, int BaseDeathBps);

/// <summary>
/// 루팅(scavenge) 결과. 서버가 세션 존의 rarity 가중치로 무엇을·얼마나 드롭할지 결정하므로,
/// 클라이언트는 이번에 획득한 것(<see cref="Dropped"/>)과 갱신된 세션(<see cref="Session"/>)을 함께 받는다.
/// 마감(deadline)을 넘겨 루팅하면 탈출 실패=사망 정산되어 Dropped=null, Session.Status=Died가 된다.
/// </summary>
public sealed record LootResultDto(
    RaidSessionItemDto? Dropped,
    RaidSessionDto Session);

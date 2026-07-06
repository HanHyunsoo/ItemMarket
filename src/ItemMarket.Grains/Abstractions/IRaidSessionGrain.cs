using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Raid;

namespace ItemMarket.Grains.Abstractions;

/// <summary>
/// 익스트랙션 레이드 세션 grain(키 = playerId). 플레이어당 단일 활성화 ⇒ 한 번에 하나의
/// 레이드만(이중 진입 없음). 상태의 소스오브트루스는 Postgres(raid_session)이며 grain은
/// 매 호출마다 DB에서 상태를 읽어 정산한다(다른 grain과 동일한 무상태 pass-through 설계).
///
/// 상태기계: (없음) --StartRaid--> ACTIVE --Extract--> EXTRACTED
///                                        \--Die-----> DIED
/// </summary>
public interface IRaidSessionGrain : IGrainWithGuidKey
{
    /// <summary>현재 세션 스냅샷(ACTIVE 우선, 없으면 최근 세션). 세션 이력이 없으면 null.</summary>
    Task<RaidSessionDto?> Get();

    /// <summary>해결된(EXTRACTED/DIED) 과거 세션 이력을 페이지네이션 조회(아이템 스냅샷 포함).</summary>
    Task<PagedResult<RaidHistoryEntryDto>> GetHistory(int page, int size);

    /// <summary>
    /// 레이드 시작: ACTIVE 세션이 없어야 한다(있으면 RaidActive). 스태시 밖 전부(장착 장비 +
    /// 장착된 백팩/리그 내용물 + 주머니)를 위험(at-risk)으로 잠그고(판매/이동 불가) 위험 스냅샷으로
    /// 옮긴다. 스태시 밖이 전부 비어 있으면 RaidNothingToDeploy(장비만 있어도 항상 성공한다).
    /// 존(zone)이 드롭 등급 가중치·loot당 사망확률 상승률을 결정한다. 반환은 새 ACTIVE 세션.
    /// </summary>
    Task<RaidSessionDto> StartRaid(RaidZone zone);

    /// <summary>루팅(scavenge): 서버가 세션 존의 rarity 가중치로 무엇을·얼마나 드롭할지 결정해
    /// ACTIVE 세션에 LOOTED 위험 아이템으로 추가하고 사망확률을 올린다. 이번 드롭과 갱신 세션을 반환.
    /// 마감 초과 시 탈출 실패=사망으로 정산되어 Dropped=null, Session.Status=Died.</summary>
    Task<LootResultDto> Scavenge();

    /// <summary>생존 탈출: ACTIVE→EXTRACTED. 반입+획득 전량을 소유로 복귀(→ 다음 GET에서 STASH 자동 배치).</summary>
    Task<RaidSessionDto> Extract();

    /// <summary>사망: ACTIVE→DIED. 위험 아이템 전량 소실. 스태시(안전)는 무관.</summary>
    Task<RaidSessionDto> Die();
}

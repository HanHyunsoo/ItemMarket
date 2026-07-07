namespace ItemMarket.Contracts.Leaderboard;

/// <summary>리더보드 한 줄: 플레이어와 그 지표 값(캡 잔액 또는 탈출 횟수).</summary>
public sealed record LeaderEntryDto(Guid PlayerId, string DisplayName, long Value);

/// <summary>
/// 호출자 본인의 순위·지표. Top-N 밖이어도 "당신은 N위 / 전체 M명"을 보여줘 상위권 밖 플레이어에게도
/// 위치 피드백을 준다. 생환 기록이 없으면(0회) ExtractionsRank는 null(순위 미정).
/// </summary>
public sealed record LeaderMeDto(
    Guid PlayerId,
    long NetWorth, long NetWorthRank,
    long Extractions, long? ExtractionsRank,
    long TotalPlayers);

/// <summary>
/// 리더보드 스냅샷. 경제에 판돈이 생긴 뒤의 사회적 목표 — 최다 순자산(지갑+보유 아이템 가치)과
/// 최다 생환(탈출) 순위. 순자산 기준이라 캡을 장비로 바꿔도(거래) 순위가 유지돼 소비를 억제하지 않는다.
/// Me는 호출자 본인의 순위(Top-N 밖이어도 표시).
/// </summary>
public sealed record LeaderboardDto(
    IReadOnlyList<LeaderEntryDto> TopNetWorth,
    IReadOnlyList<LeaderEntryDto> TopExtractions,
    LeaderMeDto Me);

namespace ItemMarket.Contracts.Leaderboard;

/// <summary>리더보드 한 줄: 플레이어와 그 지표 값(캡 잔액 또는 탈출 횟수).</summary>
public sealed record LeaderEntryDto(Guid PlayerId, string DisplayName, long Value);

/// <summary>
/// 리더보드 스냅샷. 경제에 판돈이 생긴 뒤의 사회적 목표 — 최다 캡 보유와 최다 생환(탈출) 순위.
/// </summary>
public sealed record LeaderboardDto(
    IReadOnlyList<LeaderEntryDto> TopCaps,
    IReadOnlyList<LeaderEntryDto> TopExtractions);

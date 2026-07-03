namespace ItemMarket.Contracts.Auth;

/// <summary>
/// 로그인 요청. (개발 스코프) 비밀번호 없이 시드 플레이어 ID로 토큰 발급.
/// 실제 서비스라면 자격증명 검증 단계가 여기 들어간다.
/// </summary>
public sealed record LoginRequest(Guid PlayerId);

/// <summary>
/// JWT 발급 결과. 이후 요청은 Authorization: Bearer {AccessToken}.
/// playerId는 토큰의 sub 클레임에서 서버가 신뢰. Roles에 "admin" 포함 시 운영 API 접근.
/// </summary>
public sealed record TokenResponse(
    string AccessToken,
    string TokenType,       // "Bearer"
    long ExpiresIn,         // 초
    Guid PlayerId,
    string DisplayName,
    IReadOnlyList<string> Roles);

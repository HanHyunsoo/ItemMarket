namespace ItemMarket.Contracts.Auth;

/// <summary>
/// 로그인 요청. (개발 스코프) 비밀번호 없이 시드 플레이어 ID로 토큰 발급.
/// 실제 서비스라면 자격증명 검증 단계가 여기 들어간다.
/// </summary>
public sealed record LoginRequest(Guid PlayerId);

/// <summary>
/// JWT 발급 결과. 이후 요청은 Authorization: Bearer {AccessToken}.
/// playerId는 토큰의 sub 클레임에서 서버가 신뢰. Roles에 "admin" 포함 시 운영 API 접근.
///
/// AccessToken은 짧게(기본 15분), RefreshToken은 길게(기본 14일) 산다. 액세스가 만료되면
/// POST /api/auth/refresh 로 RefreshToken을 제시해 새 쌍을 받는다(로테이션: 옛 토큰 폐기).
/// RefreshToken 원문은 클라이언트만 보관하며, 서버는 SHA-256 해시만 저장한다.
/// </summary>
public sealed record TokenResponse(
    string AccessToken,
    string TokenType,             // "Bearer"
    long AccessTokenExpiresIn,    // 액세스 토큰 만료까지 남은 초
    string RefreshToken,          // 리프레시 토큰 원문(클라이언트 보관용)
    Guid PlayerId,
    string DisplayName,
    IReadOnlyList<string> Roles);

/// <summary>
/// 액세스 토큰 갱신/로그아웃 요청. 로그인 시 받은 RefreshToken 원문을 담는다.
/// refresh: 유효하면 새 TokenResponse(로테이션) 반환. logout: 해당 토큰을 폐기.
/// </summary>
public sealed record RefreshRequest(string RefreshToken);

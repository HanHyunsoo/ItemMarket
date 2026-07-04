using System.Security.Cryptography;

namespace ItemMarket.Api.Infrastructure;

/// <summary>
/// 리프레시 토큰 원문 생성기. 256비트 CSPRNG 난수를 URL-safe base64로 인코딩한다.
/// 저장·조회 시의 해싱(SHA-256)은 <see cref="ItemMarket.Grains.Data.MarketRepository"/>가
/// 단일 지점에서 담당한다(원문은 DB에 남기지 않는다).
/// </summary>
public static class RefreshTokens
{
    /// <summary>추측 불가능한 새 리프레시 토큰 원문을 만든다(클라이언트에만 반환).</summary>
    public static string NewRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

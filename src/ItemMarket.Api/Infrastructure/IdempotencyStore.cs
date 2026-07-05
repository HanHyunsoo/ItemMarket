using StackExchange.Redis;

namespace ItemMarket.Api.Infrastructure;

/// <summary>멱등 슬롯 청구 결과.</summary>
public enum IdempotencyStatus
{
    /// <summary>이 요청이 슬롯을 새로 선점했다(원본). 호출자가 action을 실행해야 한다.</summary>
    Claimed,

    /// <summary>동일 키의 원본이 아직 처리중(응답 미저장) → 409로 알려야 한다.</summary>
    InProgress,

    /// <summary>동일 키의 원본이 완료됨. ResponseJson을 그대로 반환한다.</summary>
    Completed
}

/// <summary>멱등 청구의 결과 + (완료 시) 저장된 응답 JSON.</summary>
public readonly record struct IdempotencyClaim(IdempotencyStatus Status, string? ResponseJson);

/// <summary>
/// 주문 등록 멱등성 저장소. (player, key) 슬롯을 원자적으로 청구하고,
/// 원본 완료 시 직렬화된 응답을 저장/조회하며, 실패 시 슬롯을 해제한다.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>(player, key) 슬롯을 원자적으로 청구한다.</summary>
    Task<IdempotencyClaim> TryClaimAsync(Guid playerId, string key);

    /// <summary>원본 완료 후 직렬화된 응답 JSON을 저장한다.</summary>
    Task StoreResponseAsync(Guid playerId, string key, string responseJson);

    /// <summary>원본 처리가 실패하면 슬롯을 비워 같은 키로 재시도를 허용한다.</summary>
    Task ReleaseAsync(Guid playerId, string key);
}

/// <summary>
/// Redis 기반 멱등성 저장소. 키는 idem:{playerId}:{key}.
///  - 청구: SET key INFLIGHT NX EX ttl. 성공 = 원본(Claimed). 실패 시 GET 하여
///    값이 INFLIGHT 마커면 InProgress, 아니면 저장된 응답(Completed).
///  - 저장: 응답 JSON으로 덮어쓰기(동일 TTL 재설정).
///  - 해제: DEL.
/// 마커는 응답 JSON(항상 여는 중괄호로 시작)과 충돌하지 않는 센티넬 문자열이다.
/// </summary>
public sealed class RedisIdempotencyStore(IConnectionMultiplexer redis, TimeSpan ttl) : IIdempotencyStore
{
    /// <summary>처리중 슬롯을 표시하는 센티넬. 응답 JSON은 항상 여는 중괄호로 시작하므로 충돌하지 않는다.</summary>
    public const string InflightMarker = "INFLIGHT";

    /// <summary>Redis 키 규칙. 테스트에서 슬롯을 직접 조작할 때 재사용한다.</summary>
    public static string RedisKeyFor(Guid playerId, string key) => $"idem:{playerId}:{key}";

    private static RedisKey KeyFor(Guid playerId, string key) => (RedisKey)RedisKeyFor(playerId, key);

    public async Task<IdempotencyClaim> TryClaimAsync(Guid playerId, string key)
    {
        var db = redis.GetDatabase();
        var rk = KeyFor(playerId, key);

        // 원자적 선점: 없을 때만(NX) INFLIGHT 마커를 TTL과 함께 기록.
        var claimed = await db.StringSetAsync(rk, InflightMarker, ttl, When.NotExists);
        if (claimed)
            return new IdempotencyClaim(IdempotencyStatus.Claimed, null);

        // 이미 존재 → 처리중인지 완료인지 판별.
        var existing = await db.StringGetAsync(rk);
        if (!existing.HasValue || existing == InflightMarker)
            return new IdempotencyClaim(IdempotencyStatus.InProgress, null);

        return new IdempotencyClaim(IdempotencyStatus.Completed, existing.ToString());
    }

    public Task StoreResponseAsync(Guid playerId, string key, string responseJson)
        => redis.GetDatabase().StringSetAsync(KeyFor(playerId, key), responseJson, ttl);

    public Task ReleaseAsync(Guid playerId, string key)
        => redis.GetDatabase().KeyDeleteAsync(KeyFor(playerId, key));
}

/// <summary>
/// Redis 미구성(단일 인스턴스 개발) 시의 무저장 구현. 항상 "청구 성공"을 반환해
/// 멱등 헤더를 사실상 무시하고 주문을 평소대로 등록한다(중복 방어 없음).
/// </summary>
public sealed class NullIdempotencyStore : IIdempotencyStore
{
    public Task<IdempotencyClaim> TryClaimAsync(Guid playerId, string key)
        => Task.FromResult(new IdempotencyClaim(IdempotencyStatus.Claimed, null));

    public Task StoreResponseAsync(Guid playerId, string key, string responseJson) => Task.CompletedTask;

    public Task ReleaseAsync(Guid playerId, string key) => Task.CompletedTask;
}

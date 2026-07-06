namespace ItemMarket.Contracts.Common;

/// <summary>
/// 도메인 오류 코드. 문자열로 직렬화(JsonStringEnumConverter)하여
/// 프론트/백엔드가 안정적으로 분기할 수 있게 한다.
/// </summary>
public enum ErrorCode
{
    Unknown = 0,
    ValidationError,
    Unauthorized,           // 토큰 없음/만료/서명 불일치 (HTTP 401)
    Forbidden,              // 권한 부족: 어드민 롤 필요 등 (HTTP 403)
    PlayerNotFound,
    TemplateNotFound,
    InstanceNotFound,
    InstanceNotOwned,
    InsufficientFunds,      // 병뚜껑 잔액 부족(매수 주문 에스크로 실패)
    InsufficientQuantity,   // 스택 수량 부족(매도 주문 에스크로 실패)
    OrderNotFound,
    OrderNotOwned,
    OrderAlreadyClosed,     // 이미 체결완료/취소된 주문
    StackableMismatch,      // 스택형에 InstanceId 지정, 혹은 유니크에 Quantity>1 등
    SlotMismatch,           // 장착 슬롯 불일치: 인스턴스 template.equip_slot ≠ 요청 슬롯, 또는 슬롯 점유
    PlacementInvalid,       // 스태시 배치 불가: 경계 밖 또는 다른 아이템과 겹침
    RateLimited,            // 요청 빈도 초과(HTTP 429) — 레이트 리미터가 거부
    IdempotencyInProgress,  // 동일 Idempotency-Key 원본이 아직 처리중(HTTP 409)
    IdempotencyUnavailable, // 멱등 저장소(Redis) 미구성으로 Idempotency-Key 처리 불가(HTTP 503)
    RaidActive,             // 이미 진행 중인 레이드가 있음(중복 StartRaid, HTTP 409)
    RaidNotFound,           // 진행 중인 레이드가 없음(Extract/Die/AddLoot 대상 없음, HTTP 404)
    RaidNothingToDeploy     // 스태시 밖(장비/주머니/중첩 컨테이너)이 전부 비어 반입할 것이 없음(StartRaid 거부, HTTP 400)
}

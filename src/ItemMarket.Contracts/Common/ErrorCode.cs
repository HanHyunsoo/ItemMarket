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
    PlacementInvalid        // 스태시 배치 불가: 경계 밖 또는 다른 아이템과 겹침
}

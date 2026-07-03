using ItemMarket.Contracts.Trades;

namespace ItemMarket.Contracts.Orders;

public enum OrderSide { Buy, Sell }

public enum OrderStatus { Open, PartiallyFilled, Filled, Cancelled }

/// <summary>
/// 주문 등록 요청.
/// - 스택형(FOOD/MEDICAL/AMMO): InstanceId=null, Quantity>=1
/// - 유니크 매도(MELEE/GUN): InstanceId 지정, Quantity=1
/// - 유니크 매수: InstanceId=null (가장 싼 매도 인스턴스와 매칭)
/// UnitPrice는 병뚜껑 단가(bigint). 매수는 상한가, 매도는 하한가로 동작.
/// </summary>
public sealed record PlaceOrderRequest(
    OrderSide Side,
    int ItemTemplateId,
    long UnitPrice,
    int Quantity,
    Guid? InstanceId = null);

public sealed record OrderDto(
    Guid Id,
    Guid PlayerId,
    OrderSide Side,
    int ItemTemplateId,
    long UnitPrice,
    int Quantity,
    int RemainingQuantity,
    OrderStatus Status,
    Guid? InstanceId,
    DateTimeOffset CreatedAt);

/// <summary>
/// 주문 등록 결과. 즉시 체결된 부분은 Fills로, 남은 물량은 Order(잔량)로 반환.
/// 완전 체결이면 Fills만 채워지고 Order.Status=Filled.
/// </summary>
public sealed record PlaceOrderResult(
    OrderDto Order,
    IReadOnlyList<TradeDto> Fills);

/// <summary>호가창 한 가격대 집계.</summary>
public sealed record OrderBookLevelDto(long UnitPrice, int Quantity, int OrderCount);

/// <summary>템플릿별 호가창 스냅샷. Bids=매수(가격 내림차순), Asks=매도(가격 오름차순).</summary>
public sealed record OrderBookSnapshotDto(
    int ItemTemplateId,
    IReadOnlyList<OrderBookLevelDto> Bids,
    IReadOnlyList<OrderBookLevelDto> Asks);

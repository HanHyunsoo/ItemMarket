using ItemMarket.Contracts.Orders;

namespace ItemMarket.Grains.Abstractions;

/// <summary>
/// 매칭 엔진(키 = templateId). Orleans 단일 활성화 보장 덕에 템플릿별로
/// 매칭이 단일 스레드처럼 직렬화되어 매칭 레이스가 원천 차단된다.
/// </summary>
public interface IOrderBookGrain : IGrainWithIntegerKey
{
    Task<PlaceOrderResult> PlaceOrder(Guid playerId, PlaceOrderRequest request);
    Task<OrderDto> CancelOrder(Guid playerId, Guid orderId, bool isAdmin);
    Task<OrderBookSnapshotDto> GetSnapshot();
}

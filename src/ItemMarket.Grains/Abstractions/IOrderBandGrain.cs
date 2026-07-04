using ItemMarket.Contracts.Orders;

namespace ItemMarket.Grains.Abstractions;

/// <summary>
/// 가격 밴드 매칭 엔진(키 = "{templateId}:{band}"). 밴딩 ON일 때만 사용된다.
/// 한 템플릿의 호가창을 가격 밴드(= unitPrice / PriceBandSize)로 샤딩한 조각 하나로,
/// 그 밴드에 속한 잔존 주문의 소유·밴드 내부 매칭·에스크로·정산·스냅샷을 담당한다.
///
/// Orleans 단일 활성화 + 논-리엔트런트 보장 덕에 <b>밴드별로</b> 매칭이 직렬화되어
/// 매칭 레이스가 차단되며, 서로 다른 밴드 grain은 병렬로 흐른다(핫 grain 상한 돌파).
/// 밴드 경계를 넘는 매칭은 일어나지 않는다(밴드-격리 매칭 — 의도된 트레이드오프).
/// </summary>
public interface IOrderBandGrain : IGrainWithStringKey
{
    Task<PlaceOrderResult> PlaceOrder(Guid playerId, PlaceOrderRequest request);
    Task<OrderDto> CancelOrder(Guid playerId, Guid orderId, bool isAdmin);
    Task<OrderBookSnapshotDto> GetSnapshot();
}

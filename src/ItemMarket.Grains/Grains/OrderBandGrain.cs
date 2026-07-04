using ItemMarket.Contracts.Orders;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;
using Microsoft.Extensions.Logging;

namespace ItemMarket.Grains.Grains;

/// <summary>
/// 가격 밴드 매칭 grain(키 = "{templateId}:{band}"). 밴딩 ON일 때 코디네이터
/// (<see cref="OrderBookGrain"/>)가 밴드별로 라우팅한다.
///
/// 논-리엔트런트(기본)라 자기 밴드의 매칭은 단일 스레드처럼 직렬화된다. 매칭·에스크로·정산·
/// 스냅샷 로직은 <see cref="OrderBookEngine"/>을 그대로 재사용하되, 활성화 시 <b>자기 밴드에
/// 속한</b> 미체결 주문만 재수화한다. 따라서 매칭 후보도 자기 밴드로 한정되어 밴드 간 교차가
/// 구조적으로 불가능하다(밴드-격리).
/// </summary>
public sealed class OrderBandGrain(
    MarketRepository repo,
    IGrainFactory grains,
    MarketOptions options,
    ILogger<OrderBandGrain> log) : Grain, IOrderBandGrain
{
    private int _templateId;
    private long _band;
    private OrderBookEngine _engine = default!;

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        // 키 "{templateId}:{band}" 파싱.
        var key = this.GetPrimaryKeyString();
        var sep = key.IndexOf(':');
        _templateId = int.Parse(key[..sep]);
        _band = long.Parse(key[(sep + 1)..]);

        _engine = new OrderBookEngine(repo, grains, log, _templateId, DeactivateOnIdle);
        await _engine.RehydrateAsync(
            await repo.GetLiveOrdersInBandAsync(_templateId, options.PriceBandSize, _band));
        await base.OnActivateAsync(ct);
    }

    public Task<PlaceOrderResult> PlaceOrder(Guid playerId, PlaceOrderRequest req)
        => _engine.PlaceOrderAsync(playerId, req);

    public Task<OrderDto> CancelOrder(Guid playerId, Guid orderId, bool isAdmin)
        => _engine.CancelOrderAsync(playerId, orderId, isAdmin);

    public Task<OrderBookSnapshotDto> GetSnapshot()
        => Task.FromResult(_engine.GetSnapshot());
}

using ItemMarket.Contracts.Orders;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Serialization.Invocation;

namespace ItemMarket.Grains.Grains;

/// <summary>
/// 매칭 엔진 grain(키 = templateId). 두 모드로 동작한다:
///
/// <para><b>밴딩 OFF</b>(<c>Market:PriceBandSize == 0</c>, 기본): 종전과 동일하게 이 grain이
/// 템플릿 전체 호가창을 <see cref="OrderBookEngine"/>로 직접 매칭한다. Orleans의 단일 활성화 +
/// 논-리엔트런트 보장으로 템플릿별 매칭이 단일 스레드처럼 직렬화되어 매칭 레이스가 원천 차단된다.
/// 이 경로는 기존 동작을 바이트 단위로 보존한다.</para>
///
/// <para><b>밴딩 ON</b>(<c>PriceBandSize &gt; 0</c>): 이 grain은 매칭을 하지 않는 <b>코디네이터
/// (façade)</b>가 된다. 주문의 밴드 = <c>unitPrice / PriceBandSize</c>를 계산해 밴드별
/// <see cref="OrderBandGrain"/>("{templateId}:{band}")로 라우팅한다. 스냅샷은 밴드 grain들로
/// 팬아웃해 병합한다. 강제 전역 가격-시간 우선을 포기하는 대신(밴드-격리 매칭) 밴드 간 병렬성을
/// 얻는다 — 단일 핫 grain 상한을 돌파하기 위한 의도된 트레이드오프.</para>
///
/// <para><b>조건부 리엔트런시</b>: 코디네이터는 자기 상태를 변경하지 않고 라우팅만 하므로
/// 리엔트런트여도 안전하며, 리엔트런트여야만 여러 주문이 밴드 grain을 기다리는 동안 코디네이터가
/// 새 병목이 되지 않는다. 반면 OFF 모드는 매칭을 직접 하므로 반드시 논-리엔트런트여야 한다.
/// <see cref="MayInterleaveAttribute"/> + <see cref="AllowInterleaving"/>(기동 시 설정)로
/// 이를 분기한다: OFF면 술어가 false → 논-리엔트런트(기존과 동일), ON이면 true → 리엔트런트.</para>
/// </summary>
[MayInterleave(nameof(MayInterleave))]
public sealed class OrderBookGrain(
    MarketRepository repo,
    IGrainFactory grains,
    MarketOptions options,
    ILogger<OrderBookGrain> log) : Grain, IOrderBookGrain
{
    /// <summary>
    /// 코디네이터(밴딩 ON) 리엔트런시 스위치. 프로세스당 한 번, 기동 시 <c>Market:PriceBandSize</c>로
    /// 설정한다(<see cref="Program"/>). OFF면 false → 술어가 모든 요청을 인터리브 불가로 판정하여
    /// 사실상 논-리엔트런트(기존 매칭 정확성 보존). ON이면 true → 코디네이터가 리엔트런트가 되어
    /// 밴드 라우팅이 서로를 막지 않는다. (한 프로세스=한 설정이므로 정적 필드로 충분하다.)
    /// </summary>
    public static volatile bool AllowInterleaving;

    /// <summary>Orleans 리엔트런시 술어(정적). <see cref="AllowInterleaving"/>에 위임.</summary>
    public static bool MayInterleave(IInvokable req) => AllowInterleaving;

    private int TemplateId => (int)this.GetPrimaryKeyLong();

    // 밴딩 OFF 모드에서만 사용. ON 모드(코디네이터)에서는 null이며 상태를 갖지 않는다.
    private OrderBookEngine? _engine;

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!options.BandingEnabled)
        {
            _engine = new OrderBookEngine(repo, grains, log, TemplateId, DeactivateOnIdle);
            await _engine.RehydrateAsync(await repo.GetLiveOrdersAsync(TemplateId));
        }
        await base.OnActivateAsync(ct);
    }

    public Task<PlaceOrderResult> PlaceOrder(Guid playerId, PlaceOrderRequest req)
    {
        if (_engine is not null)
            return _engine.PlaceOrderAsync(playerId, req);

        // 코디네이터: 밴드를 계산해 해당 밴드 grain으로 라우팅(가격 상세 검증은 밴드 grain이 수행).
        if (req.ItemTemplateId != TemplateId)
            throw new DomainException(ErrorCode.ValidationError, "템플릿 ID가 주문서와 일치하지 않습니다.");
        var band = BandOf(req.UnitPrice);
        return grains.GetGrain<IOrderBandGrain>(BandKey(TemplateId, band)).PlaceOrder(playerId, req);
    }

    public async Task<OrderDto> CancelOrder(Guid playerId, Guid orderId, bool isAdmin)
    {
        if (_engine is not null)
            return await _engine.CancelOrderAsync(playerId, orderId, isAdmin);

        // 코디네이터: 주문의 가격으로 소유 밴드를 찾아 라우팅. 소유·상태 검증은 밴드 grain이 수행.
        var order = await repo.GetOrderAsync(orderId)
            ?? throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다.");
        if (order.TemplateId != TemplateId)
            throw new DomainException(ErrorCode.OrderNotFound, "이 템플릿의 주문이 아닙니다.");
        var band = BandOf(order.UnitPrice);
        return await grains.GetGrain<IOrderBandGrain>(BandKey(TemplateId, band)).CancelOrder(playerId, orderId, isAdmin);
    }

    public async Task<OrderBookSnapshotDto> GetSnapshot()
    {
        if (_engine is not null)
            return _engine.GetSnapshot();

        // 코디네이터: 미체결 주문이 있는 밴드들을 DB에서 찾아 각 밴드 grain의 스냅샷을 병합.
        // 밴드는 서로 겹치지 않는 가격 구간이라 가격대(level) 중복 없이 단순 병합으로 충분하다.
        var bands = await repo.GetLiveBandsAsync(TemplateId, options.PriceBandSize);
        var snaps = await Task.WhenAll(
            bands.Select(b => grains.GetGrain<IOrderBandGrain>(BandKey(TemplateId, b)).GetSnapshot()));

        var bids = snaps.SelectMany(s => s.Bids).OrderByDescending(l => l.UnitPrice).ToList();
        var asks = snaps.SelectMany(s => s.Asks).OrderBy(l => l.UnitPrice).ToList();
        return new OrderBookSnapshotDto(TemplateId, bids, asks);
    }

    private long BandOf(long unitPrice) => unitPrice / options.PriceBandSize;

    /// <summary>밴드 grain 키 규칙: "{templateId}:{band}".</summary>
    internal static string BandKey(int templateId, long band) => $"{templateId}:{band}";
}

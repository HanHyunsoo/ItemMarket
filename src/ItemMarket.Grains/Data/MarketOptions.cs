namespace ItemMarket.Grains.Data;

/// <summary>
/// 매칭 엔진 런타임 옵션(설정 <c>Market:*</c>에서 바인딩). 싱글턴으로 DI에 등록되어
/// 코디네이터/밴드 grain이 주입받는다.
///
/// <para><b>PriceBandSize</b> — 가격 밴드 샤딩 스위치.
/// 0(기본)이면 샤딩 <b>비활성</b>: <see cref="Grains.OrderBookGrain"/>이 종전과 동일하게
/// 템플릿당 단일 호가창을 직접 매칭한다(바이트 단위로 기존 동작 보존).
/// 0보다 크면 주문의 밴드 = <c>unitPrice / PriceBandSize</c>로 계산되어, 코디네이터가
/// 밴드별 <see cref="Grains.OrderBandGrain"/>으로 라우팅한다. 밴드 경계를 넘는 매칭은
/// 일어나지 않는다(밴드-격리 매칭 — 병렬성과 맞바꾼 의도된 트레이드오프).</para>
/// </summary>
public sealed record MarketOptions(int PriceBandSize)
{
    /// <summary>가격 밴드 샤딩 활성 여부.</summary>
    public bool BandingEnabled => PriceBandSize > 0;
}

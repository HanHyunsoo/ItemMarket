namespace ItemMarket.Contracts.Trades;

/// <summary>
/// 체결(trade) 기록. 매수/매도 주문이 매칭될 때마다 생성되는 불변 원장.
/// FeeAmount는 판매자 대금에서 차감되어 소각(sink)되는 병뚜껑 수량.
/// InstanceId는 유니크 무기 거래 시 실제 넘어간 인스턴스.
/// </summary>
public sealed record TradeDto(
    Guid Id,
    int ItemTemplateId,
    long UnitPrice,
    int Quantity,
    Guid BuyerId,
    Guid SellerId,
    Guid BuyOrderId,
    Guid SellOrderId,
    Guid? InstanceId,
    long FeeAmount,
    DateTimeOffset ExecutedAt);

/// <summary>
/// 종목 하나의 시세 요약(마켓 카드 목록용). 최우선 매수/매도 호가·최근 체결가·활동 신호를 담는다.
/// 활성 주문/체결이 없는 종목은 BestBid/BestAsk/LastPrice/LastTradeAt이 null, OpenOrders=0("시장 없음").
/// </summary>
public sealed record MarketTickerDto(
    int TemplateId,
    long? BestBid,
    long? BestAsk,
    long? LastPrice,
    DateTimeOffset? LastTradeAt,
    int OpenOrders);

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

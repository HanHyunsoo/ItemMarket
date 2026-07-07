namespace ItemMarket.Contracts.Wallet;

/// <summary>플레이어 지갑. 단일 재화 '병뚜껑(CAP)', 정수(bigint) 잔액.</summary>
public sealed record WalletDto(Guid PlayerId, long Balance);

/// <summary>지갑 변동 사유. 감사(audit)/RMT 탐지용 원장 태그.</summary>
public enum WalletLedgerReason
{
    OrderEscrow,    // 매수 주문 등록 시 대금 잠금(-)
    OrderRefund,    // 주문 취소/체결가 차익 환불(+)
    TradePayment,   // 매수 체결 대금 지불(-)  ※에스크로에서 정산
    TradeProceeds,  // 매도 체결 대금 수령(+, 수수료 차감 후)
    Fee,            // 거래 수수료 소각(-)
    AdminAdjust,    // 운영 수동 조정(±)
    StashUpgrade,   // 스태시 행 확장 구매(-) — 캡 싱크
    RaidEntryFee,   // 출격 수수료(-) — 존별 캡 싱크
    VendorSell      // NPC 벤더 매입 대금(+) — 캡 faucet
}

/// <summary>지갑 원장 한 줄(append-only). 모든 병뚜껑 이동을 추적.</summary>
public sealed record WalletLedgerEntryDto(
    long Id,
    Guid PlayerId,
    long Delta,
    long BalanceAfter,
    WalletLedgerReason Reason,
    Guid? RefId,
    DateTimeOffset CreatedAt);

using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Wallet;

namespace ItemMarket.Grains.Abstractions;

/// <summary>플레이어 지갑(키 = playerId). 모든 잔액 변동은 wallet_ledger에 기록된다.</summary>
public interface IWalletGrain : IGrainWithGuidKey
{
    Task<WalletDto> Get();

    /// <summary>매수 에스크로: 대금 잠금. 잔액 부족이면 false.</summary>
    Task<bool> TryEscrow(long amount, Guid orderId);

    /// <summary>취소 등으로 병뚜껑 환불(+).</summary>
    Task Refund(long amount, Guid refId);

    /// <summary>운영 수동 가감(±). ADMIN_ADJUST 원장.</summary>
    Task<WalletDto> AdminAdjust(long delta, string reason);

    Task<PagedResult<WalletLedgerEntryDto>> GetLedger(int page, int size);
}

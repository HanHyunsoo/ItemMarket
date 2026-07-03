using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Wallet;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;

namespace ItemMarket.Grains.Grains;

/// <summary>
/// 지갑 grain. 상태를 메모리에 캐시하지 않고 Postgres(소스오브트루스)를 통해
/// 읽고 쓴다. 단일 활성화라 같은 플레이어 지갑에 대한 요청은 직렬화된다.
/// </summary>
public sealed class WalletGrain(MarketRepository repo) : Grain, IWalletGrain
{
    private Guid PlayerId => this.GetPrimaryKey();

    public Task<WalletDto> Get() => repo.GetWalletAsync(PlayerId);

    public Task<bool> TryEscrow(long amount, Guid orderId) => repo.TryEscrowCapsAsync(PlayerId, amount, orderId);

    public Task Refund(long amount, Guid refId) => repo.RefundCapsAsync(PlayerId, amount, refId);

    public Task<WalletDto> AdminAdjust(long delta, string reason) => repo.AdminAdjustAsync(PlayerId, delta, reason);

    public Task<PagedResult<WalletLedgerEntryDto>> GetLedger(int page, int size)
        => repo.GetLedgerAsync(PlayerId, Math.Max(1, page), Math.Clamp(size, 1, 200));
}

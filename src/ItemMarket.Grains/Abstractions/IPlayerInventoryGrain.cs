using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
using ItemMarket.Grains.Data;

namespace ItemMarket.Grains.Abstractions;

/// <summary>플레이어 인벤토리(키 = playerId). 스택형 수량 + 유니크 인스턴스.</summary>
public interface IPlayerInventoryGrain : IGrainWithGuidKey
{
    Task<InventoryDto> Get();

    /// <summary>아이템 원장(item_ledger) 페이지네이션 조회(RAID_*/ADMIN_GRANT 이동 로그).</summary>
    Task<PagedResult<ItemLedgerEntryDto>> GetLedger(int page, int size);

    /// <summary>스택형 매도 에스크로: 수량 차감. 부족하면 false.</summary>
    Task<bool> TryEscrowStack(int templateId, int quantity);

    /// <summary>스택형 매도 취소: 수량 반환.</summary>
    Task ReturnStack(int templateId, int quantity);

    /// <summary>유니크 매도 에스크로: 소유권을 에스크로(owner=NULL)로.</summary>
    Task<EscrowInstanceOutcome> TryEscrowInstance(Guid instanceId, int templateId);

    /// <summary>유니크 매도 취소: 인스턴스 소유권 원복.</summary>
    Task ReturnInstance(Guid instanceId);

    Task<InventoryDto> AdminGrantStack(int templateId, int quantity);
    Task<ItemInstanceDto> AdminGrantInstance(int templateId, int? durability, IReadOnlyList<string>? attachments);
}

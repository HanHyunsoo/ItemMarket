using ItemMarket.Contracts.Items;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;

namespace ItemMarket.Grains.Grains;

/// <summary>
/// 인벤토리 grain. 스택형 수량과 유니크 인스턴스 소유권을 Postgres 기준으로 관리.
/// 매도 에스크로/취소 반환을 제공한다.
/// </summary>
public sealed class PlayerInventoryGrain(MarketRepository repo) : Grain, IPlayerInventoryGrain
{
    private Guid PlayerId => this.GetPrimaryKey();

    public Task<InventoryDto> Get() => repo.GetInventoryAsync(PlayerId);

    public Task<bool> TryEscrowStack(int templateId, int quantity) => repo.TryEscrowStackAsync(PlayerId, templateId, quantity);

    public Task ReturnStack(int templateId, int quantity) => repo.ReturnStackAsync(PlayerId, templateId, quantity);

    public Task<EscrowInstanceOutcome> TryEscrowInstance(Guid instanceId, int templateId)
        => repo.TryEscrowInstanceAsync(PlayerId, instanceId, templateId);

    public Task ReturnInstance(Guid instanceId) => repo.ReturnInstanceAsync(PlayerId, instanceId);

    public Task<InventoryDto> AdminGrantStack(int templateId, int quantity) => repo.AdminGrantStackAsync(PlayerId, templateId, quantity);

    public Task<ItemInstanceDto> AdminGrantInstance(int templateId, int? durability, IReadOnlyList<string>? attachments)
        => repo.AdminGrantInstanceAsync(PlayerId, templateId, durability, attachments);
}

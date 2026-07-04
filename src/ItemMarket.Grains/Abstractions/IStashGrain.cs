using ItemMarket.Contracts.Stash;

namespace ItemMarket.Grains.Abstractions;

/// <summary>플레이어 스태시(키 = playerId). 고정 그리드 위 아이템 배치 + 서버 권위 이동 검증.</summary>
public interface IStashGrain : IGrainWithGuidKey
{
    /// <summary>현재 배치 스냅샷. 미배치 소유 아이템은 first-fit으로 자동 배치·영속화한다.</summary>
    Task<StashDto> GetStash();

    /// <summary>아이템 이동. 소유권/경계/겹침 검증 후 영속화. 위반 시 PlacementInvalid.</summary>
    Task<StashDto> MoveItem(MoveStashItemRequest req);
}

using ItemMarket.Contracts.Stash;

namespace ItemMarket.Grains.Abstractions;

/// <summary>
/// 플레이어 스태시(키 = playerId). 컨테이너(STASH/LOADOUT) 인지 그리드 배치 + 서버 권위 이동 검증.
/// grain은 플레이어당 단일 활성화라 한 플레이어의 모든 컨테이너 조작이 직렬화된다
/// (컨테이너 간 이동도 하나의 활성화 안에서 검증·영속화되므로 원자적).
/// </summary>
public interface IStashGrain : IGrainWithGuidKey
{
    /// <summary>지정 컨테이너의 배치 스냅샷. 미배치 소유 아이템은 first-fit으로 STASH에 자동 배치·영속화한다.</summary>
    Task<StashDto> GetStash(GridContainer container);

    /// <summary>
    /// 아이템 이동. FromContainer==ToContainer면 같은 컨테이너 재배치,
    /// 다르면 컨테이너 간 이동(반입/반출). 소유권/경계/겹침 검증 후 영속화. 위반 시 PlacementInvalid.
    /// </summary>
    Task<StashDto> MoveItem(MoveStashItemRequest req);
}

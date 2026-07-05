using ItemMarket.Contracts.Equipment;
using ItemMarket.Contracts.Stash;

namespace ItemMarket.Grains.Abstractions;

/// <summary>
/// 플레이어 스태시(키 = playerId). 컨테이너(STASH/POCKETS/장착된 백팩·리그의 중첩 그리드) 인지
/// 그리드 배치 + 서버 권위 이동 검증 + 장비(equipment) 슬롯 조작. grain은 플레이어당 단일 활성화라
/// 한 플레이어의 모든 컨테이너/장비 조작이 직렬화된다(컨테이너 간 이동·장착/해제도 하나의 활성화
/// 안에서 검증·영속화되므로 원자적이며 정합화(reconcile)와 경합하지 않는다).
/// </summary>
public interface IStashGrain : IGrainWithGuidKey
{
    /// <summary>지정 컨테이너의 배치 스냅샷. 미배치 소유 아이템은 first-fit으로 STASH에 자동 배치·영속화한다.</summary>
    Task<StashDto> GetStash(GridContainer container);

    /// <summary>장착된 백팩/리그의 내부(중첩) 그리드 스냅샷. 장착된 컨테이너가 아니면 PlacementInvalid.</summary>
    Task<StashDto> GetContainer(Guid containerInstanceId);

    /// <summary>
    /// 아이템 이동. FromContainer==ToContainer면 같은 컨테이너 재배치,
    /// 다르면 컨테이너 간 이동(반입/반출). 소유권/경계/겹침 검증 후 영속화. 위반 시 PlacementInvalid.
    /// </summary>
    Task<StashDto> MoveItem(MoveStashItemRequest req);

    /// <summary>장비 전체 스냅샷: 슬롯→인스턴스 매핑 + 장착된 백팩/리그의 중첩 그리드 목록.</summary>
    Task<EquipmentDto> GetEquipment();

    /// <summary>소유 인스턴스를 호환 슬롯에 장착(template.equip_slot == slot 검증). 슬롯 점유/불일치 시 SlotMismatch.</summary>
    Task<EquipmentDto> Equip(EquipRequest req);

    /// <summary>슬롯을 비운다. 해제된 아이템(및 백팩/리그 내용물)은 소유로 남아 다음 조회에서 STASH로 회수된다.</summary>
    Task<EquipmentDto> Unequip(UnequipRequest req);
}

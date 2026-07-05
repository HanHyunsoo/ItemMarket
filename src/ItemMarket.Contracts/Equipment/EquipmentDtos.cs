using ItemMarket.Contracts.Stash;

namespace ItemMarket.Contracts.Equipment;

/// <summary>
/// 장착 슬롯. 인스턴스는 template.equip_slot과 일치하는 슬롯에만 장착 가능(서버 권위 검증).
///   Helmet/Armor/Weapon = 단일 아이템 슬롯. Backpack/Rig = 단일 아이템이면서 내부 그리드(중첩 컨테이너) 제공.
/// </summary>
public enum EquipSlot
{
    Helmet,
    Armor,
    Weapon,
    Backpack,
    Rig
}

/// <summary>한 슬롯에 장착된 인스턴스.</summary>
public sealed record EquippedItemDto(EquipSlot Slot, Guid InstanceId, int TemplateId);

/// <summary>
/// 장착된 백팩/리그가 제공하는 중첩 컨테이너 그리드 스냅샷.
/// ContainerInstanceId로 주소화하며(이동 요청의 ToContainerInstanceId), GridW×GridH는 그 인스턴스의
/// 내부 그리드 크기(template.container_w×container_h). Placements는 그 그리드에 놓인 배치.
/// </summary>
public sealed record NestedContainerDto(
    Guid ContainerInstanceId,
    int TemplateId,
    EquipSlot Slot,
    int GridW,
    int GridH,
    IReadOnlyList<StashPlacementDto> Placements);

/// <summary>
/// 플레이어 장비 전체 스냅샷: 슬롯→인스턴스 매핑 + 장착된 백팩/리그의 중첩 그리드 목록.
/// </summary>
public sealed record EquipmentDto(
    Guid PlayerId,
    IReadOnlyList<EquippedItemDto> Slots,
    IReadOnlyList<NestedContainerDto> Containers);

/// <summary>장착 요청: 소유 인스턴스를 호환 슬롯에 장착. 슬롯이 이미 차 있으면 거부(먼저 해제).</summary>
public sealed record EquipRequest(EquipSlot Slot, Guid InstanceId);

/// <summary>해제 요청: 슬롯을 비운다. 해제된 아이템(및 백팩/리그 내용물)은 STASH로 회수된다.</summary>
public sealed record UnequipRequest(EquipSlot Slot);

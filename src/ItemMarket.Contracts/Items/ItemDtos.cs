namespace ItemMarket.Contracts.Items;

/// <summary>아이템 카테고리. FOOD/MEDICAL/AMMO=스택형, MELEE/GUN/GEAR=유니크 인스턴스.
/// GEAR=장비(헬멧/방어구/백팩/리그).</summary>
public enum ItemCategory
{
    Food,
    Medical,
    Melee,
    Gun,
    Ammo,
    Gear
}

/// <summary>희귀도. 아포칼립스 세계관상 총(GUN)은 대체로 Rare 이상.</summary>
public enum ItemRarity
{
    Common,
    Uncommon,
    Rare,
    Epic,
    Legendary
}

/// <summary>
/// 아이템 마스터(카탈로그). 시드 데이터로 고정. Stackable=true면 수량 기반,
/// false면 개별 인스턴스(내구도·부착물) 기반. Icon은 픽셀 스프라이트 키.
/// </summary>
public sealed record ItemTemplateDto(
    int Id,
    string Code,
    string Name,
    ItemCategory Category,
    ItemRarity Rarity,
    bool Stackable,
    int? MaxDurability,
    string Icon,
    long BaseValue,
    int GridW = 1,
    int GridH = 1,
    Equipment.EquipSlot? EquipSlot = null,
    bool IsContainer = false,
    int? ContainerW = null,
    int? ContainerH = null);

/// <summary>유니크 인스턴스(무기/방어구). 내구도·부착물 보유.</summary>
public sealed record ItemInstanceDto(
    Guid Id,
    int TemplateId,
    int? Durability,
    IReadOnlyList<string> Attachments,
    DateTimeOffset AcquiredAt);

/// <summary>스택형 인벤토리 한 줄: (플레이어, 템플릿) 당 수량.</summary>
public sealed record InventoryStackDto(int TemplateId, int Quantity);

/// <summary>플레이어 인벤토리 전체: 스택형 + 유니크 인스턴스.</summary>
public sealed record InventoryDto(
    Guid PlayerId,
    IReadOnlyList<InventoryStackDto> Stacks,
    IReadOnlyList<ItemInstanceDto> Instances);

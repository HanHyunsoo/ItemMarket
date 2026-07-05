using ItemMarket.Contracts.Equipment;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Raid;
using ItemMarket.Contracts.Stash;

namespace ItemMarket.Grains.Data;

/// <summary>플레이어 마스터 한 줄. StashRows=스태시 세로 칸 수(가로는 StashGeometry.StashWidth로 고정).</summary>
public sealed record PlayerRow(Guid Id, string DisplayName, int StashRows);

/// <summary>스태시 배치 한 줄(stash_placement 원본). 유니크는 InstanceId 사용.
/// Container=놓인 컨테이너, Quantity=스택형이 이 컨테이너에 가진 수량(유니크는 1).</summary>
public sealed record StashPlacementRow(
    GridContainer Container,
    StashEntryKind Kind,
    int TemplateId,
    Guid? InstanceId,
    int X,
    int Y,
    int Quantity,
    Guid? ContainerInstanceId = null);

/// <summary>장착 슬롯 한 줄(player_equipment). 슬롯 → 인스턴스(+템플릿).</summary>
public sealed record EquipmentSlotRow(EquipSlot Slot, Guid InstanceId, int TemplateId);

/// <summary>매칭에 필요한 아이템 템플릿 속성.</summary>
public sealed record TemplateRow(int Id, bool Stackable);

/// <summary>주문 한 줄(원본 컬럼 그대로). 매칭 엔진 재수화·취소에 사용.</summary>
public sealed record OrderRow(
    Guid Id,
    Guid PlayerId,
    OrderSide Side,
    int TemplateId,
    long UnitPrice,
    int Quantity,
    int RemainingQuantity,
    Guid? InstanceId,
    OrderStatus Status,
    long EscrowCaps,
    DateTimeOffset CreatedAt)
{
    public OrderDto ToDto() => new(
        Id, PlayerId, Side, TemplateId, UnitPrice, Quantity,
        RemainingQuantity, Status, InstanceId, CreatedAt);
}

/// <summary>유니크 인스턴스 에스크로 시도 결과.</summary>
public enum EscrowInstanceOutcome { Ok, NotFound, NotOwned, TemplateMismatch }

/// <summary>레이드 세션 한 줄(raid_session 원본, 아이템 목록 제외).</summary>
public sealed record RaidSessionRow(
    Guid Id,
    Guid PlayerId,
    RaidStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>리프레시 토큰 행(해시 컬럼은 제외 — 조회는 해시로만 한다).</summary>
public sealed record RefreshTokenRow(Guid Id, Guid PlayerId, DateTime ExpiresAt, bool Revoked);

/// <summary>단일 체결(fill)을 원자적으로 정산하기 위한 입력 묶음.</summary>
public sealed record SettleFillArgs(
    Guid TradeId,
    int TemplateId,
    Guid BuyOrderId,
    Guid SellOrderId,
    Guid BuyerId,
    Guid SellerId,
    long ExecPrice,
    int Quantity,
    long BuyLimitPrice,
    int FeeBps,
    Guid? InstanceId,
    bool Stackable,
    int BuyRemaining,
    OrderStatus BuyStatus,
    int SellRemaining,
    OrderStatus SellStatus,
    DateTimeOffset ExecutedAt);

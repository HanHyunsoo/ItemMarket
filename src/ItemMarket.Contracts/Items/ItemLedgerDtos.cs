using ItemMarket.Contracts.Stash;

namespace ItemMarket.Contracts.Items;

/// <summary>
/// 아이템 원장(item_ledger) 태그. wallet_ledger의 WalletLedgerReason에 대응하는
/// 아이템 이동 프로버넌스 사유. DB에는 SNAKE_CASE 텍스트로 저장한다.
/// </summary>
public enum ItemLedgerReason
{
    RaidBrought,   // 로드아웃 → 레이드 에스크로(반입, -)
    RaidExtract,   // 반입 아이템 생존 회수(+)
    RaidLoot,      // 전리품 materialize(+)
    RaidLoss,      // 위험 아이템 소실(사망, -)
    AdminGrant,    // 운영 지급(+)
    VendorSell     // NPC 벤더 매입으로 소진(-)
}

/// <summary>
/// 아이템 원장 한 줄(append-only). 소유 인벤토리 기준 아이템 이동 로그(잔고 컬럼 없음).
/// DeltaQty 부호: 반입/소실 = -, 회수/획득/지급 = +. RefId는 관련 raid_session id 등.
/// </summary>
public sealed record ItemLedgerEntryDto(
    long Id,
    Guid PlayerId,
    StashEntryKind Kind,
    int TemplateId,
    Guid? InstanceId,
    int DeltaQty,
    ItemLedgerReason Reason,
    Guid? RefId,
    DateTimeOffset CreatedAt);

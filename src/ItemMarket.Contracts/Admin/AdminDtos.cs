namespace ItemMarket.Contracts.Admin;

/// <summary>운영 툴: 지갑 병뚜껑 수동 가감(+/-). Reason은 원장에 기록.</summary>
public sealed record AdminAdjustWalletRequest(Guid PlayerId, long Delta, string Reason);

/// <summary>운영 툴: 스택형 아이템(먹을거/힐템/탄약) 수량 지급.</summary>
public sealed record AdminGrantStackRequest(Guid PlayerId, int TemplateId, int Quantity);

/// <summary>운영 툴: 유니크 아이템(근접무기/총) 인스턴스 지급.</summary>
public sealed record AdminGrantInstanceRequest(
    Guid PlayerId,
    int TemplateId,
    int? Durability,
    IReadOnlyList<string>? Attachments);

/// <summary>운영 툴: 주문 강제 취소(에스크로 자동 환불). 이상 거래 개입용.</summary>
public sealed record AdminForceCancelOrderRequest(Guid OrderId, string Reason);

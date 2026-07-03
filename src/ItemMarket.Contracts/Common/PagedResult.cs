namespace ItemMarket.Contracts.Common;

/// <summary>거래 내역/원장/주문 목록 등 페이지네이션 응답.</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount);

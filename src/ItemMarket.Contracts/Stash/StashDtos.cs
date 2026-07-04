namespace ItemMarket.Contracts.Stash;

/// <summary>스태시 배치 대상 종류. 스택형 더미(템플릿당 1칸) 또는 유니크 인스턴스.</summary>
public enum StashEntryKind
{
    Stack,
    Instance
}

/// <summary>
/// 스태시 그리드 위 배치 하나. (X,Y)=좌상단 칸, W×H=템플릿의 footprint.
/// Stack이면 TemplateId+Quantity, Instance면 InstanceId(+TemplateId) 사용.
/// </summary>
public sealed record StashPlacementDto(
    StashEntryKind Kind,
    int TemplateId,
    Guid? InstanceId,
    int X,
    int Y,
    int W,
    int H,
    int Quantity);

/// <summary>
/// 플레이어 스태시 전체 뷰. 서버가 미배치 아이템을 first-fit으로 자동 배치한 뒤의
/// 스냅샷이며, 그리드가 가득 차 배치 불가한 항목은 Unplaced(대기 트레이)로 내려간다.
/// </summary>
public sealed record StashDto(
    Guid PlayerId,
    int GridW,
    int GridH,
    IReadOnlyList<StashPlacementDto> Placements,
    IReadOnlyList<StashPlacementDto> Unplaced);

/// <summary>
/// 아이템 이동 요청. 서버 권위 검증: 소유권 + 경계(그리드 밖 불가) + 충돌(겹침 불가).
/// Stack 이동은 TemplateId로, Instance 이동은 InstanceId로 대상을 지정한다.
/// </summary>
public sealed record MoveStashItemRequest(
    StashEntryKind Kind,
    int? TemplateId,
    Guid? InstanceId,
    int X,
    int Y);

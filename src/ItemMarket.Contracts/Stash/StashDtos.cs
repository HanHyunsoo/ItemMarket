namespace ItemMarket.Contracts.Stash;

/// <summary>스태시 배치 대상 종류. 스택형 더미(템플릿당 1칸) 또는 유니크 인스턴스.</summary>
public enum StashEntryKind
{
    Stack,
    Instance
}

/// <summary>
/// 그리드 컨테이너 종류. 아이템은 정확히 한 컨테이너 안에 놓인다.
///   Stash   = 안전 보관소(10×12). 소유 아이템은 기본적으로 여기에 자동 배치된다.
///   Loadout = 레이드에 들고 나가는 칸(6×8). 이동(반입/반출)으로만 채워진다.
/// </summary>
public enum GridContainer
{
    Stash,
    Loadout
}

/// <summary>
/// 스태시 그리드 위 배치 하나. (X,Y)=좌상단 칸, W×H=템플릿의 footprint.
/// Stack이면 TemplateId+Quantity, Instance면 InstanceId(+TemplateId) 사용.
/// Container는 이 배치가 속한 컨테이너(Stash/Loadout).
/// </summary>
public sealed record StashPlacementDto(
    GridContainer Container,
    StashEntryKind Kind,
    int TemplateId,
    Guid? InstanceId,
    int X,
    int Y,
    int W,
    int H,
    int Quantity);

/// <summary>
/// 한 컨테이너의 전체 뷰. 서버가 미배치 아이템을 first-fit으로 STASH에 자동 배치한 뒤의
/// 스냅샷이며, 그리드가 가득 차 배치 불가한 항목은 Unplaced(대기 트레이)로 내려간다.
/// Container는 이 스냅샷이 어느 컨테이너인지, GridW/GridH는 그 컨테이너의 크기.
/// </summary>
public sealed record StashDto(
    Guid PlayerId,
    GridContainer Container,
    int GridW,
    int GridH,
    IReadOnlyList<StashPlacementDto> Placements,
    IReadOnlyList<StashPlacementDto> Unplaced);

/// <summary>
/// 아이템 이동 요청. 서버 권위 검증: 소유권 + 경계(대상 컨테이너 밖 불가) + 충돌(겹침 불가).
/// Stack 이동은 TemplateId로, Instance 이동은 InstanceId로 대상을 지정한다.
///
/// FromContainer==ToContainer면 같은 컨테이너 안 재배치, 다르면 컨테이너 간 이동(반입/반출).
/// Quantity는 스택의 컨테이너 간 부분 이동 수량(미지정 시 원본 컨테이너의 전체 수량).
/// 유니크 인스턴스는 항상 통째로 이동하며 Quantity는 무시된다.
/// 기본값(Stash)으로 두면 기존 단일-스태시 이동 계약과 호환된다.
/// </summary>
public sealed record MoveStashItemRequest(
    StashEntryKind Kind,
    int? TemplateId,
    Guid? InstanceId,
    int X,
    int Y,
    GridContainer FromContainer = GridContainer.Stash,
    GridContainer ToContainer = GridContainer.Stash,
    int? Quantity = null);

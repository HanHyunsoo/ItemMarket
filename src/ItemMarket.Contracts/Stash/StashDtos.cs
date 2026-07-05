namespace ItemMarket.Contracts.Stash;

/// <summary>스태시 배치 대상 종류. 스택형 더미(템플릿당 1칸) 또는 유니크 인스턴스.</summary>
public enum StashEntryKind
{
    Stack,
    Instance
}

/// <summary>
/// 그리드 컨테이너 종류. 아이템은 정확히 한 컨테이너 안에 놓인다.
///   Stash     = 안전 보관소(가로 12 고정 · 세로 가변 = player.stash_rows). 레이드 중에도 안전(at-risk 아님).
///               소유 아이템 중 어디에도 배치되지 않은 것은 기본적으로 여기에 자동 배치된다.
///   Pockets   = 캐릭터 내재 주머니(1×4, 소형). 착용 장비처럼 레이드에 함께 반입되어 at-risk가 된다.
///   Container = 장착된 백팩/리그의 내부 그리드(중첩 컨테이너). 특정 컨테이너 인스턴스를
///               가리키므로 이동 요청에 ContainerInstanceId가 함께 필요하다(크기는 그 인스턴스의 template).
/// </summary>
public enum GridContainer
{
    Stash,
    Pockets,
    Container
}

/// <summary>
/// 스태시 그리드 위 배치 하나. (X,Y)=좌상단 칸, W×H=템플릿의 footprint.
/// Stack이면 TemplateId+Quantity, Instance면 InstanceId(+TemplateId) 사용.
/// Container는 이 배치가 속한 컨테이너(Stash/Pockets/Container).
/// 같은 스택 템플릿이 여러 칸·여러 컨테이너에 나뉘어 존재할 수 있다(다중 스택, 각 스택은 max_stack 상한).
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
    int Quantity,
    Guid? ContainerInstanceId = null);

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
/// FromContainer==ToContainer면 같은 컨테이너 안 재배치, 다르면 컨테이너 간 이동.
/// Quantity는 스택의 부분 이동 수량(미지정 시 원본 스택의 전체 수량) → 스택 분할에 사용.
/// 대상 칸에 같은 템플릿 스택이 있으면 max_stack 상한까지 병합되고 초과분은 원본에 남는다.
/// 유니크 인스턴스는 항상 통째로 이동하며 Quantity는 무시된다.
/// 기본값(Stash)으로 두면 기존 단일-스태시 이동 계약과 호환된다.
///
/// 중첩 컨테이너(백팩/리그 내부 그리드) 이동: From/ToContainer=Container로 두고,
/// From/ToContainerInstanceId에 그 컨테이너(장착된 백팩/리그) 인스턴스 id를 지정한다.
/// 주머니 이동: From/ToContainer=Pockets(ContainerInstanceId 불필요, 내재 컨테이너).
/// </summary>
public sealed record MoveStashItemRequest(
    StashEntryKind Kind,
    int? TemplateId,
    Guid? InstanceId,
    int X,
    int Y,
    GridContainer FromContainer = GridContainer.Stash,
    GridContainer ToContainer = GridContainer.Stash,
    int? Quantity = null,
    Guid? FromContainerInstanceId = null,
    Guid? ToContainerInstanceId = null);

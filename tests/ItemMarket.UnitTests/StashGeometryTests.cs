using ItemMarket.Contracts.Stash;
using ItemMarket.Grains.Data;
using Xunit;

namespace ItemMarket.UnitTests;

/// <summary>
/// StashGeometry 순수 기하 로직(DB/Orleans 무관). 경계·겹침·first-fit을 핀 고정.
/// STASH는 가로 12 고정 · 세로는 player.stash_rows(가변) — 여기서는 대표값 12를 stashRows로 써서
/// 기존 10×12 스위트와 유사한 규모로 검증한다. POCKETS는 항상 고정 4×1이다.
/// </summary>
public class StashGeometryTests
{
    private const GridContainer Stash = GridContainer.Stash;
    private const int StashRows = 12; // 이 테스트 스위트에서 쓰는 대표 세로 칸 수(가변값의 한 예).

    // ---- Dims --------------------------------------------------------------

    [Fact]
    public void Dims_returns_per_container_size()
    {
        Assert.Equal((12, StashRows), StashGeometry.Dims(GridContainer.Stash, StashRows));
        Assert.Equal((4, 1), StashGeometry.Dims(GridContainer.Pockets, StashRows));
    }

    [Fact]
    public void Dims_stash_height_follows_stashRows_argument()
    {
        // STASH 가로는 항상 12 고정, 세로만 player.stash_rows를 그대로 반영(가변 지원).
        Assert.Equal((12, 60), StashGeometry.Dims(GridContainer.Stash, 60));
        Assert.Equal((12, 5), StashGeometry.Dims(GridContainer.Stash, 5));
    }

    // ---- InBounds ----------------------------------------------------------

    [Fact]
    public void InBounds_accepts_top_left_1x1()
        => Assert.True(StashGeometry.InBounds(Stash, new Rect(0, 0, 1, 1), StashRows));

    [Fact]
    public void InBounds_accepts_footprint_flush_to_bottom_right()
        // x+w=12, y+h=12 로 경계에 정확히 맞닿는 배치는 유효.
        => Assert.True(StashGeometry.InBounds(Stash, new Rect(8, 10, 4, 2), StashRows));

    [Fact]
    public void InBounds_respects_smaller_pockets_grid()
    {
        // POCKETS은 4×1 — STASH에서 유효한 (8,10,4,2)도 POCKETS에선 경계 밖(높이 1 초과).
        Assert.False(StashGeometry.InBounds(GridContainer.Pockets, new Rect(0, 0, 1, 2), StashRows));
        // 1×1 스택은 POCKETS 우측 끝 (3,0)에 딱 맞는다: x+w=4, y+h=1.
        Assert.True(StashGeometry.InBounds(GridContainer.Pockets, new Rect(3, 0, 1, 1), StashRows));
        // x=4이면 x+w=5 > 4 → 경계 밖.
        Assert.False(StashGeometry.InBounds(GridContainer.Pockets, new Rect(4, 0, 1, 1), StashRows));
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]   // x 음수
    [InlineData(0, -1, 1, 1)]   // y 음수
    [InlineData(12, 0, 1, 1)]   // x+w=13 > 12
    [InlineData(0, 12, 1, 1)]   // y+h=13 > 12
    [InlineData(9, 0, 4, 2)]    // x+w=13 > 12 (4폭 무기)
    [InlineData(0, 11, 1, 2)]   // y+h=13 > 12
    [InlineData(0, 0, 0, 1)]    // w<1
    [InlineData(0, 0, 1, 0)]    // h<1
    public void InBounds_rejects_out_of_grid(int x, int y, int w, int h)
        => Assert.False(StashGeometry.InBounds(Stash, new Rect(x, y, w, h), StashRows));

    // ---- Overlaps ----------------------------------------------------------

    [Fact]
    public void Overlaps_true_when_footprints_share_a_cell()
        => Assert.True(StashGeometry.Overlaps(new Rect(0, 0, 2, 2), new Rect(1, 1, 2, 2)));

    [Fact]
    public void Overlaps_true_when_identical()
        => Assert.True(StashGeometry.Overlaps(new Rect(3, 3, 2, 2), new Rect(3, 3, 2, 2)));

    [Fact]
    public void Overlaps_false_when_edge_adjacent_horizontally()
        // a는 x[0,2), b는 x[2,4) — 맞닿기만 하고 겹치지 않음.
        => Assert.False(StashGeometry.Overlaps(new Rect(0, 0, 2, 2), new Rect(2, 0, 2, 2)));

    [Fact]
    public void Overlaps_false_when_edge_adjacent_vertically()
        => Assert.False(StashGeometry.Overlaps(new Rect(0, 0, 2, 2), new Rect(0, 2, 2, 2)));

    [Fact]
    public void Overlaps_false_when_diagonally_touching_corners()
        => Assert.False(StashGeometry.Overlaps(new Rect(0, 0, 2, 2), new Rect(2, 2, 2, 2)));

    // ---- FirstFit ------------------------------------------------------------

    [Fact]
    public void FirstFit_returns_top_left_when_empty()
        => Assert.Equal((0, 0), StashGeometry.FirstFit(Stash, [], 1, 1, StashRows));

    [Fact]
    public void FirstFit_scans_left_to_right_then_down()
    {
        // (0,0) 1×1 점유 → 다음 1×1은 (1,0).
        var occupied = new[] { new Rect(0, 0, 1, 1) };
        Assert.Equal((1, 0), StashGeometry.FirstFit(Stash, occupied, 1, 1, StashRows));
    }

    [Fact]
    public void FirstFit_skips_occupied_and_respects_footprint()
    {
        // 첫 행 전체(y=0)를 폭 12로 막으면 4×2 무기는 (0,1)부터 들어간다.
        var occupied = new[] { new Rect(0, 0, 12, 1) };
        Assert.Equal((0, 1), StashGeometry.FirstFit(Stash, occupied, 4, 2, StashRows));
    }

    [Fact]
    public void FirstFit_returns_null_when_grid_full()
    {
        // 그리드 전체(12×12)를 한 사각형으로 덮으면 1×1도 못 들어간다.
        var occupied = new[] { new Rect(0, 0, 12, StashRows) };
        Assert.Null(StashGeometry.FirstFit(Stash, occupied, 1, 1, StashRows));
    }

    [Fact]
    public void FirstFit_returns_null_when_no_room_for_wide_item_but_room_for_small()
    {
        // 각 행 왼쪽 9칸을 막으면 남는 폭은 3 → 4폭 무기는 배치 불가(null).
        var occupied = new[] { new Rect(0, 0, 9, StashRows) };
        Assert.Null(StashGeometry.FirstFit(Stash, occupied, 4, 2, StashRows));
        // 하지만 1×1은 (9,0)에 들어간다 — 로직이 폭을 정확히 존중함을 확인.
        Assert.Equal((9, 0), StashGeometry.FirstFit(Stash, occupied, 1, 1, StashRows));
    }

    [Fact]
    public void FirstFit_finds_gap_between_occupied_rects()
    {
        // (0,0)과 (3,0)을 각각 2×2로 막으면 폭2 아이템은 (5,0)에 들어간다.
        var occupied = new[] { new Rect(0, 0, 2, 2), new Rect(3, 0, 2, 2) };
        Assert.Equal((5, 0), StashGeometry.FirstFit(Stash, occupied, 2, 2, StashRows));
    }

    [Fact]
    public void FirstFit_pockets_respects_4x1_bounds()
    {
        // POCKETS(4×1) 3칸이 차 있으면 남은 자리는 (3,0) 하나뿐.
        var occupied = new[] { new Rect(0, 0, 1, 1), new Rect(1, 0, 1, 1), new Rect(2, 0, 1, 1) };
        Assert.Equal((3, 0), StashGeometry.FirstFit(GridContainer.Pockets, occupied, 1, 1, StashRows));
        // 다 차면(4칸) null.
        var full = occupied.Append(new Rect(3, 0, 1, 1)).ToArray();
        Assert.Null(StashGeometry.FirstFit(GridContainer.Pockets, full, 1, 1, StashRows));
    }

    // ---- 중첩 컨테이너(동적 크기) — 백팩 5×5 / 리그 4×3 등 template 기반 그리드 -----------

    [Fact]
    public void InBounds_dynamic_dims_respects_nested_container_size()
    {
        // 리그 4×3: (3,2,1,1)은 딱 맞고(경계 flush), (4,0,1,1)은 폭 초과, (0,3,1,1)은 높이 초과.
        Assert.True(StashGeometry.InBounds(4, 3, new Rect(3, 2, 1, 1)));
        Assert.False(StashGeometry.InBounds(4, 3, new Rect(4, 0, 1, 1)));
        Assert.False(StashGeometry.InBounds(4, 3, new Rect(0, 3, 1, 1)));
    }

    [Fact]
    public void InBounds_dynamic_dims_rejects_footprint_crossing_edge()
    {
        // 백팩 5×5: 손도끼(1×2)를 (4,4)에 두면 y+h=6 > 5 → 경계 밖.
        Assert.False(StashGeometry.InBounds(5, 5, new Rect(4, 4, 1, 2)));
        // (4,3)이면 y+h=5 로 딱 맞는다.
        Assert.True(StashGeometry.InBounds(5, 5, new Rect(4, 3, 1, 2)));
    }

    [Fact]
    public void FirstFit_dynamic_dims_scans_within_nested_grid()
    {
        // 백팩 5×5의 첫 행을 폭 5로 막으면 다음 1×1은 (0,1).
        var occupied = new[] { new Rect(0, 0, 5, 1) };
        Assert.Equal((0, 1), StashGeometry.FirstFit(5, 5, occupied, 1, 1));
    }

    [Fact]
    public void FirstFit_dynamic_dims_returns_null_when_nested_grid_full()
        => Assert.Null(StashGeometry.FirstFit(4, 3, [new Rect(0, 0, 4, 3)], 1, 1));
}

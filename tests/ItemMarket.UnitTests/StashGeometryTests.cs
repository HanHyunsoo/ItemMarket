using ItemMarket.Grains.Data;
using Xunit;

namespace ItemMarket.UnitTests;

/// <summary>
/// StashGeometry 순수 기하 로직(DB/Orleans 무관). 경계·겹침·first-fit을 핀 고정.
/// 그리드는 GridW=10, GridH=12 고정.
/// </summary>
public class StashGeometryTests
{
    // ---- InBounds ----------------------------------------------------------

    [Fact]
    public void InBounds_accepts_top_left_1x1()
        => Assert.True(StashGeometry.InBounds(new Rect(0, 0, 1, 1)));

    [Fact]
    public void InBounds_accepts_footprint_flush_to_bottom_right()
        // x+w=10, y+h=12 로 경계에 정확히 맞닿는 배치는 유효.
        => Assert.True(StashGeometry.InBounds(new Rect(6, 10, 4, 2)));

    [Theory]
    [InlineData(-1, 0, 1, 1)]   // x 음수
    [InlineData(0, -1, 1, 1)]   // y 음수
    [InlineData(10, 0, 1, 1)]   // x+w=11 > 10
    [InlineData(0, 12, 1, 1)]   // y+h=13 > 12
    [InlineData(7, 0, 4, 2)]    // x+w=11 > 10 (4폭 무기)
    [InlineData(0, 11, 1, 2)]   // y+h=13 > 12
    [InlineData(0, 0, 0, 1)]    // w<1
    [InlineData(0, 0, 1, 0)]    // h<1
    public void InBounds_rejects_out_of_grid(int x, int y, int w, int h)
        => Assert.False(StashGeometry.InBounds(new Rect(x, y, w, h)));

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

    // ---- FirstFit ----------------------------------------------------------

    [Fact]
    public void FirstFit_returns_top_left_when_empty()
        => Assert.Equal((0, 0), StashGeometry.FirstFit([], 1, 1));

    [Fact]
    public void FirstFit_scans_left_to_right_then_down()
    {
        // (0,0) 1×1 점유 → 다음 1×1은 (1,0).
        var occupied = new[] { new Rect(0, 0, 1, 1) };
        Assert.Equal((1, 0), StashGeometry.FirstFit(occupied, 1, 1));
    }

    [Fact]
    public void FirstFit_skips_occupied_and_respects_footprint()
    {
        // 첫 행 전체(y=0)를 폭 10으로 막으면 4×2 무기는 (0,1)부터 들어간다.
        var occupied = new[] { new Rect(0, 0, 10, 1) };
        Assert.Equal((0, 1), StashGeometry.FirstFit(occupied, 4, 2));
    }

    [Fact]
    public void FirstFit_returns_null_when_grid_full()
    {
        // 그리드 전체(10×12)를 한 사각형으로 덮으면 1×1도 못 들어간다.
        var occupied = new[] { new Rect(0, 0, 10, 12) };
        Assert.Null(StashGeometry.FirstFit(occupied, 1, 1));
    }

    [Fact]
    public void FirstFit_returns_null_when_no_room_for_wide_item_but_room_for_small()
    {
        // 각 행 왼쪽 7칸을 막으면 남는 폭은 3 → 4폭 무기는 배치 불가(null).
        var occupied = new[] { new Rect(0, 0, 7, 12) };
        Assert.Null(StashGeometry.FirstFit(occupied, 4, 2));
        // 하지만 1×1은 (7,0)에 들어간다 — 로직이 폭을 정확히 존중함을 확인.
        Assert.Equal((7, 0), StashGeometry.FirstFit(occupied, 1, 1));
    }

    [Fact]
    public void FirstFit_finds_gap_between_occupied_rects()
    {
        // (0,0)과 (3,0)을 각각 2×2로 막으면 폭2 아이템은 (5,0)? 아니, x=2에 폭2면 x+w=4>3와 겹침.
        // 실제: candidate (2,0,2,2) 는 (3,0,2,2)와 겹침 → 다음 후보들…
        // 남는 첫 자리는 (5,0) (x[5,7)은 두 점유와 무관).
        var occupied = new[] { new Rect(0, 0, 2, 2), new Rect(3, 0, 2, 2) };
        Assert.Equal((5, 0), StashGeometry.FirstFit(occupied, 2, 2));
    }
}

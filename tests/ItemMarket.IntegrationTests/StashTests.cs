using System.Net;
using System.Net.Http.Json;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Stash;
using Xunit;
using static ItemMarket.IntegrationTests.MarketAppFixture;
using ErrorCode = ItemMarket.Contracts.Common.ErrorCode; // Orleans.ErrorCode와 모호성 방지

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 스태시(그리드 인벤토리) 통합테스트. 실제 API+Orleans+Postgres 경로.
/// 자동 배치(first-fit), 유효 이동, 경계 밖 거부, 겹침 거부, 대형 무기 footprint 존중.
/// </summary>
[Collection("market")]
public class StashTests(MarketAppFixture f)
{
    private readonly MarketAppFixture _f = f;

    private static async Task<StashDto> GetStash(HttpClient c)
        => (await Api<StashDto>(await c.GetAsync("/api/stash"))).Data!;

    // 첫 GET /api/stash 에서 소유 아이템이 자동 배치되고 그리드가 10×12로 노출된다.
    [Fact]
    public async Task First_get_auto_places_owned_items()
    {
        var alpha = await _f.AuthedAs(Alpha); // 시드: 스택 3종(1,31,95)
        var stash = await GetStash(alpha);

        Assert.Equal(10, stash.GridW);
        Assert.Equal(12, stash.GridH);
        Assert.NotEmpty(stash.Placements);
        // 시드된 스택 3종이 모두 배치되어야 한다.
        Assert.Contains(stash.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 1);
        Assert.Contains(stash.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 31);
        Assert.Contains(stash.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 95);
        // 스택은 1×1. 자동 배치 결과는 안정적(재조회해도 자리 유지).
        Assert.All(stash.Placements.Where(p => p.Kind == StashEntryKind.Stack),
            p => Assert.True(p.W == 1 && p.H == 1));

        var again = await GetStash(alpha);
        var first = stash.Placements.First(p => p.TemplateId == 1);
        var second = again.Placements.First(p => p.TemplateId == 1);
        Assert.Equal((first.X, first.Y), (second.X, second.Y)); // 재배치 없이 자리 보존
    }

    // 유효 이동: 빈 칸으로 옮기면 그 좌표가 반영된다.
    [Fact]
    public async Task Valid_move_updates_position()
    {
        var admin = await _f.AuthedAs(Charlie);
        var bravo = await _f.AuthedAs(Bravo);

        // 격리를 위해 아직 안 쓴 스택 템플릿 26번(사탕)을 Bravo에게 지급.
        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, 26, 3), Json)).EnsureSuccessStatusCode();
        await GetStash(bravo); // 자동 배치 트리거

        // 그리드 바닥(빈 칸)으로 이동 — 시드 무기(좌상단 자동 배치)와 무관.
        var move = await Api<StashDto>(await bravo.PostAsJsonAsync(
            "/api/stash/move", new MoveStashItemRequest(StashEntryKind.Stack, 26, null, 2, 11), Json));
        Assert.True(move.Success);
        var placed = Assert.Single(move.Data!.Placements, p => p.TemplateId == 26);
        Assert.Equal((2, 11), (placed.X, placed.Y));
    }

    // 경계 밖 이동은 PlacementInvalid + 400.
    [Fact]
    public async Task Out_of_bounds_move_is_rejected()
    {
        var admin = await _f.AuthedAs(Charlie);
        var alpha = await _f.AuthedAs(Alpha);

        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Alpha, 27, 1), Json)).EnsureSuccessStatusCode();
        await GetStash(alpha);

        // x=10 → 1×1도 x+w=11 > 10 이라 경계 밖.
        var res = await alpha.PostAsJsonAsync(
            "/api/stash/move", new MoveStashItemRequest(StashEntryKind.Stack, 27, null, 10, 0), Json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(ErrorCode.PlacementInvalid, (await Api<StashDto>(res)).Error!.Code);
    }

    // 겹침 이동은 PlacementInvalid + 400.
    [Fact]
    public async Task Overlapping_move_is_rejected()
    {
        var admin = await _f.AuthedAs(Charlie);
        var bravo = await _f.AuthedAs(Bravo);

        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, 28, 1), Json)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, 29, 1), Json)).EnsureSuccessStatusCode();
        await GetStash(bravo);

        // 28을 바닥 빈 칸 (5,11)에 고정(시드 무기와 무관).
        var a = await Api<StashDto>(await bravo.PostAsJsonAsync(
            "/api/stash/move", new MoveStashItemRequest(StashEntryKind.Stack, 28, null, 5, 11), Json));
        Assert.True(a.Success);

        // 29를 같은 (5,11)로 이동 시도 → 겹침 거부.
        var res = await bravo.PostAsJsonAsync(
            "/api/stash/move", new MoveStashItemRequest(StashEntryKind.Stack, 29, null, 5, 11), Json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(ErrorCode.PlacementInvalid, (await Api<StashDto>(res)).Error!.Code);
    }

    // 대형 무기(AK-47 4×2) footprint가 배치·경계·겹침에서 존중된다.
    [Fact]
    public async Task Large_weapon_footprint_is_respected()
    {
        var admin = await _f.AuthedAs(Charlie);
        var charlie = await _f.AuthedAs(Charlie);

        // Charlie에게 AK-47(템플릿 83, 4×2) 인스턴스 지급.
        var granted = await Api<ItemInstanceDto>(await admin.PostAsJsonAsync("/api/admin/grant/instance",
            new AdminGrantInstanceRequest(Charlie, 83, 460, null), Json));
        Assert.True(granted.Success);
        var ak = granted.Data!.Id;

        var stash = await GetStash(charlie);
        var akPlaced = Assert.Single(stash.Placements, p => p.InstanceId == ak);
        Assert.Equal((4, 2), (akPlaced.W, akPlaced.H)); // footprint 4×2 노출

        // 유효 이동: x=6,y=10 이면 x+w=10, y+h=12 로 경계에 딱 맞는다.
        var ok = await Api<StashDto>(await charlie.PostAsJsonAsync(
            "/api/stash/move", new MoveStashItemRequest(StashEntryKind.Instance, null, ak, 6, 10), Json));
        Assert.True(ok.Success);
        Assert.Equal((6, 10), (ok.Data!.Placements.Single(p => p.InstanceId == ak).X,
                               ok.Data!.Placements.Single(p => p.InstanceId == ak).Y));

        // 경계 밖: x=7 이면 x+w=11 > 10 → 거부(4폭이라 1×1이면 통과할 자리).
        var oob = await charlie.PostAsJsonAsync(
            "/api/stash/move", new MoveStashItemRequest(StashEntryKind.Instance, null, ak, 7, 10), Json);
        Assert.Equal(HttpStatusCode.BadRequest, oob.StatusCode);
        Assert.Equal(ErrorCode.PlacementInvalid, (await Api<StashDto>(oob)).Error!.Code);
    }
}

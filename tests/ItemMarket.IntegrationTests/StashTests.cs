using System.Net;
using System.Net.Http.Json;
using Dapper;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Stash;
using Npgsql;
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

    private static async Task<StashDto> GetContainer(HttpClient c, string container)
        => (await Api<StashDto>(await c.GetAsync($"/api/stash/{container}"))).Data!;

    private static async Task<int> StackQty(HttpClient c, int templateId)
    {
        var inv = (await Api<InventoryDto>(await c.GetAsync("/api/inventory"))).Data!;
        return inv.Stacks.FirstOrDefault(s => s.TemplateId == templateId)?.Quantity ?? 0;
    }

    // 첫 GET /api/stash 에서 소유 아이템이 자동 배치되고 그리드가 12×stash_rows(기본 60)로 노출된다.
    [Fact]
    public async Task First_get_auto_places_owned_items()
    {
        var alpha = await _f.AuthedAs(Alpha); // 시드: 스택 3종(1,31,95)
        var stash = await GetStash(alpha);

        Assert.Equal(12, stash.GridW);   // 가로는 항상 12 고정
        Assert.Equal(60, stash.GridH);   // 세로는 player.stash_rows(시드 기본값 60)
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

    // 컨테이너별 조회(BUG2/M1): stash/pockets는 정상, 중첩 컨테이너(container)는 인스턴스 id가
    // 필수라 이 라우트로 조회 불가 — 500(NRE)이 아니라 명확한 400 ValidationError로 거부한다.
    [Fact]
    public async Task Get_stash_by_container_rejects_nested_container_with_400()
    {
        var alpha = await _f.AuthedAs(Alpha);

        // 정상 컨테이너 조회는 200.
        var stash = await alpha.GetAsync("/api/stash/stash");
        Assert.Equal(HttpStatusCode.OK, stash.StatusCode);
        var pockets = await alpha.GetAsync("/api/stash/pockets");
        Assert.Equal(HttpStatusCode.OK, pockets.StatusCode);

        // 중첩 컨테이너는 400(500 아님).
        var nested = await alpha.GetAsync("/api/stash/container");
        Assert.Equal(HttpStatusCode.BadRequest, nested.StatusCode);
        Assert.Equal(ErrorCode.ValidationError, (await Api<StashDto>(nested)).Error!.Code);

        // 알 수 없는 컨테이너도 400.
        var unknown = await alpha.GetAsync("/api/stash/bogus");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(ErrorCode.ValidationError, (await Api<StashDto>(unknown)).Error!.Code);
    }

    // L2/BUG4: 스택 이동 수량 하한(<1)은 모든 경로에서 거부된다(빈 풀 분기가 음수·0을 조용히
    // no-op 성공으로 흘려보내던 비일관 제거). template 1은 Alpha 시드 스택(자동 배치됨).
    [Fact]
    public async Task Stack_move_with_non_positive_quantity_is_rejected()
    {
        var alpha = await _f.AuthedAs(Alpha);
        await GetStash(alpha); // 소유 스택 자동 배치

        foreach (var bad in new[] { 0, -5 })
        {
            var res = await alpha.PostAsJsonAsync("/api/stash/move",
                new MoveStashItemRequest(StashEntryKind.Stack, 1, null, 3, 3, Quantity: bad), Json);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Equal(ErrorCode.ValidationError, (await Api<StashDto>(res)).Error!.Code);
        }
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

        // x=12 → 1×1도 x+w=13 > 12(STASH 고정 폭) 이라 경계 밖.
        var res = await alpha.PostAsJsonAsync(
            "/api/stash/move", new MoveStashItemRequest(StashEntryKind.Stack, 27, null, 12, 0), Json);
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

        // 28을 빈 칸 (5,11)에 고정(시드 무기와 무관).
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

        // 유효 이동: x=8,y=10 이면 x+w=12 로 STASH 고정 폭 경계에 딱 맞는다.
        var ok = await Api<StashDto>(await charlie.PostAsJsonAsync(
            "/api/stash/move", new MoveStashItemRequest(StashEntryKind.Instance, null, ak, 8, 10), Json));
        Assert.True(ok.Success);
        Assert.Equal((8, 10), (ok.Data!.Placements.Single(p => p.InstanceId == ak).X,
                               ok.Data!.Placements.Single(p => p.InstanceId == ak).Y));

        // 경계 밖: x=9 이면 x+w=13 > 12 → 거부(4폭이라 1×1이면 통과할 자리).
        var oob = await charlie.PostAsJsonAsync(
            "/api/stash/move", new MoveStashItemRequest(StashEntryKind.Instance, null, ak, 9, 10), Json);
        Assert.Equal(HttpStatusCode.BadRequest, oob.StatusCode);
        Assert.Equal(ErrorCode.PlacementInvalid, (await Api<StashDto>(oob)).Error!.Code);
    }

    // 회귀: 같은 템플릿의 무기를 여러 자루 보유해도 각 인스턴스가 개별 배치된다.
    // (과거 uq_stash_stack이 INSTANCE 행에도 적용되어 두 번째 배치가 duplicate key로 실패했음.)
    [Fact]
    public async Task Multiple_instances_of_same_template_all_place()
    {
        var admin = await _f.AuthedAs(Charlie);
        var alpha = await _f.AuthedAs(Alpha);

        // Alpha에게 마카로프 권총(템플릿 74) 인스턴스 2자루 지급.
        var g1 = await Api<ItemInstanceDto>(await admin.PostAsJsonAsync("/api/admin/grant/instance",
            new AdminGrantInstanceRequest(Alpha, 74, 300, null), Json));
        var g2 = await Api<ItemInstanceDto>(await admin.PostAsJsonAsync("/api/admin/grant/instance",
            new AdminGrantInstanceRequest(Alpha, 74, 250, null), Json));
        Assert.True(g1.Success && g2.Success);

        // GetStash가 duplicate key 없이 두 인스턴스를 모두 배치해야 한다.
        var stash = await GetStash(alpha);
        Assert.Contains(stash.Placements, p => p.InstanceId == g1.Data!.Id);
        Assert.Contains(stash.Placements, p => p.InstanceId == g2.Data!.Id);
    }

    // POCKETS는 4×1(내재 컨테이너)이며 새 아이템은 STASH에 자동 배치되어 POCKETS는 비어 시작한다(이동으로만 채워짐).
    [Fact]
    public async Task Pockets_starts_empty_and_reports_4x1()
    {
        var admin = await _f.AuthedAs(Charlie);
        var bravo = await _f.AuthedAs(Bravo);
        await ClearPockets(Bravo);

        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, 44, 5), Json)).EnsureSuccessStatusCode(); // 봉합 키트
        await GetStash(bravo); // 자동 배치(→ STASH)

        var pockets = await GetContainer(bravo, "pockets");
        Assert.Equal(GridContainer.Pockets, pockets.Container);
        Assert.Equal(4, pockets.GridW);
        Assert.Equal(1, pockets.GridH);
        // 새 아이템은 STASH로 가고 POCKETS엔 들어가지 않는다.
        Assert.DoesNotContain(pockets.Placements, p => p.TemplateId == 44);
        var stash = await GetContainer(bravo, "stash");
        Assert.Contains(stash.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 44);
    }

    // stash→pockets 부분 이동: 지정 수량만 POCKETS로 가고 나머지는 STASH에 남으며, 총 소유량은 보존된다.
    [Fact]
    public async Task Move_stash_to_pockets_moves_quantity_and_conserves_inventory()
    {
        var admin = await _f.AuthedAs(Charlie);
        var alpha = await _f.AuthedAs(Alpha);
        await ClearPockets(Alpha);

        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Alpha, 40, 10), Json)).EnsureSuccessStatusCode(); // 수술 키트(max_stack=5)
        await GetStash(alpha);
        var totalBefore = await StackQty(alpha, 40);
        Assert.Equal(10, totalBefore);

        // 10개 중 4개를 POCKETS (0,0)으로 반입(max_stack=5 이내라 그대로 병합/생성된다).
        var moved = await Api<StashDto>(await alpha.PostAsJsonAsync("/api/stash/move",
            new MoveStashItemRequest(StashEntryKind.Stack, 40, null, 0, 0,
                GridContainer.Stash, GridContainer.Pockets, 4), Json));
        Assert.True(moved.Success);
        Assert.Equal(GridContainer.Pockets, moved.Data!.Container);
        var inPockets = Assert.Single(moved.Data.Placements, p => p.TemplateId == 40);
        Assert.Equal((0, 0), (inPockets.X, inPockets.Y));
        Assert.Equal(4, inPockets.Quantity);

        // 나머지 6개는 STASH에 남는다(수술 키트 max_stack=5라 두 칸으로 나뉠 수 있음 — 합계로 검증).
        var stash = await GetContainer(alpha, "stash");
        var inStashQty = stash.Placements.Where(p => p.TemplateId == 40).Sum(p => p.Quantity);
        Assert.Equal(6, inStashQty);

        // 총 소유량은 그대로(중복/유실 없음).
        Assert.Equal(10, await StackQty(alpha, 40));

        await ClearPockets(Alpha);
    }

    // POCKETS(4×1) 경계 밖 이동과 겹침 이동은 PlacementInvalid + 400.
    [Fact]
    public async Task Pockets_respects_4x1_bounds_and_overlap()
    {
        var admin = await _f.AuthedAs(Charlie);
        var bravo = await _f.AuthedAs(Bravo);
        await ClearPockets(Bravo);

        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, 45, 1), Json)).EnsureSuccessStatusCode(); // 비타민
        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, 46, 1), Json)).EnsureSuccessStatusCode(); // 해독제
        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, 47, 1), Json)).EnsureSuccessStatusCode(); // 수혈팩
        await GetStash(bravo);

        // 경계 밖: x=4 이면 1×1도 x+w=5 > 4(POCKETS 고정 폭) → 거부.
        var oob = await bravo.PostAsJsonAsync("/api/stash/move",
            new MoveStashItemRequest(StashEntryKind.Stack, 45, null, 4, 0,
                GridContainer.Stash, GridContainer.Pockets, 1), Json);
        Assert.Equal(HttpStatusCode.BadRequest, oob.StatusCode);
        Assert.Equal(ErrorCode.PlacementInvalid, (await Api<StashDto>(oob)).Error!.Code);

        // 46을 POCKETS (0,0)에 반입(성공).
        var ok = await Api<StashDto>(await bravo.PostAsJsonAsync("/api/stash/move",
            new MoveStashItemRequest(StashEntryKind.Stack, 46, null, 0, 0,
                GridContainer.Stash, GridContainer.Pockets, 1), Json));
        Assert.True(ok.Success);

        // 47(다른 템플릿)을 같은 (0,0)으로 반입 시도 → 겹침 거부.
        var clash = await bravo.PostAsJsonAsync("/api/stash/move",
            new MoveStashItemRequest(StashEntryKind.Stack, 47, null, 0, 0,
                GridContainer.Stash, GridContainer.Pockets, 1), Json);
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        Assert.Equal(ErrorCode.PlacementInvalid, (await Api<StashDto>(clash)).Error!.Code);

        await ClearPockets(Bravo);
    }

    // 대형 무기(AK-47 4×2)는 POCKETS(4×1, 높이 1)에 들어갈 수 없다 — 별도의 "소형 전용" 규칙 없이
    // footprint 경계 검증만으로 자연히 막힌다(설계 문서의 "natural enforcement").
    [Fact]
    public async Task Large_weapon_does_not_fit_pockets_height()
    {
        var admin = await _f.AuthedAs(Charlie);
        var charlie = await _f.AuthedAs(Charlie);

        var granted = await Api<ItemInstanceDto>(await admin.PostAsJsonAsync("/api/admin/grant/instance",
            new AdminGrantInstanceRequest(Charlie, 83, 500, null), Json)); // AK-47 4×2
        Assert.True(granted.Success);
        var ak = granted.Data!.Id;
        await GetStash(charlie); // STASH 자동 배치

        // 어떤 (x,0)을 시도해도 h=2 > POCKETS 높이(1)라 항상 경계 밖.
        var oob = await charlie.PostAsJsonAsync("/api/stash/move",
            new MoveStashItemRequest(StashEntryKind.Instance, null, ak, 0, 0,
                GridContainer.Stash, GridContainer.Pockets), Json);
        Assert.Equal(HttpStatusCode.BadRequest, oob.StatusCode);
        Assert.Equal(ErrorCode.PlacementInvalid, (await Api<StashDto>(oob)).Error!.Code);

        // 인스턴스는 실패한 이동으로 사라지지 않는다 — 여전히 STASH에 있다.
        var stash = await GetContainer(charlie, "stash");
        Assert.Contains(stash.Placements, p => p.InstanceId == ak);
    }

    // 다중 스택: max_stack(60, AMMO)을 넘는 수량은 자동 배치 시 여러 칸으로 쪼개진다.
    [Fact]
    public async Task Multi_stack_auto_placement_splits_by_max_stack()
    {
        var admin = await _f.AuthedAs(Charlie);
        var bravo = await _f.AuthedAs(Bravo);

        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, 98, 130), Json)).EnsureSuccessStatusCode(); // .308 탄약(max_stack=60, 다른 테스트가 안 건드리는 템플릿)

        var stash = await GetStash(bravo);
        var stacks = stash.Placements.Where(p => p.TemplateId == 98).ToList();

        // 130 = 60 + 60 + 10 → 세 칸으로 분리되고, 각 칸은 max_stack(60)을 넘지 않는다.
        Assert.Equal(3, stacks.Count);
        Assert.All(stacks, p => Assert.True(p.Quantity <= 60 && p.W == 1 && p.H == 1));
        Assert.Equal(130, stacks.Sum(p => p.Quantity));
        Assert.Equal(new[] { 10, 60, 60 }, stacks.Select(p => p.Quantity).OrderBy(q => q));

        // 재조회해도 칸 배분이 안정적으로 유지된다(재분배 없음).
        var again = await GetStash(bravo);
        Assert.Equal(stacks.Select(p => (p.X, p.Y, p.Quantity)).OrderBy(t => t),
            again.Placements.Where(p => p.TemplateId == 98).Select(p => (p.X, p.Y, p.Quantity)).OrderBy(t => t));
    }

    // 다중 스택 이동: 목적지 칸에 같은 템플릿 스택이 있으면 max_stack까지만 병합되고,
    // 초과분은 원본(풀)에 그대로 남는다(유실 없음, 총 소유량 보존).
    [Fact]
    public async Task Move_stack_merge_caps_at_max_stack_and_leaves_overflow_at_source()
    {
        var admin = await _f.AuthedAs(Charlie);
        var alpha = await _f.AuthedAs(Alpha);
        await ClearPockets(Alpha);

        // 해열제(51, MEDICAL, max_stack=5)를 8개 지급 → STASH에 5+3 두 칸으로 자동 분리된다.
        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Alpha, 51, 8), Json)).EnsureSuccessStatusCode();
        await GetStash(alpha);
        Assert.Equal(8, await StackQty(alpha, 51));

        // 1) STASH → POCKETS(0,0)로 3개 이동(풀 전체 8개 중 3개). 목적지가 비어 있으므로 그대로 3개 생성.
        var first = await Api<StashDto>(await alpha.PostAsJsonAsync("/api/stash/move",
            new MoveStashItemRequest(StashEntryKind.Stack, 51, null, 0, 0,
                GridContainer.Stash, GridContainer.Pockets, 3), Json));
        Assert.True(first.Success);
        Assert.Equal(3, Assert.Single(first.Data!.Placements, p => p.TemplateId == 51).Quantity);

        // 2) 나머지 스택 풀(5개) 전체를 같은 POCKETS(0,0)로 병합 시도.
        //    목적지 여유 = max_stack(5) - 기존(3) = 2 → 2개만 병합되고 3개는 STASH(원본)에 남는다.
        var second = await Api<StashDto>(await alpha.PostAsJsonAsync("/api/stash/move",
            new MoveStashItemRequest(StashEntryKind.Stack, 51, null, 0, 0,
                GridContainer.Stash, GridContainer.Pockets, 5), Json));
        Assert.True(second.Success);
        var pocketsQty = Assert.Single(second.Data!.Placements, p => p.TemplateId == 51).Quantity;
        Assert.Equal(5, pocketsQty); // max_stack 상한에서 캡핑

        var stashLeft = (await GetContainer(alpha, "stash")).Placements
            .Where(p => p.TemplateId == 51).Sum(p => p.Quantity);
        Assert.Equal(3, stashLeft); // 오버플로(2개 초과분 아닌 나머지 3개)는 원본에 남는다

        // 총 소유량은 어느 시점에도 유실/중복 없이 보존된다.
        Assert.Equal(8, await StackQty(alpha, 51));

        await ClearPockets(Alpha);
    }

    // 스태시 세로 칸 수는 player.stash_rows를 그대로 따르는 가변값이다(가로만 12로 고정).
    [Fact]
    public async Task Stash_height_follows_player_stash_rows_and_width_stays_fixed()
    {
        var alpha = await _f.AuthedAs(Alpha);
        await using var db = new NpgsqlConnection(_f.ConnString);
        await db.OpenAsync();
        try
        {
            await db.ExecuteAsync("UPDATE player SET stash_rows = 20 WHERE id = @p", new { p = Alpha });

            var stash = await GetStash(alpha);
            Assert.Equal(12, stash.GridW);  // 가로는 항상 고정
            Assert.Equal(20, stash.GridH);  // 세로는 이 플레이어의 stash_rows를 그대로 반영
        }
        finally
        {
            // 다른 테스트가 기본값(60)을 전제하므로 반드시 원복한다.
            await db.ExecuteAsync("UPDATE player SET stash_rows = 60 WHERE id = @p", new { p = Alpha });
        }
    }

    private async Task ClearPockets(Guid player)
    {
        await using var db = new NpgsqlConnection(_f.ConnString);
        await db.OpenAsync();
        await db.ExecuteAsync(
            "DELETE FROM stash_placement WHERE player_id = @p AND container = 'POCKETS'",
            new { p = player });
    }
}

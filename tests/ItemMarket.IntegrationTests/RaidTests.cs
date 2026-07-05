using System.Net;
using System.Net.Http.Json;
using Dapper;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Equipment;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Raid;
using ItemMarket.Contracts.Stash;
using Npgsql;
using Xunit;
using static ItemMarket.IntegrationTests.MarketAppFixture;
using ErrorCode = ItemMarket.Contracts.Common.ErrorCode;

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 익스트랙션 레이드 세션 통합테스트(머니샷). 실제 API+Orleans+Postgres 경로.
///   - StartRaid: 로드아웃 → 세션(위험). 반입 아이템은 판매/이동 불가.
///   - Extract = 보존: 반입(+획득) 전량이 소유/스태시로 복귀, 총량 보존.
///   - Die = 로드아웃만 소실: 반입 소실, 스태시(안전) 무관, 손실은 원장 기록.
///   - 플레이어당 ACTIVE 레이드 1개(두 번째 StartRaid → RaidActive).
///   - 원자성: 정산 도중 실패 시 전량 롤백(best-effort 폴트 인젝션).
/// 레이드 전용 시드 플레이어(Delta/Echo/Foxtrot)를 써서 다른 테스트와 완전 격리한다.
/// </summary>
[Collection("market")]
public class RaidTests(MarketAppFixture f)
{
    private readonly MarketAppFixture _f = f;

    // ---- 헬퍼 -------------------------------------------------------------

    private async Task GrantStack(Guid player, int templateId, int qty)
    {
        var admin = await _f.AuthedAs(Charlie);
        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(player, templateId, qty), Json)).EnsureSuccessStatusCode();
    }

    private async Task<Guid> GrantInstance(Guid player, int templateId, int durability)
    {
        var admin = await _f.AuthedAs(Charlie);
        var g = await Api<ItemInstanceDto>(await admin.PostAsJsonAsync("/api/admin/grant/instance",
            new AdminGrantInstanceRequest(player, templateId, durability, null), Json));
        Assert.True(g.Success);
        return g.Data!.Id;
    }

    /// <summary>테스트 격리: 잔존 LOADOUT/중첩 배치를 지운다(익스트랙션이 이제 원위치로 복원하므로
    /// 공유 플레이어의 로드아웃이 테스트 간 누적된다). 소유는 유지되어 다음 GET /api/stash에서 STASH로 정합화된다.</summary>
    private async Task ClearLoadout(Guid player)
    {
        await using var db = new NpgsqlConnection(_f.ConnString);
        await db.OpenAsync();
        await db.ExecuteAsync(
            "DELETE FROM stash_placement WHERE player_id = @p AND container IN ('LOADOUT','CONTAINER')",
            new { p = player });
    }

    private static async Task<StashDto> Loadout(HttpClient c)
        => (await Api<StashDto>(await c.GetAsync("/api/stash/loadout"))).Data!;

    private static async Task<InventoryDto> Inv(HttpClient c)
        => (await Api<InventoryDto>(await c.GetAsync("/api/inventory"))).Data!;

    private static async Task<int> StackQty(HttpClient c, int templateId)
        => (await Inv(c)).Stacks.FirstOrDefault(s => s.TemplateId == templateId)?.Quantity ?? 0;

    private static async Task<bool> Owns(HttpClient c, Guid instanceId)
        => (await Inv(c)).Instances.Any(i => i.Id == instanceId);

    private static Task<HttpResponseMessage> Move(HttpClient c, MoveStashItemRequest req)
        => c.PostAsJsonAsync("/api/stash/move", req, Json);

    private static async Task BringStack(HttpClient c, int templateId, int qty, int x, int y)
    {
        var r = await Move(c, new MoveStashItemRequest(StashEntryKind.Stack, templateId, null, x, y,
            GridContainer.Stash, GridContainer.Loadout, qty));
        r.EnsureSuccessStatusCode();
    }

    private static async Task BringInstance(HttpClient c, Guid id, int x, int y)
    {
        var r = await Move(c, new MoveStashItemRequest(StashEntryKind.Instance, null, id, x, y,
            GridContainer.Stash, GridContainer.Loadout));
        r.EnsureSuccessStatusCode();
    }

    private static async Task<StashDto> Stash(HttpClient c)
        => (await Api<StashDto>(await c.GetAsync("/api/stash"))).Data!;

    private static Task<HttpResponseMessage> Start(HttpClient c) => c.PostAsync("/api/raid/start", null);
    private static Task<HttpResponseMessage> Extract(HttpClient c) => c.PostAsync("/api/raid/extract", null);
    private static Task<HttpResponseMessage> Die(HttpClient c) => c.PostAsync("/api/raid/die", null);
    private static Task<HttpResponseMessage> Loot(HttpClient c, AddLootRequest req)
        => c.PostAsJsonAsync("/api/raid/loot", req, Json);

    private static async Task<EquipmentDto> Equipment(HttpClient c)
        => (await Api<EquipmentDto>(await c.GetAsync("/api/equipment"))).Data!;

    private static async Task Equip(HttpClient c, EquipSlot slot, Guid id)
        => (await c.PostAsJsonAsync("/api/equipment/equip", new EquipRequest(slot, id), Json)).EnsureSuccessStatusCode();

    private static async Task ClearEquipment(HttpClient c)
    {
        foreach (var s in (await Equipment(c)).Slots)
            (await c.PostAsJsonAsync("/api/equipment/unequip", new UnequipRequest(s.Slot), Json)).EnsureSuccessStatusCode();
    }

    // ----------------------------------------------------------------------

    // StartRaid: 로드아웃 아이템이 세션(위험)으로 이동하고, 인벤에서 빠져 판매/이동 불가가 된다.
    [Fact]
    public async Task StartRaid_moves_loadout_to_session_and_locks_items()
    {
        var d = await _f.AuthedAs(Delta);
        await ClearLoadout(Delta);
        await GrantStack(Delta, 25, 8);            // MRE(스택)
        var knife = await GrantInstance(Delta, 55, 100); // 마체테(유니크 1×3)
        await Stash(d);                            // STASH 자동 배치

        await BringStack(d, 25, 8, 5, 0);          // 8개 전량 로드아웃 반입
        await BringInstance(d, knife, 0, 0);       // 마체테 로드아웃 반입

        Assert.Equal(8, await StackQty(d, 25));    // 반입은 소유 인벤을 줄이지 않음(컨테이너 이동)
        Assert.True(await Owns(d, knife));

        var started = await Api<RaidSessionDto>(await Start(d));
        Assert.True(started.Success);
        Assert.Equal(RaidStatus.Active, started.Data!.Status);
        Assert.Contains(started.Data.Items, i => i.Kind == StashEntryKind.Stack && i.TemplateId == 25
            && i.Quantity == 8 && i.Source == RaidItemSource.Brought);
        Assert.Contains(started.Data.Items, i => i.Kind == StashEntryKind.Instance && i.InstanceId == knife
            && i.Source == RaidItemSource.Brought);

        // 위험 아이템은 인벤에서 사라져 판매/배치 불가.
        Assert.Equal(0, await StackQty(d, 25));
        Assert.False(await Owns(d, knife));

        // 판매 시도: 유니크는 소유 아님 → 거부.
        var sell = await Api<PlaceOrderResult>(await d.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Sell, 55, 100, 1, knife), Json));
        Assert.False(sell.Success);
        // 스택은 재고 0 → 매도 에스크로 실패.
        var sellStack = await Api<PlaceOrderResult>(await d.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Sell, 25, 10, 1), Json));
        Assert.False(sellStack.Success);
        Assert.Equal(ErrorCode.InsufficientQuantity, sellStack.Error!.Code);

        // 배치(이동) 시도: 소유 아님 → PlacementInvalid.
        var mv = await Move(d, new MoveStashItemRequest(StashEntryKind.Instance, null, knife, 0, 0));
        Assert.Equal(HttpStatusCode.BadRequest, mv.StatusCode);
        Assert.Equal(ErrorCode.PlacementInvalid, (await Api<StashDto>(mv)).Error!.Code);

        // 정리: 탈출로 원복(다음 테스트에 ACTIVE 세션을 남기지 않음).
        (await Extract(d)).EnsureSuccessStatusCode();
    }

    // Extract = 보존: 반입 + 획득 전량이 소유/스태시로 복귀, 총량 보존.
    [Fact]
    public async Task Extract_conserves_brought_and_looted_items()
    {
        var d = await _f.AuthedAs(Delta);
        await ClearLoadout(Delta);
        await GrantStack(Delta, 22, 6);            // 꿀단지(스택)
        var axe = await GrantInstance(Delta, 59, 150); // 소방도끼(유니크)
        await Stash(d);

        var beforeStack22 = await StackQty(d, 22);
        var beforeStack93 = await StackQty(d, 93); // 획득할 탄약(사전 보유 0 예상)

        await BringStack(d, 22, 6, 5, 0);
        await BringInstance(d, axe, 0, 0);
        (await Start(d)).EnsureSuccessStatusCode();

        // 레이드 중 획득(전리품): 스택 + 유니크.
        (await Loot(d, new AddLootRequest(StashEntryKind.Stack, 93, 30))).EnsureSuccessStatusCode();
        var looted = await Api<RaidSessionDto>(await Loot(d, new AddLootRequest(StashEntryKind.Instance, 63)));
        Assert.True(looted.Success);
        var lootedKatana = looted.Data!.Items.Single(i =>
            i.Kind == StashEntryKind.Instance && i.Source == RaidItemSource.Looted).InstanceId!.Value;

        var extracted = await Api<RaidSessionDto>(await Extract(d));
        Assert.True(extracted.Success);
        Assert.Equal(RaidStatus.Extracted, extracted.Data!.Status);
        Assert.NotNull(extracted.Data.ResolvedAt);

        // 보존: 반입 스택 원복, 반입 유니크 소유 복귀.
        Assert.Equal(beforeStack22, await StackQty(d, 22));
        Assert.True(await Owns(d, axe));
        // 획득분이 소유로 추가.
        Assert.Equal(beforeStack93 + 30, await StackQty(d, 93));
        Assert.True(await Owns(d, lootedKatana));

        // 익스트랙션 시맨틱: 반입(BROUGHT) 아이템은 원위치(로드아웃 칸)로 그대로 복원된다(STASH 덤프 아님).
        var loadout = await Loadout(d);
        Assert.Contains(loadout.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 22 && p.X == 5 && p.Y == 0);
        Assert.Contains(loadout.Placements, p => p.InstanceId == axe && p.X == 0 && p.Y == 0);
        // 획득(LOOTED)은 원위치가 없어 반입 공간(백팩 없음 → LOADOUT)에 first-fit 배치된다.
        Assert.Contains(loadout.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 93);
        Assert.Contains(loadout.Placements, p => p.InstanceId == lootedKatana);
        // STASH(안전)에는 이번 회수분이 덤프되지 않는다.
        var stash = await Stash(d);
        Assert.DoesNotContain(stash.Placements, p => p.InstanceId == axe);
    }

    // Die = 로드아웃만 소실: 반입 소실, 스태시(안전) 무관, 손실은 item_ledger에 기록.
    [Fact]
    public async Task Die_destroys_at_risk_only_and_stash_is_untouched()
    {
        var d = await _f.AuthedAs(Delta);
        await ClearLoadout(Delta);
        await GrantStack(Delta, 21, 5);   // 땅콩버터 — STASH에 남길 안전 아이템(반입 안 함)
        await GrantStack(Delta, 12, 7);   // 쌀 — 반입(소실 대상)
        var hatchet = await GrantInstance(Delta, 60, 100); // 손도끼 — 반입(소실 대상)
        await Stash(d);

        var safeBefore = await StackQty(d, 21);
        Assert.Equal(7, await StackQty(d, 12));

        await BringStack(d, 12, 7, 5, 0);
        await BringInstance(d, hatchet, 0, 0);
        var started = await Api<RaidSessionDto>(await Start(d));
        var sessionId = started.Data!.Id;
        (await Loot(d, new AddLootRequest(StashEntryKind.Stack, 94, 10))).EnsureSuccessStatusCode(); // 획득분

        var died = await Api<RaidSessionDto>(await Die(d));
        Assert.True(died.Success);
        Assert.Equal(RaidStatus.Died, died.Data!.Status);

        // 반입/획득 전량 소실.
        Assert.Equal(0, await StackQty(d, 12));
        Assert.False(await Owns(d, hatchet));
        Assert.Equal(0, await StackQty(d, 94)); // 획득 스택도 소실(소유로 오지 않음)

        // 스태시(안전)는 무관 — 반입하지 않은 땅콩버터는 그대로.
        Assert.Equal(safeBefore, await StackQty(d, 21));
        var stash = await Stash(d);
        Assert.Contains(stash.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 21);

        // 손실이 item_ledger(RAID_LOSS)에 회계된다(반입 스택+반입 유니크+획득 스택 = 3건).
        await using var db = new NpgsqlConnection(_f.ConnString);
        await db.OpenAsync();
        var lossCount = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM item_ledger WHERE player_id = @p AND reason = 'RAID_LOSS' AND ref_id = @s",
            new { p = Delta, s = sessionId });
        Assert.Equal(3, lossCount);
    }

    // 플레이어당 ACTIVE 레이드는 1개 — 두 번째 StartRaid는 RaidActive(409).
    [Fact]
    public async Task Second_start_raid_is_rejected_while_active()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearLoadout(Echo);
        await GrantStack(Echo, 24, 3); // 건빵
        await Stash(e);
        await BringStack(e, 24, 3, 0, 0);

        (await Start(e)).EnsureSuccessStatusCode();

        var second = await Start(e);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ErrorCode.RaidActive, (await Api<RaidSessionDto>(second)).Error!.Code);

        // 정리.
        (await Die(e)).EnsureSuccessStatusCode();

        // 활성 세션이 없으면 Extract/Die는 RaidNotFound(404).
        var noActive = await Extract(e);
        Assert.Equal(HttpStatusCode.NotFound, noActive.StatusCode);
        Assert.Equal(ErrorCode.RaidNotFound, (await Api<RaidSessionDto>(noActive)).Error!.Code);
    }

    // 원자성(best-effort 폴트 인젝션): 정산 도중 실패하면 전량 롤백된다.
    // 로드아웃 수량과 inventory_stack을 어긋나게(외부 변조) 만들어 StartRaid 스택 가드를 실패시키고,
    // 같은 트랜잭션에서 먼저 INSERT된 raid_session이 롤백되는지(ACTIVE 세션 미생성) 검증한다.
    [Fact]
    public async Task StartRaid_rolls_back_entirely_on_mid_settlement_failure()
    {
        var fx = await _f.AuthedAs(Foxtrot);
        await ClearLoadout(Foxtrot);
        await GrantStack(Foxtrot, 11, 5);            // 라면
        var knife = await GrantInstance(Foxtrot, 54, 90); // 전투용 나이프
        await Stash(fx);
        await BringStack(fx, 11, 5, 5, 0);
        await BringInstance(fx, knife, 0, 0);

        // 폴트 인젝션: 로드아웃은 5개인데 소유 재고를 1로 낮춰 StartRaid 스택 가드를 실패시킨다.
        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            await db.ExecuteAsync(
                "UPDATE inventory_stack SET quantity = 1 WHERE player_id = @p AND template_id = 11",
                new { p = Foxtrot });
        }

        var res = await Start(fx);
        Assert.False((await Api<RaidSessionDto>(res)).Success); // 정산 실패

        // 롤백 검증: ACTIVE 세션이 생성되지 않았고(트랜잭션 원자성),
        var snap = await Api<RaidSessionDto?>(await fx.GetAsync("/api/raid"));
        Assert.True(snap.Data is null || snap.Data.Status != RaidStatus.Active);

        // 유니크는 여전히 소유(instance 루프까지 도달하지 않고 롤백), 원장/세션아이템도 미기록.
        Assert.True(await Owns(fx, knife));
        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            var sessions = await db.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM raid_session WHERE player_id = @p", new { p = Foxtrot });
            Assert.Equal(0, sessions);
            var ledger = await db.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM item_ledger WHERE player_id = @p", new { p = Foxtrot });
            Assert.Equal(0, ledger);
        }
    }

    // Refinement 1: Extract는 원위치로 복원한다 — 로드아웃 아이템은 원래 칸, 장착 아이템은 원래 슬롯,
    // 획득(LOOTED)은 반입 공간(백팩 없음 → LOADOUT)에. STASH 자동 덤프가 아니다.
    [Fact]
    public async Task Extract_restores_to_original_loadout_cell_equip_slot_and_looted_to_carried_space()
    {
        var d = await _f.AuthedAs(Delta);
        await ClearLoadout(Delta);
        await ClearEquipment(d);

        // 로드아웃 아이템: 사탕(26, 스택 — 이 플레이어의 다른 테스트와 겹치지 않는 템플릿)을 (3,2)로 반입.
        await GrantStack(Delta, 26, 4);
        await Stash(d);
        await BringStack(d, 26, 4, 3, 2);

        // 장착 아이템: 리볼버(76, WEAPON)를 장착.
        var revolver = await GrantInstance(Delta, 76, 400);
        await Stash(d);
        await Equip(d, EquipSlot.Weapon, revolver);

        (await Start(d)).EnsureSuccessStatusCode();
        // 위험 상태: 로드아웃 스택은 인벤에서 빠지고, 리볼버는 소유 아님(슬롯 비움).
        Assert.Equal(0, await StackQty(d, 26));
        Assert.False(await Owns(d, revolver));
        Assert.Empty((await Equipment(d)).Slots);

        // 레이드 중 획득: 스택(5.56mm 탄약 96).
        (await Loot(d, new AddLootRequest(StashEntryKind.Stack, 96, 15))).EnsureSuccessStatusCode();

        var extracted = await Api<RaidSessionDto>(await Extract(d));
        Assert.Equal(RaidStatus.Extracted, extracted.Data!.Status);

        // 로드아웃 아이템은 원래 칸(3,2)으로 복원(STASH 아님).
        var loadout = await Loadout(d);
        Assert.Contains(loadout.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 26 && p.X == 3 && p.Y == 2);
        Assert.DoesNotContain((await Stash(d)).Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 26);

        // 장착 아이템은 원래 슬롯(WEAPON)으로 복원.
        Assert.Contains((await Equipment(d)).Slots, s => s.Slot == EquipSlot.Weapon && s.InstanceId == revolver);

        // 획득(LOOTED)은 반입 공간(백팩 없음 → LOADOUT)에 배치.
        Assert.Contains(loadout.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 96);

        await ClearEquipment(d);
    }

    // Refinement 2: GET /api/raid/history + GET /api/inventory/ledger 읽기 엔드포인트.
    [Fact]
    public async Task Raid_history_and_item_ledger_read_endpoints_reflect_a_raid()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearLoadout(Echo);
        await GrantStack(Echo, 24, 3);   // 건빵(반입)
        await Stash(e);
        await BringStack(e, 24, 3, 1, 1);

        (await Start(e)).EnsureSuccessStatusCode();
        (await Loot(e, new AddLootRequest(StashEntryKind.Stack, 94, 5))).EnsureSuccessStatusCode(); // .45 ACP 획득
        var extracted = await Api<RaidSessionDto>(await Extract(e));
        var sessionId = extracted.Data!.Id;

        // 이력: 해결된 세션이 아이템 스냅샷(source + qty)과 함께 조회된다.
        var hist = await Api<PagedResult<RaidHistoryEntryDto>>(await e.GetAsync("/api/raid/history?page=1&size=20"));
        Assert.True(hist.Success);
        var entry = hist.Data!.Items.SingleOrDefault(h => h.Id == sessionId);
        Assert.NotNull(entry);
        Assert.Equal(RaidStatus.Extracted, entry!.Status);
        Assert.NotNull(entry.ResolvedAt);
        Assert.Contains(entry.Items, i => i.TemplateId == 24 && i.Source == RaidItemSource.Brought && i.Quantity == 3);
        Assert.Contains(entry.Items, i => i.TemplateId == 94 && i.Source == RaidItemSource.Looted && i.Quantity == 5);

        // 원장: RAID_* 사유가 기록된다.
        var ledger = await Api<PagedResult<ItemLedgerEntryDto>>(await e.GetAsync("/api/inventory/ledger?page=1&size=100"));
        Assert.True(ledger.Success);
        var rows = ledger.Data!.Items;
        Assert.Contains(rows, l => l.Reason == ItemLedgerReason.RaidBrought && l.TemplateId == 24 && l.DeltaQty == -3);
        Assert.Contains(rows, l => l.Reason == ItemLedgerReason.RaidExtract && l.TemplateId == 24 && l.DeltaQty == 3);
        Assert.Contains(rows, l => l.Reason == ItemLedgerReason.RaidLoot && l.TemplateId == 94 && l.DeltaQty == 5);
    }
}

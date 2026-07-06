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
///   - at-risk(위험) = 스태시 밖 전부: 장착 장비(HELMET/ARMOR/WEAPON/BACKPACK/RIG) +
///     장착된 백팩/리그의 중첩 그리드 내용물 + 주머니(POCKETS). STASH는 절대 무관(항상 안전).
///   - StartRaid: 위 대상을 위험으로 잠근다. 장비만 있어도(주머니가 비어도) 반드시 성공해야 한다
///     ("장비를 착용했는데 출격이 안됨" 버그 수정). 반대로 스태시 밖이 전부 비어 있으면 거부(RaidNothingToDeploy).
///   - Extract = 보존: 반입(+획득) 전량이 소유로 복귀 + 원위치(슬롯/컨테이너/주머니)로 정확히 복원, 총량 보존.
///   - Die = 위험만 소실: 반입/획득 소실, 스태시(안전) 무관, 손실은 원장 기록.
///   - 플레이어당 ACTIVE 레이드 1개(두 번째 StartRaid → RaidActive).
///   - 원자성: 정산 도중 실패 시 전량 롤백(best-effort 폴트 인젝션).
/// 레이드 전용 시드 플레이어(Delta/Echo/Foxtrot)를 써서 다른 테스트와 완전 격리한다.
/// </summary>
[Collection("market")]
public class RaidTests(MarketAppFixture f)
{
    private readonly MarketAppFixture _f = f;

    // GUN(equip_slot=WEAPON) 템플릿 — MELEE와 달리 장착 가능해 EQUIP-origin at-risk 테스트에 쓴다.
    private const int Pistol = 74, Revolver = 76;
    private const int Backpack = 106;

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

    /// <summary>테스트 격리: 잔존 POCKETS/CONTAINER 배치를 지운다(익스트랙션이 원위치로 복원하므로
    /// 공유 플레이어의 위험 컨테이너가 테스트 간 누적된다). 소유는 유지되어 다음 GET /api/stash에서 STASH로 정합화된다.</summary>
    private async Task ClearAtRisk(Guid player)
    {
        await using var db = new NpgsqlConnection(_f.ConnString);
        await db.OpenAsync();
        await db.ExecuteAsync(
            "DELETE FROM stash_placement WHERE player_id = @p AND container IN ('POCKETS','CONTAINER')",
            new { p = player });
    }

    private static async Task ClearEquipment(HttpClient c)
    {
        foreach (var s in (await Equipment(c)).Slots)
            (await c.PostAsJsonAsync("/api/equipment/unequip", new UnequipRequest(s.Slot), Json)).EnsureSuccessStatusCode();
    }

    private static async Task<StashDto> Pockets(HttpClient c)
        => (await Api<StashDto>(await c.GetAsync("/api/stash/pockets"))).Data!;

    private static async Task<InventoryDto> Inv(HttpClient c)
        => (await Api<InventoryDto>(await c.GetAsync("/api/inventory"))).Data!;

    private static async Task<int> StackQty(HttpClient c, int templateId)
        => (await Inv(c)).Stacks.FirstOrDefault(s => s.TemplateId == templateId)?.Quantity ?? 0;

    private static async Task<bool> Owns(HttpClient c, Guid instanceId)
        => (await Inv(c)).Instances.Any(i => i.Id == instanceId);

    private static Task<HttpResponseMessage> Move(HttpClient c, MoveStashItemRequest req)
        => c.PostAsJsonAsync("/api/stash/move", req, Json);

    /// <summary>스택을 STASH에서 POCKETS의 (x,0)으로 반입(전량 이동).</summary>
    private static async Task BringStackToPockets(HttpClient c, int templateId, int qty, int x)
    {
        var r = await Move(c, new MoveStashItemRequest(StashEntryKind.Stack, templateId, null, x, 0,
            GridContainer.Stash, GridContainer.Pockets, qty));
        r.EnsureSuccessStatusCode();
    }

    /// <summary>인스턴스를 STASH에서 장착된 컨테이너(백팩/리그) 내부 (x,y)로 반입.</summary>
    private static async Task BringInstanceToContainer(HttpClient c, Guid id, Guid containerInstanceId, int x, int y)
    {
        var r = await Move(c, new MoveStashItemRequest(StashEntryKind.Instance, null, id, x, y,
            GridContainer.Stash, GridContainer.Container, null, null, containerInstanceId));
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

    private static async Task<Guid> Equip(HttpClient c, EquipSlot slot, Guid id)
    {
        var r = await Api<EquipmentDto>(await c.PostAsJsonAsync("/api/equipment/equip", new EquipRequest(slot, id), Json));
        Assert.True(r.Success);
        return id;
    }

    // ----------------------------------------------------------------------

    // StartRaid: 주머니 아이템 + 장착 장비가 세션(위험)으로 이동하고, 인벤에서 빠져 판매/이동 불가가 된다.
    [Fact]
    public async Task StartRaid_locks_pockets_and_equipped_items()
    {
        var d = await _f.AuthedAs(Delta);
        await ClearAtRisk(Delta);
        await ClearEquipment(d);

        await GrantStack(Delta, 25, 8);              // MRE(스택, 주머니로 반입)
        var pistol = await GrantInstance(Delta, Pistol, 300); // 마카로프 권총(WEAPON 장착)
        await Stash(d);                              // STASH 자동 배치

        await BringStackToPockets(d, 25, 8, 0);
        await Equip(d, EquipSlot.Weapon, pistol);

        Assert.Equal(8, await StackQty(d, 25));       // 반입은 소유 인벤을 줄이지 않음(컨테이너 이동)
        Assert.True(await Owns(d, pistol));           // 장착 중에도 소유는 유지

        var started = await Api<RaidSessionDto>(await Start(d));
        Assert.True(started.Success);
        Assert.Equal(RaidStatus.Active, started.Data!.Status);
        Assert.Contains(started.Data.Items, i => i.Kind == StashEntryKind.Stack && i.TemplateId == 25
            && i.Quantity == 8 && i.Source == RaidItemSource.Brought);
        Assert.Contains(started.Data.Items, i => i.Kind == StashEntryKind.Instance && i.InstanceId == pistol
            && i.Source == RaidItemSource.Brought);

        // 위험 아이템은 인벤/장비에서 사라져 판매/배치 불가.
        Assert.Equal(0, await StackQty(d, 25));
        Assert.False(await Owns(d, pistol));
        Assert.Empty((await Equipment(d)).Slots);

        // 판매 시도: 유니크는 소유 아님 → 거부.
        var sell = await Api<PlaceOrderResult>(await d.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Sell, Pistol, 500, 1, pistol), Json));
        Assert.False(sell.Success);
        // 스택은 재고 0 → 매도 에스크로 실패.
        var sellStack = await Api<PlaceOrderResult>(await d.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Sell, 25, 10, 1), Json));
        Assert.False(sellStack.Success);
        Assert.Equal(ErrorCode.InsufficientQuantity, sellStack.Error!.Code);

        // 배치(이동) 시도: 소유 아님 → PlacementInvalid.
        var mv = await Move(d, new MoveStashItemRequest(StashEntryKind.Instance, null, pistol, 0, 0));
        Assert.Equal(HttpStatusCode.BadRequest, mv.StatusCode);
        Assert.Equal(ErrorCode.PlacementInvalid, (await Api<StashDto>(mv)).Error!.Code);

        // 정리: 탈출로 원복(다음 테스트에 ACTIVE 세션을 남기지 않음).
        (await Extract(d)).EnsureSuccessStatusCode();
    }

    // StartRaid는 장착 장비만 있어도(주머니가 비어 있어도) 반드시 성공해야 한다
    // ("장비를 착용했는데 출격이 안됨" 버그 수정 회귀 테스트).
    [Fact]
    public async Task StartRaid_succeeds_with_equipment_only_and_empty_pockets()
    {
        var d = await _f.AuthedAs(Delta);
        await ClearAtRisk(Delta);
        await ClearEquipment(d);

        var helmet = await GrantInstance(Delta, 103, 120); // GEAR: 전투 헬멧
        await Stash(d);
        await Equip(d, EquipSlot.Helmet, helmet);

        // 주머니는 비어 있다 — 그래도 장비만으로 출격은 성공해야 한다.
        Assert.Empty((await Pockets(d)).Placements);

        var started = await Api<RaidSessionDto>(await Start(d));
        Assert.True(started.Success);
        Assert.Single(started.Data!.Items, i => i.InstanceId == helmet);

        (await Die(d)).EnsureSuccessStatusCode(); // 정리(장비 소실 — 다음 테스트가 새로 지급)
    }

    // StartRaid는 스태시 밖이 전부 비어 있으면(장비도 없고 주머니도 비면) 거부되어야 한다.
    [Fact]
    public async Task StartRaid_rejected_when_nothing_at_risk()
    {
        var d = await _f.AuthedAs(Delta);
        await ClearAtRisk(Delta);
        await ClearEquipment(d);

        Assert.Empty((await Pockets(d)).Placements);
        Assert.Empty((await Equipment(d)).Slots);

        var res = await Start(d);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(ErrorCode.RaidNothingToDeploy, (await Api<RaidSessionDto>(res)).Error!.Code);
    }

    // Extract = 보존: 주머니 + 장착된 백팩 내용물(중첩) + 획득 전량이 소유/원위치로 복귀, 총량 보존.
    // 획득(LOOTED)은 반입 공간 우선순위(장착된 백팩 중첩 → 주머니 → STASH)를 따른다.
    [Fact]
    public async Task Extract_conserves_brought_and_looted_items_and_restores_in_place()
    {
        var d = await _f.AuthedAs(Delta);
        await ClearAtRisk(Delta);
        await ClearEquipment(d);

        await GrantStack(Delta, 22, 6);                 // 꿀단지(스택) — 주머니로 반입
        var backpackId = await GrantInstance(Delta, Backpack, 100); // 배낭(중첩 컨테이너, 5×5)
        var axe = await GrantInstance(Delta, 59, 150);   // 소방도끼(유니크, MELEE 1×3 — 장착 불가라 배낭에 넣어야 위험)
        await Stash(d);

        var beforeStack22 = await StackQty(d, 22);

        await BringStackToPockets(d, 22, 6, 0);
        await Equip(d, EquipSlot.Backpack, backpackId);
        await Stash(d); // 배낭 장착 후 도끼가 여전히 STASH에 있는지 재동기화
        await BringInstanceToContainer(d, axe, backpackId, 0, 0);

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

        // 보존: 반입 스택 원복, 반입 유니크(배낭+도끼) 소유 복귀.
        Assert.Equal(beforeStack22, await StackQty(d, 22));
        Assert.True(await Owns(d, backpackId));
        Assert.True(await Owns(d, axe));
        // 획득분이 소유로 추가.
        Assert.Equal(30, await StackQty(d, 93));
        Assert.True(await Owns(d, lootedKatana));

        // 익스트랙션 시맨틱: 반입(BROUGHT) 아이템은 원위치로 그대로 복원된다(STASH 덤프 아님).
        var pockets = await Pockets(d);
        Assert.Contains(pockets.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 22 && p.X == 0 && p.Y == 0);

        var eq = await Equipment(d);
        Assert.Contains(eq.Slots, s => s.Slot == EquipSlot.Backpack && s.InstanceId == backpackId);
        var nested = eq.Containers.Single(c => c.ContainerInstanceId == backpackId);
        Assert.Contains(nested.Placements, p => p.InstanceId == axe && p.X == 0 && p.Y == 0);

        // 획득(LOOTED)은 반입 공간 우선순위(장착된 백팩 중첩이 첫 순위 — 방금 도끼가 (0,0)-(0,2)를
        // 차지했지만 5×5 안에 여유가 있다)에 first-fit으로 들어간다. STASH에는 덤프되지 않는다.
        Assert.Contains(nested.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 93);
        Assert.Contains(nested.Placements, p => p.InstanceId == lootedKatana);
        var stash = await Stash(d);
        Assert.DoesNotContain(stash.Placements, p => p.InstanceId == axe || p.InstanceId == backpackId);
        Assert.DoesNotContain(stash.Placements, p => p.TemplateId == 93);

        await ClearEquipment(d);
    }

    // Die = 위험만 소실: 주머니+장착 무기 소실, 스태시(안전) 무관.
    // item_ledger는 세션의 소유량 순변화를 대칭 회계한다(M4): 사망 시 RaidLoss를 남기지 않는다 —
    // 반입분은 RaidBrought(-)로 이미 손실이 회계됐고(재차감=이중차감), 전리품은 사전 credit이 없어
    // RaidLoss(-)만 남기면 유령 음수이기 때문. 손실 감사는 raid_session(DIED)+raid_session_item이 보유.
    [Fact]
    public async Task Die_destroys_at_risk_only_and_stash_is_untouched()
    {
        var d = await _f.AuthedAs(Delta);
        await ClearAtRisk(Delta);
        await ClearEquipment(d);

        await GrantStack(Delta, 21, 5);   // 땅콩버터 — STASH에 남길 안전 아이템(반입 안 함)
        await GrantStack(Delta, 12, 7);   // 쌀 — 주머니로 반입(소실 대상)
        var pistol = await GrantInstance(Delta, Pistol, 300); // 마카로프 권총 — 장착(소실 대상)
        await Stash(d);

        var safeBefore = await StackQty(d, 21);
        Assert.Equal(7, await StackQty(d, 12));

        await BringStackToPockets(d, 12, 7, 0);
        await Equip(d, EquipSlot.Weapon, pistol);
        var started = await Api<RaidSessionDto>(await Start(d));
        var sessionId = started.Data!.Id;
        (await Loot(d, new AddLootRequest(StashEntryKind.Stack, 94, 10))).EnsureSuccessStatusCode(); // 획득분

        var died = await Api<RaidSessionDto>(await Die(d));
        Assert.True(died.Success);
        Assert.Equal(RaidStatus.Died, died.Data!.Status);

        // 반입/획득 전량 소실.
        Assert.Equal(0, await StackQty(d, 12));
        Assert.False(await Owns(d, pistol));
        Assert.Empty((await Equipment(d)).Slots);
        Assert.Equal(0, await StackQty(d, 94)); // 획득 스택도 소실(소유로 오지 않음)

        // 스태시(안전)는 무관 — 반입하지 않은 땅콩버터는 그대로.
        Assert.Equal(safeBefore, await StackQty(d, 21));
        var stash = await Stash(d);
        Assert.Contains(stash.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 21);

        await using var db = new NpgsqlConnection(_f.ConnString);
        await db.OpenAsync();

        // 대칭화(M4): 사망 정산은 RaidLoss 원장을 남기지 않는다.
        var lossCount = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM item_ledger WHERE player_id = @p AND reason = 'RAID_LOSS' AND ref_id = @s",
            new { p = Delta, s = sessionId });
        Assert.Equal(0, lossCount);

        // 불변식: 이 세션의 ledger delta 합 == 세션이 플레이어 소유량에 준 순변화.
        //   반입 스택 #12: RaidBrought -7 / 반입 유니크 pistol: RaidBrought -1 / 획득 #94: 무기록(0) → 합 -8.
        var sessionDelta = await db.ExecuteScalarAsync<long>(
            "SELECT COALESCE(SUM(delta_qty), 0) FROM item_ledger WHERE player_id = @p AND ref_id = @s",
            new { p = Delta, s = sessionId });
        Assert.Equal(-8, sessionDelta);

        // 유령 음수 없음: 전리품(#94)은 소유한 적이 없으므로 원장 행이 아예 없다.
        var lootRows = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM item_ledger WHERE player_id = @p AND ref_id = @s AND template_id = 94",
            new { p = Delta, s = sessionId });
        Assert.Equal(0, lootRows);
    }

    // 플레이어당 ACTIVE 레이드는 1개 — 두 번째 StartRaid는 RaidActive(409).
    [Fact]
    public async Task Second_start_raid_is_rejected_while_active()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);
        await GrantStack(Echo, 24, 3); // 건빵
        await Stash(e);
        await BringStackToPockets(e, 24, 3, 0);

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

    // B(raid): StartedAt/ResolvedAt은 앱 시각이 아니라 DB에서 읽는다. AddLoot가 출격 시각 대신
    // loot 호출 시각을 반환하던 버그 회귀 방지 — AddLoot를 여러 번 해도 StartedAt은 최초 출격 시각으로 불변.
    [Fact]
    public async Task Raid_timestamps_come_from_db_and_startedat_is_stable_across_loot()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);

        await GrantStack(Echo, 24, 3); // 건빵 — 주머니로 반입
        await Stash(e);
        await BringStackToPockets(e, 24, 3, 0);

        var started = await Api<RaidSessionDto>(await Start(e));
        Assert.True(started.Success);
        var startedAt = started.Data!.StartedAt;
        Assert.Null(started.Data.ResolvedAt);

        // AddLoot 두 번 — StartedAt은 최초 출격 시각으로 불변(loot 시각으로 바뀌지 않음).
        var loot1 = await Api<RaidSessionDto>(await Loot(e, new AddLootRequest(StashEntryKind.Stack, 94, 2)));
        Assert.Equal(startedAt, loot1.Data!.StartedAt);
        var loot2 = await Api<RaidSessionDto>(await Loot(e, new AddLootRequest(StashEntryKind.Stack, 94, 1)));
        Assert.Equal(startedAt, loot2.Data!.StartedAt);

        // Extract: ResolvedAt이 DB에서 채워지고 StartedAt은 여전히 불변, Started <= Resolved.
        var ex = await Api<RaidSessionDto>(await Extract(e));
        Assert.True(ex.Success);
        Assert.Equal(startedAt, ex.Data!.StartedAt);
        Assert.NotNull(ex.Data.ResolvedAt);
        Assert.True(ex.Data.StartedAt <= ex.Data.ResolvedAt);
    }

    // 원자성(best-effort 폴트 인젝션): 정산 도중 실패하면 전량 롤백된다.
    // 주머니 수량과 inventory_stack을 어긋나게(외부 변조) 만들어 StartRaid 스택 가드를 실패시키고,
    // 같은 트랜잭션에서 먼저 INSERT된 raid_session이 롤백되는지(ACTIVE 세션 미생성) 검증한다.
    [Fact]
    public async Task StartRaid_rolls_back_entirely_on_mid_settlement_failure()
    {
        var fx = await _f.AuthedAs(Foxtrot);
        await ClearAtRisk(Foxtrot);
        await ClearEquipment(fx);
        await GrantStack(Foxtrot, 11, 5);            // 라면
        var pistol = await GrantInstance(Foxtrot, Pistol, 300); // 마카로프 권총(장착)
        await Stash(fx);
        await BringStackToPockets(fx, 11, 5, 0);
        await Equip(fx, EquipSlot.Weapon, pistol);

        // 폴트 인젝션: 주머니는 5개인데 소유 재고를 1로 낮춰 StartRaid 스택 가드를 실패시킨다.
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

        // 유니크(장착 무기)는 여전히 소유(instance 루프까지 도달하지 않고 롤백), 원장/세션아이템도 미기록.
        Assert.True(await Owns(fx, pistol));
        Assert.Contains((await Equipment(fx)).Slots, s => s.InstanceId == pistol);
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

        await ClearEquipment(fx);
    }

    // Refinement: Extract는 원위치로 정확히 복원한다 — 주머니 아이템은 원래 칸, 장착 아이템은 원래 슬롯,
    // 획득(LOOTED)은 반입 공간(백팩 없음 → 주머니 → STASH)에 순서대로. STASH 자동 덤프가 아니다.
    [Fact]
    public async Task Extract_restores_to_original_pockets_cell_equip_slot_and_looted_to_carried_space()
    {
        var d = await _f.AuthedAs(Delta);
        await ClearAtRisk(Delta);
        await ClearEquipment(d);

        // 주머니 아이템: 사탕(26, 스택)을 (2,0)으로 반입.
        await GrantStack(Delta, 26, 4);
        await Stash(d);
        await BringStackToPockets(d, 26, 4, 2);

        // 장착 아이템: 리볼버(76, WEAPON)를 장착.
        var revolver = await GrantInstance(Delta, Revolver, 400);
        await Stash(d);
        await Equip(d, EquipSlot.Weapon, revolver);

        (await Start(d)).EnsureSuccessStatusCode();
        // 위험 상태: 주머니 스택은 인벤에서 빠지고, 리볼버는 소유 아님(슬롯 비움).
        Assert.Equal(0, await StackQty(d, 26));
        Assert.False(await Owns(d, revolver));
        Assert.Empty((await Equipment(d)).Slots);

        // 레이드 중 획득: 스택(5.56mm 탄약 96) — 백팩이 없으므로 주머니 여유 칸에 들어간다.
        (await Loot(d, new AddLootRequest(StashEntryKind.Stack, 96, 15))).EnsureSuccessStatusCode();

        var extracted = await Api<RaidSessionDto>(await Extract(d));
        Assert.Equal(RaidStatus.Extracted, extracted.Data!.Status);

        // 주머니 아이템은 원래 칸(2,0)으로 복원(STASH 아님).
        var pockets = await Pockets(d);
        Assert.Contains(pockets.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 26 && p.X == 2 && p.Y == 0);
        Assert.DoesNotContain((await Stash(d)).Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 26);

        // 장착 아이템은 원래 슬롯(WEAPON)으로 복원.
        Assert.Contains((await Equipment(d)).Slots, s => s.Slot == EquipSlot.Weapon && s.InstanceId == revolver);

        // 획득(LOOTED)은 반입 공간(백팩 없음 → 주머니 나머지 칸)에 배치.
        Assert.Contains(pockets.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 96);

        await ClearEquipment(d);
    }

    // Refinement: GET /api/raid/history + GET /api/inventory/ledger 읽기 엔드포인트.
    [Fact]
    public async Task Raid_history_and_item_ledger_read_endpoints_reflect_a_raid()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);
        await GrantStack(Echo, 24, 3);   // 건빵(반입)
        await Stash(e);
        await BringStackToPockets(e, 24, 3, 1);

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

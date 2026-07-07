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
        // 잔존 ACTIVE 세션을 닫는다 — 레이드 중 스태시/장비 변이가 잠기므로(A-1),
        // 앞 테스트가 남긴 진행 중 세션이 있으면 다음 테스트의 셋업(unequip/move)이 RaidActive로 막힌다.
        await db.ExecuteAsync(
            "UPDATE raid_session SET status = 'DIED', resolved_at = now() WHERE player_id = @p AND status = 'ACTIVE'",
            new { p = player });
        await db.ExecuteAsync(
            "DELETE FROM stash_placement WHERE player_id = @p AND container IN ('POCKETS','CONTAINER')",
            new { p = player });
        // 출격 수수료(캡 싱크) 도입 후 반복 출격이 잔액을 소진하므로, 레이드 테스트 셋업마다 잔액을
        // 넉넉히 리셋해 수수료에 걸리지 않게 한다. (수수료 자체 검증 테스트는 이 뒤에 잔액을 덮어쓴다.)
        await db.ExecuteAsync(
            "UPDATE wallet SET balance = 100000 WHERE player_id = @p", new { p = player });
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
    private static Task<HttpResponseMessage> Start(HttpClient c, RaidZone zone)
        => c.PostAsJsonAsync("/api/raid/start", new StartRaidRequest(zone), Json);
    private static Task<HttpResponseMessage> Extract(HttpClient c) => c.PostAsync("/api/raid/extract", null);
    private static Task<HttpResponseMessage> Die(HttpClient c) => c.PostAsync("/api/raid/die", null);
    // 루팅(서버 드롭): 바디 없음. 서버가 세션 존의 rarity 가중치로 무엇을·얼마나 드롭할지 결정한다.
    private static Task<HttpResponseMessage> Scavenge(HttpClient c) => c.PostAsync("/api/raid/loot", null);

    // extract 확률 사망을 배제하고 "보존/복원" 불변식만 결정론으로 검증하기 위해 누적 사망확률을 0으로 리셋.
    private async Task ResetDeathChance(Guid player)
    {
        await using var db = new NpgsqlConnection(_f.ConnString);
        await db.OpenAsync();
        await db.ExecuteAsync(
            "UPDATE raid_session SET death_chance_bps = 0 WHERE player_id = @p AND status = 'ACTIVE'",
            new { p = player });
    }

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

        // 배치(이동) 시도: 레이드 중이므로 RaidActive로 잠긴다(A-1 — 소유 검증보다 앞선 가드).
        var mv = await Move(d, new MoveStashItemRequest(StashEntryKind.Instance, null, pistol, 0, 0));
        Assert.Equal(HttpStatusCode.Conflict, mv.StatusCode);
        Assert.Equal(ErrorCode.RaidActive, (await Api<StashDto>(mv)).Error!.Code);

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

        // 레이드 중 획득(전리품): 서버 드롭 여러 번. 무엇이 나올지는 존 가중치로 서버가 결정하므로
        // 반환된 Dropped를 수집해 귀속 여부를 확인한다.
        var drops = new List<RaidSessionItemDto>();
        for (var i = 0; i < 3; i++)
        {
            var lr = await Api<LootResultDto>(await Scavenge(d));
            Assert.True(lr.Success);
            Assert.NotNull(lr.Data!.Dropped);
            drops.Add(lr.Data.Dropped!);
        }

        await ResetDeathChance(Delta); // 보존 불변식만 검증 — 확률 사망 배제
        var extracted = await Api<RaidSessionDto>(await Extract(d));
        Assert.True(extracted.Success);
        Assert.Equal(RaidStatus.Extracted, extracted.Data!.Status);
        Assert.NotNull(extracted.Data.ResolvedAt);

        // 보존: 반입 유니크(배낭+도끼) 소유 복귀.
        Assert.True(await Owns(d, backpackId));
        Assert.True(await Owns(d, axe));
        // 획득분이 소유로 귀속(스택 수량 가산 / 유니크 owner 복원).
        foreach (var drop in drops)
        {
            if (drop.Kind == StashEntryKind.Instance)
                Assert.True(await Owns(d, drop.InstanceId!.Value), $"looted unique {drop.InstanceId} not owned");
            else
                Assert.True(await StackQty(d, drop.TemplateId) >= drop.Quantity, $"looted stack {drop.TemplateId} not credited");
        }

        // 익스트랙션 시맨틱: 반입(BROUGHT) 아이템은 원위치로 그대로 복원된다(STASH 덤프 아님).
        // (반입 스택 22의 원위치 복원 = 총량 보존의 증거 — 획득 드롭이 같은 템플릿일 수 있어 절대량 대신 원위치로 확인)
        var pockets = await Pockets(d);
        Assert.Contains(pockets.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 22 && p.X == 0 && p.Y == 0);
        Assert.True(await StackQty(d, 22) >= beforeStack22); // 반입분 복원(+ 우연히 같은 템플릿 드롭이면 그 이상)

        var eq = await Equipment(d);
        Assert.Contains(eq.Slots, s => s.Slot == EquipSlot.Backpack && s.InstanceId == backpackId);
        var nested = eq.Containers.Single(c => c.ContainerInstanceId == backpackId);
        Assert.Contains(nested.Placements, p => p.InstanceId == axe && p.X == 0 && p.Y == 0);
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
        var dropped = (await Api<LootResultDto>(await Scavenge(d))).Data!.Dropped!; // 획득분(서버 드롭)

        var died = await Api<RaidSessionDto>(await Die(d));
        Assert.True(died.Success);
        Assert.Equal(RaidStatus.Died, died.Data!.Status);

        // 반입 전량 소실.
        Assert.Equal(0, await StackQty(d, 12));
        Assert.False(await Owns(d, pistol));
        Assert.Empty((await Equipment(d)).Slots);
        // 획득분도 소실 — 유니크 드롭이면 소유되지 않는다(스택 드롭은 기존 소유와 템플릿이 겹칠 수 있어
        // 절대량 대신 아래 ledger 불변식으로 미귀속을 보장한다).
        if (dropped.Kind == StashEntryKind.Instance)
            Assert.False(await Owns(d, dropped.InstanceId!.Value));

        // 스태시(안전)는 무관 — 반입하지 않은 땅콩버터(21)의 원위치 배치가 그대로 남는다.
        Assert.True(safeBefore >= 5);
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
        //   반입 스택 #12: RaidBrought -7 / 반입 유니크 pistol: RaidBrought -1 / 획득(LOOTED): die 시 무기록(0) → 합 -8.
        //   서버가 무엇을 드롭했든 LOOTED는 사망 시 원장을 남기지 않으므로 합은 반입분(-8)으로 고정된다.
        var sessionDelta = await db.ExecuteScalarAsync<long>(
            "SELECT COALESCE(SUM(delta_qty), 0) FROM item_ledger WHERE player_id = @p AND ref_id = @s",
            new { p = Delta, s = sessionId });
        Assert.Equal(-8, sessionDelta);

        // 유령 음수 없음: 획득(LOOTED)분은 소유한 적이 없으므로 이 세션 원장은 RaidBrought 2건뿐이다.
        var rowCount = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM item_ledger WHERE player_id = @p AND ref_id = @s",
            new { p = Delta, s = sessionId });
        Assert.Equal(2, rowCount); // 반입 스택 + 반입 유니크만
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

        // 루팅 두 번 — StartedAt은 최초 출격 시각으로 불변(loot 시각으로 바뀌지 않음).
        var loot1 = await Api<LootResultDto>(await Scavenge(e));
        Assert.Equal(startedAt, loot1.Data!.Session.StartedAt);
        var loot2 = await Api<LootResultDto>(await Scavenge(e));
        Assert.Equal(startedAt, loot2.Data!.Session.StartedAt);

        await ResetDeathChance(Echo); // 시각 검증이 목적 — 확률 사망 배제
        // Extract: ResolvedAt이 DB에서 채워지고 StartedAt은 여전히 불변, Started <= Resolved.
        var ex = await Api<RaidSessionDto>(await Extract(e));
        Assert.True(ex.Success);
        Assert.Equal(startedAt, ex.Data!.StartedAt);
        Assert.NotNull(ex.Data.ResolvedAt);
        Assert.True(ex.Data.StartedAt <= ex.Data.ResolvedAt);
    }

    // BUG D(서버 드롭 버전): 서버가 드롭 수량을 결정하므로 한 스택 상한(max_stack) 초과가 원천 불가하다.
    // 여러 번 루팅해 나온 모든 스택 드롭의 수량이 그 템플릿 max_stack 이하임을 확인한다.
    [Fact]
    public async Task Server_drop_quantity_never_exceeds_max_stack()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);
        await GrantStack(Echo, 24, 3);
        await Stash(e);
        await BringStackToPockets(e, 24, 3, 0);
        (await Start(e, RaidZone.High)).EnsureSuccessStatusCode(); // 고위험 존 — 다양한 등급 드롭

        await using var db = new NpgsqlConnection(_f.ConnString);
        await db.OpenAsync();

        for (var i = 0; i < 12; i++)
        {
            var drop = (await Api<LootResultDto>(await Scavenge(e))).Data!.Dropped!;
            if (drop.Kind != StashEntryKind.Stack) continue;
            var maxStack = await db.ExecuteScalarAsync<int>(
                "SELECT max_stack FROM item_template WHERE id = @id", new { id = drop.TemplateId });
            Assert.InRange(drop.Quantity, 1, maxStack);
        }

        await ResetDeathChance(Echo);
        (await Die(e)).EnsureSuccessStatusCode(); // 정리
    }

    // A-1: 레이드 ACTIVE 중 스태시/장비 변이는 RaidActive(409)로 잠긴다. 잠그지 않으면 비운 슬롯/칸에
    // 예비품이 들어가 Extract 원위치 복원이 고유 제약과 충돌해 500 + 세션 소프트락이 났다.
    [Fact]
    public async Task Stash_and_equipment_mutations_are_locked_during_active_raid()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);

        var pistol = await GrantInstance(Echo, Pistol, 300);
        await GrantStack(Echo, 24, 3);
        await Stash(e);
        await BringStackToPockets(e, 24, 3, 0);
        await Equip(e, EquipSlot.Weapon, pistol);
        (await Start(e)).EnsureSuccessStatusCode();

        // 예비 무기를 STASH에 두고 레이드 중 착용 시도 → 409 RaidActive.
        var spare = await GrantInstance(Echo, Pistol, 250);
        await Stash(e);
        var eq = await e.PostAsJsonAsync("/api/equipment/equip", new EquipRequest(EquipSlot.Weapon, spare), Json);
        Assert.Equal(HttpStatusCode.Conflict, eq.StatusCode);
        Assert.Equal(ErrorCode.RaidActive, (await Api<EquipmentDto>(eq)).Error!.Code);

        // 이동도 레이드 중 409.
        var mv = await e.PostAsJsonAsync("/api/stash/move",
            new MoveStashItemRequest(StashEntryKind.Stack, 24, null, 2, 0), Json);
        Assert.Equal(HttpStatusCode.Conflict, mv.StatusCode);
        Assert.Equal(ErrorCode.RaidActive, (await Api<StashDto>(mv)).Error!.Code);

        // 핵심 회귀: 변이가 잠겨 있으므로 Extract가 500 없이 성공하고 원위치로 복원된다.
        var ex = await Api<RaidSessionDto>(await Extract(e));
        Assert.True(ex.Success);
        Assert.Equal(RaidStatus.Extracted, ex.Data!.Status);

        // 탈출 후에는 변이가 다시 허용된다(잠금 해제).
        var un = await e.PostAsJsonAsync("/api/equipment/unequip", new UnequipRequest(EquipSlot.Weapon), Json);
        Assert.Equal(HttpStatusCode.OK, un.StatusCode);
    }

    // fun#1: 사망확률 0이면 extract는 확정 생존, ≥100%면 확정 사망(경계 결정론). GET에 필드 노출.
    [Fact]
    public async Task Extract_death_roll_is_deterministic_at_bounds()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);
        await GrantStack(Echo, 24, 3);
        await Stash(e);
        var before = await StackQty(e, 24);                  // 다른 테스트 잔여 포함 총량(델타로 검증)
        await BringStackToPockets(e, 24, 3, 0);
        var started = await Api<RaidSessionDto>(await Start(e));
        Assert.True(started.Success);
        Assert.True(started.Data!.DeathChanceBps > 0);       // 출격 시 존 기본 floor(반입 리스크 상시화)
        Assert.NotNull(started.Data.DeadlineAt);              // 마감 존재

        // death_chance_bps=10000(100%)으로 조작 → extract 확정 사망(at-risk 소실).
        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            await db.ExecuteAsync(
                "UPDATE raid_session SET death_chance_bps = 10000 WHERE player_id = @p AND status = 'ACTIVE'",
                new { p = Echo });
        }
        var died = await Api<RaidSessionDto>(await Extract(e));
        Assert.True(died.Success);
        Assert.Equal(RaidStatus.Died, died.Data!.Status);    // 탈출 시도했으나 확률로 사망
        Assert.Equal(before - 3, await StackQty(e, 24));     // 반입한 3개 소실(미복귀)
    }

    // fun#1: 확률 0 상태의 extract는 확정 생존(보존).
    [Fact]
    public async Task Extract_with_zero_death_chance_survives()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);
        await GrantStack(Echo, 24, 4);
        await Stash(e);
        var before = await StackQty(e, 24);
        await BringStackToPockets(e, 24, 4, 0);
        (await Start(e)).EnsureSuccessStatusCode();

        await ResetDeathChance(Echo); // 존 기본 floor를 0으로 덮어 확정 생존 검증(보존 불변식이 목적)
        var ex = await Api<RaidSessionDto>(await Extract(e));   // chance=0 → 확정 생존
        Assert.Equal(RaidStatus.Extracted, ex.Data!.Status);
        Assert.Equal(before, await StackQty(e, 24));           // 반입 스택 전량 귀속(보존)
    }

    // fun#1: 마감(deadline) 초과 후 extract/loot는 탈출 실패=사망으로 정산된다(lazy expiry).
    [Fact]
    public async Task Expired_deadline_forces_death_on_extract()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);
        await GrantStack(Echo, 24, 3);
        await Stash(e);
        var before = await StackQty(e, 24);
        await BringStackToPockets(e, 24, 3, 0);
        (await Start(e)).EnsureSuccessStatusCode();

        // 마감을 과거로 조작 → 만료.
        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            await db.ExecuteAsync(
                "UPDATE raid_session SET deadline_at = now() - interval '1 second' WHERE player_id = @p AND status = 'ACTIVE'",
                new { p = Echo });
        }

        // 만료 후 extract → 탈출 실패=사망.
        var ex = await Api<RaidSessionDto>(await Extract(e));
        Assert.True(ex.Success);
        Assert.Equal(RaidStatus.Died, ex.Data!.Status);
        Assert.Equal(before - 3, await StackQty(e, 24));       // 반입한 3개 소실
    }

    // F-1: 레이드 시작과 스태시/장비 변이가 grain 경계를 넘어 동시에 들어와도(StashGrain↔RaidSessionGrain은
    // 별개 grain이라 Orleans가 서로 직렬화 못 함) advisory 락으로 DB에서 직렬화돼, Extract 원위치 복원이
    // 고유 제약과 충돌하는 500(Unknown) 소프트락이 발생하지 않는다.
    [Fact]
    public async Task Concurrent_start_and_equip_never_soft_locks_extract()
    {
        var e = await _f.AuthedAs(Echo);
        for (var round = 0; round < 8; round++)
        {
            await ClearAtRisk(Echo);   // 이전 라운드 세션·위험 배치 정리
            await ClearEquipment(e);
            var w1 = await GrantInstance(Echo, Pistol, 300);
            var w2 = await GrantInstance(Echo, Pistol, 300); // 예비 무기(STASH)
            await Stash(e);
            await Equip(e, EquipSlot.Weapon, w1); // 반입 대상 착용

            // 동시: Start(w1을 at-risk로 걷어가 슬롯 비움) vs Equip(빈 슬롯에 w2 착용 시도).
            var startTask = Start(e);
            var equipTask = e.PostAsJsonAsync("/api/equipment/equip", new EquipRequest(EquipSlot.Weapon, w2), Json);
            await Task.WhenAll(startTask, equipTask);

            // 핵심: Extract가 원위치 복원 충돌로 500(Unknown) 소프트락에 빠지지 않는다.
            var ext = await Extract(e);
            Assert.NotEqual(HttpStatusCode.InternalServerError, ext.StatusCode);
        }
    }

    // F-2: 범위 밖 zone 정수({"zone":99})는 ValidationError로 거부된다(JsonStringEnumConverter가
    // 기본적으로 정수를 바인딩하므로 서버가 Enum.IsDefined로 방어).
    [Fact]
    public async Task Start_raid_rejects_unknown_zone()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);
        await GrantStack(Echo, 24, 3);
        await Stash(e);
        await BringStackToPockets(e, 24, 3, 0);

        var res = await e.PostAsync("/api/raid/start",
            new StringContent("{\"zone\":99}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(ErrorCode.ValidationError, (await Api<RaidSessionDto>(res)).Error!.Code);
    }

    // fun#5(recurring 싱크): 출격마다 존별 수수료가 잔액에서 차감되고 wallet_ledger(RAID_ENTRY_FEE)에 기록된다.
    [Fact]
    public async Task Start_raid_charges_zone_entry_fee()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);
        await GrantStack(Echo, 24, 3);
        await Stash(e);
        await BringStackToPockets(e, 24, 3, 0);

        // 잔액을 알려진 값으로.
        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            await db.ExecuteAsync("UPDATE wallet SET balance = 5000 WHERE player_id = @p", new { p = Echo });
        }

        // Med 존 출격 → 수수료 400 차감(기본값).
        (await Start(e, RaidZone.Med)).EnsureSuccessStatusCode();

        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            var balance = await db.ExecuteScalarAsync<long>(
                "SELECT balance FROM wallet WHERE player_id = @p", new { p = Echo });
            Assert.Equal(4600, balance); // 5000 - 400
            var feeRows = await db.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM wallet_ledger WHERE player_id = @p AND reason = 'RAID_ENTRY_FEE'",
                new { p = Echo });
            Assert.True(feeRows >= 1);
        }
        (await Die(e)).EnsureSuccessStatusCode(); // 정리
    }

    // fun#5: 수수료를 낼 캡이 부족하면 출격이 InsufficientFunds로 거부되고 세션이 생성되지 않는다(롤백).
    [Fact]
    public async Task Start_raid_rejected_when_cannot_afford_entry_fee()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);
        await GrantStack(Echo, 24, 3);
        await Stash(e);
        await BringStackToPockets(e, 24, 3, 0);

        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            await db.ExecuteAsync("UPDATE wallet SET balance = 50 WHERE player_id = @p", new { p = Echo }); // Med 400 미만
        }

        var res = await Start(e, RaidZone.Med);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(ErrorCode.InsufficientFunds, (await Api<RaidSessionDto>(res)).Error!.Code);

        // 세션 미생성(롤백).
        var snap = await Api<RaidSessionDto?>(await e.GetAsync("/api/raid"));
        Assert.True(snap.Data is null || snap.Data.Status != RaidStatus.Active);
    }

    // 파산 온램프: 잔액 0이어도 무료 Scav 존은 출격 가능(유료 존은 거부). 재기 경로.
    [Fact]
    public async Task Scav_zone_deploys_free_even_when_broke()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);
        await GrantStack(Echo, 24, 3);
        await Stash(e);
        await BringStackToPockets(e, 24, 3, 0);

        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            await db.ExecuteAsync("UPDATE wallet SET balance = 0 WHERE player_id = @p", new { p = Echo });
        }

        // 잔액 0 + 유료 존(Med)은 거부.
        var med = await Start(e, RaidZone.Med);
        Assert.Equal(HttpStatusCode.BadRequest, med.StatusCode);
        Assert.Equal(ErrorCode.InsufficientFunds, (await Api<RaidSessionDto>(med)).Error!.Code);

        // Scav는 무료라 출격 성공(온램프).
        var scav = await Api<RaidSessionDto>(await Start(e, RaidZone.Scav));
        Assert.True(scav.Success);
        Assert.Equal(RaidStatus.Active, scav.Data!.Status);

        (await Die(e)).EnsureSuccessStatusCode(); // 정리
    }

    // fun#2 온램프: 진짜 파산자(잔액 0 + 인벤 전무, 순자산 0)는 Scav를 "빈손 출격(반입 0·획득만)"으로
    // 언제나 재기할 수 있다. 유료 존은 반입할 것이 없어 여전히 거부된다.
    [Fact]
    public async Task Scav_empty_handed_onramp_when_broke_and_empty()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);
        await ClearEquipment(e);
        // 완전 파산: 스택·소유 인스턴스·배치 전부 제거 + 잔액 0 → 순자산 0.
        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            await db.ExecuteAsync("DELETE FROM stash_placement WHERE player_id = @p", new { p = Echo });
            await db.ExecuteAsync("DELETE FROM inventory_stack WHERE player_id = @p", new { p = Echo });
            await db.ExecuteAsync("DELETE FROM player_equipment WHERE player_id = @p", new { p = Echo });
            await db.ExecuteAsync("UPDATE item_instance SET owner_player_id = NULL WHERE owner_player_id = @p", new { p = Echo });
            await db.ExecuteAsync("UPDATE wallet SET balance = 0 WHERE player_id = @p", new { p = Echo });
        }

        // 유료 존(Low)은 파산자에게 온램프가 아니다 — 수수료조차 못 내 거부(빈손 온램프는 Scav 전용).
        var low = await Start(e, RaidZone.Low);
        Assert.Equal(HttpStatusCode.BadRequest, low.StatusCode);
        Assert.Equal(ErrorCode.InsufficientFunds, (await Api<RaidSessionDto>(low)).Error!.Code);

        // 빈손 Scav 출격 성공(온램프) — 반입 0이어도 파산+Scav면 허용.
        var scav = await Api<RaidSessionDto>(await Start(e, RaidZone.Scav));
        Assert.True(scav.Success);
        Assert.Equal(RaidStatus.Active, scav.Data!.Status);

        // 획득만 가능: 루팅 후 결정론 탈출로 재기(캡·아이템 확보).
        (await Scavenge(e)).EnsureSuccessStatusCode();
        await ResetDeathChance(Echo);
        (await Extract(e)).EnsureSuccessStatusCode();
    }

    // fun#3 faucet 유계: 순자산이 상한 이상이면 빈손 Scav가 닫힌다(유료 존으로 졸업 강제).
    // 벤더 판매는 인벤→캡 전환일 뿐 순자산 불변이라 sell→재출격 루프로도 온램프를 재개할 수 없다.
    [Fact]
    public async Task Scav_empty_handed_rejected_when_net_worth_above_ceiling()
    {
        var e = await _f.AuthedAs(Echo);
        await ClearAtRisk(Echo);   // 잔액 100000, POCKETS/CONTAINER 비움 → 순자산 ≥ 상한
        await ClearEquipment(e);   // 장비 비움 → at-risk 수집 0(반입할 것 없음)

        // 순자산 100000 ≥ 상한 1000 + 반입 0 → 빈손 Scav도 거부.
        var scav = await Start(e, RaidZone.Scav);
        Assert.Equal(HttpStatusCode.BadRequest, scav.StatusCode);
        Assert.Equal(ErrorCode.RaidNothingToDeploy, (await Api<RaidSessionDto>(scav)).Error!.Code);
    }

    // fun#5: 존 메타 엔드포인트가 전 존의 수수료·사망확률 상승률을 반환하고 고위험일수록 수수료가 크다.
    [Fact]
    public async Task Zones_endpoint_returns_fee_and_death_rate_per_zone()
    {
        var e = await _f.AuthedAs(Echo);
        var zones = await Api<IReadOnlyList<ZoneInfoDto>>(await e.GetAsync("/api/raid/zones"));
        Assert.True(zones.Success);
        Assert.Equal(4, zones.Data!.Count); // Scav/Low/Med/High

        var low = zones.Data.Single(z => z.Zone == RaidZone.Low);
        var high = zones.Data.Single(z => z.Zone == RaidZone.High);
        Assert.True(high.EntryFee > low.EntryFee);                      // 고위험=높은 진입 장벽
        Assert.True(high.DeathChancePerLootBps > low.DeathChancePerLootBps);
        Assert.True(low.BaseDeathBps > 0);                             // 반입 리스크 상시화 — floor>0
        Assert.True(high.BaseDeathBps > low.BaseDeathBps);             // 고위험 존일수록 기본 위험도 큼
    }

    // F-1(인스턴스 이동 경로): 백팩 내용물(유니크)을 STASH로 옮기는 동시에 StartRaid가 그 백팩+내용물을
    // at-risk로 걷어가도, advisory 락으로 DB에서 직렬화돼 Extract 원위치 복원 500(소프트락)이 없어야 한다.
    [Fact]
    public async Task Concurrent_start_and_instance_move_never_soft_locks_extract()
    {
        var e = await _f.AuthedAs(Echo);
        for (var round = 0; round < 6; round++)
        {
            await ClearAtRisk(Echo);
            await ClearEquipment(e);
            var backpack = await GrantInstance(Echo, Backpack, 100);
            var gun = await GrantInstance(Echo, Pistol, 300);
            await Stash(e);
            await Equip(e, EquipSlot.Backpack, backpack);
            await Stash(e); // 백팩 장착 후 재동기화(권총이 STASH에 남아있게)
            await BringInstanceToContainer(e, gun, backpack, 0, 0); // 권총을 백팩(중첩) 안으로

            // 동시: Start(백팩+내용물을 at-risk로 걷어감) vs Move(권총을 STASH로 빼냄).
            var startTask = Start(e);
            var moveTask = Move(e, new MoveStashItemRequest(StashEntryKind.Instance, null, gun, 0, 0));
            await Task.WhenAll(startTask, moveTask);

            // 핵심: Extract가 원위치 복원 충돌로 500(Unknown) 소프트락에 빠지지 않는다.
            var ext = await Extract(e);
            Assert.NotEqual(HttpStatusCode.InternalServerError, ext.StatusCode);
        }
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

        // 유니크(장착 무기)는 여전히 소유(instance 루프까지 도달하지 않고 롤백), RAID_* 원장/세션아이템도 미기록.
        // (셋업 지급의 ADMIN_GRANT 원장은 정상 존재하므로 RAID 사유만 카운트한다.)
        Assert.True(await Owns(fx, pistol));
        Assert.Contains((await Equipment(fx)).Slots, s => s.InstanceId == pistol);
        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            var sessions = await db.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM raid_session WHERE player_id = @p", new { p = Foxtrot });
            Assert.Equal(0, sessions);
            var ledger = await db.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM item_ledger WHERE player_id = @p AND reason LIKE 'RAID%'", new { p = Foxtrot });
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

        // 레이드 중 획득: 서버 드롭 1회(무엇이 나올지는 서버가 결정).
        var drop = (await Api<LootResultDto>(await Scavenge(d))).Data!.Dropped!;

        await ResetDeathChance(Delta); // 원위치 복원 불변식만 검증 — 확률 사망 배제
        var extracted = await Api<RaidSessionDto>(await Extract(d));
        Assert.Equal(RaidStatus.Extracted, extracted.Data!.Status);

        // 주머니 아이템은 원래 칸(2,0)으로 복원(STASH 아님).
        var pockets = await Pockets(d);
        Assert.Contains(pockets.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 26 && p.X == 2 && p.Y == 0);

        // 장착 아이템은 원래 슬롯(WEAPON)으로 복원.
        Assert.Contains((await Equipment(d)).Slots, s => s.Slot == EquipSlot.Weapon && s.InstanceId == revolver);

        // 획득(LOOTED)분은 소유로 귀속된다(유니크는 소유, 스택은 수량 크레딧).
        if (drop.Kind == StashEntryKind.Instance)
            Assert.True(await Owns(d, drop.InstanceId!.Value));
        else
            Assert.True(await StackQty(d, drop.TemplateId) >= drop.Quantity);

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
        var drop = (await Api<LootResultDto>(await Scavenge(e))).Data!.Dropped!; // 서버 드롭(획득)
        await ResetDeathChance(Echo); // 생존 보장 — 이력/원장 검증이 목적
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
        Assert.Contains(entry.Items, i => i.Source == RaidItemSource.Looted && i.TemplateId == drop.TemplateId);

        // 원장: RAID_* 사유가 기록된다(반입 24 debit/복원 credit + 획득 materialize credit).
        var ledger = await Api<PagedResult<ItemLedgerEntryDto>>(await e.GetAsync("/api/inventory/ledger?page=1&size=100"));
        Assert.True(ledger.Success);
        var rows = ledger.Data!.Items;
        Assert.Contains(rows, l => l.Reason == ItemLedgerReason.RaidBrought && l.TemplateId == 24 && l.DeltaQty == -3);
        Assert.Contains(rows, l => l.Reason == ItemLedgerReason.RaidExtract && l.TemplateId == 24 && l.DeltaQty == 3);
        Assert.Contains(rows, l => l.Reason == ItemLedgerReason.RaidLoot && l.TemplateId == drop.TemplateId && l.DeltaQty == drop.Quantity);
    }
}

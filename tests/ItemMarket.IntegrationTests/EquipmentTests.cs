using System.Net;
using System.Net.Http.Json;
using Dapper;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Equipment;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Raid;
using ItemMarket.Contracts.Stash;
using Npgsql;
using Xunit;
using static ItemMarket.IntegrationTests.MarketAppFixture;
using ErrorCode = ItemMarket.Contracts.Common.ErrorCode;

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 장비(equipment) + 중첩 컨테이너(백팩/리그 내부 그리드) 통합테스트. 실제 API+Orleans+Postgres 경로.
///   - 장착: template.equip_slot == slot 검증(불일치/점유 → SlotMismatch).
///   - 중첩 그리드: 아이템을 백팩 내부로 이동(반입)/재배치/반출 — 경계·겹침 서버 권위 검증.
///   - 레이드 at-risk: 장착 아이템 + 백팩 내용물이 위험 → Die 소실, Extract 복귀(보존).
///   - 전리품(유니크): 유니크 템플릿을 loot하면 인스턴스가 materialize(origin=RAID)되고 Extract 시 소유.
/// GEAR 템플릿: 103=헬멧(HELMET), 104=방탄조끼(ARMOR), 105=리그(RIG,4×3), 106=백팩(BACKPACK,5×5).
/// 장비 전용 시드 플레이어(Golf/Hotel)로 다른 테스트와 격리한다.
/// </summary>
[Collection("market")]
public class EquipmentTests(MarketAppFixture f)
{
    private readonly MarketAppFixture _f = f;

    private const int Helmet = 103, Armor = 104, Rig = 105, Backpack = 106;

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

    private static async Task<StashDto> Stash(HttpClient c)
        => (await Api<StashDto>(await c.GetAsync("/api/stash"))).Data!;

    private static async Task<EquipmentDto> Equipment(HttpClient c)
        => (await Api<EquipmentDto>(await c.GetAsync("/api/equipment"))).Data!;

    private static Task<HttpResponseMessage> EquipRaw(HttpClient c, EquipSlot slot, Guid id)
        => c.PostAsJsonAsync("/api/equipment/equip", new EquipRequest(slot, id), Json);

    private static async Task<EquipmentDto> Equip(HttpClient c, EquipSlot slot, Guid id)
    {
        var r = await Api<EquipmentDto>(await EquipRaw(c, slot, id));
        Assert.True(r.Success);
        return r.Data!;
    }

    private static Task<HttpResponseMessage> Move(HttpClient c, MoveStashItemRequest req)
        => c.PostAsJsonAsync("/api/stash/move", req, Json);

    private static async Task<InventoryDto> Inv(HttpClient c)
        => (await Api<InventoryDto>(await c.GetAsync("/api/inventory"))).Data!;

    private static async Task<int> StackQty(HttpClient c, int templateId)
        => (await Inv(c)).Stacks.FirstOrDefault(s => s.TemplateId == templateId)?.Quantity ?? 0;

    private static async Task<bool> Owns(HttpClient c, Guid instanceId)
        => (await Inv(c)).Instances.Any(i => i.Id == instanceId);

    private static Task<HttpResponseMessage> Start(HttpClient c) => c.PostAsync("/api/raid/start", null);
    private static Task<HttpResponseMessage> Start(HttpClient c, RaidZone zone)
        => c.PostAsJsonAsync("/api/raid/start", new StartRaidRequest(zone), Json);
    private static Task<HttpResponseMessage> Extract(HttpClient c) => c.PostAsync("/api/raid/extract", null);
    private static Task<HttpResponseMessage> Die(HttpClient c) => c.PostAsync("/api/raid/die", null);
    private static Task<HttpResponseMessage> Scavenge(HttpClient c) => c.PostAsync("/api/raid/loot", null);

    private async Task ResetDeathChance(Guid player)
    {
        await using var db = new NpgsqlConnection(_f.ConnString);
        await db.OpenAsync();
        await db.ExecuteAsync(
            "UPDATE raid_session SET death_chance_bps = 0 WHERE player_id = @p AND status = 'ACTIVE'",
            new { p = player });
    }

    /// <summary>테스트 격리: 잔존 장착 슬롯을 모두 해제해 깨끗한 상태에서 시작한다(실행 순서 무관).</summary>
    private static async Task ClearEquipment(HttpClient c)
    {
        var eq = await Equipment(c);
        foreach (var s in eq.Slots)
            (await c.PostAsJsonAsync("/api/equipment/unequip", new UnequipRequest(s.Slot), Json))
                .EnsureSuccessStatusCode();
    }

    /// <summary>테스트 격리: 잔존 POCKETS/중첩 배치를 지운다. 익스트랙션이 원위치로 복원하므로
    /// 공유 플레이어의 위험 컨테이너가 테스트 간 누적될 수 있다(소유는 유지, STASH로 정합화).</summary>
    private async Task ClearAtRisk(Guid player)
    {
        await using var db = new NpgsqlConnection(_f.ConnString);
        await db.OpenAsync();
        await db.ExecuteAsync(
            "DELETE FROM stash_placement WHERE player_id = @p AND container IN ('POCKETS','CONTAINER')",
            new { p = player });
        // 출격 수수료(캡 싱크) 도입 후 반복 출격이 잔액을 소진하므로 잔액을 넉넉히 리셋한다.
        await db.ExecuteAsync(
            "UPDATE wallet SET balance = 100000 WHERE player_id = @p", new { p = player });
    }

    // ----------------------------------------------------------------------

    // 장착 검증: 호환 슬롯이면 성공(그리드에서 제거·인형 위로), 슬롯 불일치면 SlotMismatch(400).
    [Fact]
    public async Task Equip_valid_and_slot_mismatch_rejected()
    {
        var g = await _f.AuthedAs(Golf);
        await ClearEquipment(g);

        var helmet = await GrantInstance(Golf, Helmet, 120);
        var armor = await GrantInstance(Golf, Armor, 240);
        await Stash(g); // 자동 배치

        // 유효: 헬멧을 HELMET 슬롯에.
        var eq = await Equip(g, EquipSlot.Helmet, helmet);
        Assert.Contains(eq.Slots, s => s.Slot == EquipSlot.Helmet && s.InstanceId == helmet);

        // 장착된 인스턴스는 스태시 그리드에서 사라진다(인형 위로).
        var stash = await Stash(g);
        Assert.DoesNotContain(stash.Placements, p => p.InstanceId == helmet);
        Assert.DoesNotContain(stash.Unplaced, p => p.InstanceId == helmet);

        // 슬롯 불일치: 방탄조끼(ARMOR)를 HELMET 슬롯에 → SlotMismatch.
        var mismatch = await EquipRaw(g, EquipSlot.Helmet, armor);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Equal(ErrorCode.SlotMismatch, (await Api<EquipmentDto>(mismatch)).Error!.Code);

        // 슬롯 불일치(다른 방향): 헬멧을 WEAPON 슬롯에 → SlotMismatch.
        var mismatch2 = await EquipRaw(g, EquipSlot.Weapon, helmet);
        Assert.Equal(ErrorCode.SlotMismatch, (await Api<EquipmentDto>(mismatch2)).Error!.Code);

        // 이미 점유된 슬롯에 재장착 시도 → SlotMismatch(먼저 해제 필요).
        var helmet2 = await GrantInstance(Golf, Helmet, 100);
        var occupied = await EquipRaw(g, EquipSlot.Helmet, helmet2);
        Assert.Equal(ErrorCode.SlotMismatch, (await Api<EquipmentDto>(occupied)).Error!.Code);

        await ClearEquipment(g);
    }

    // 중첩 그리드: 아이템을 백팩 내부로 이동(반입)/재배치/반출. 경계·겹침 서버 권위 검증.
    [Fact]
    public async Task Place_item_into_backpack_nested_grid_bounds_and_overlap()
    {
        var g = await _f.AuthedAs(Golf);
        await ClearEquipment(g);

        var backpackId = await GrantInstance(Golf, Backpack, 100);
        await Stash(g);
        var eq = await Equip(g, EquipSlot.Backpack, backpackId);

        // 장비 스냅샷에 백팩의 중첩 그리드(5×5)가 노출된다.
        var nested = Assert.Single(eq.Containers);
        Assert.Equal(backpackId, nested.ContainerInstanceId);
        Assert.Equal(EquipSlot.Backpack, nested.Slot);
        Assert.Equal(5, nested.GridW);
        Assert.Equal(5, nested.GridH);
        Assert.Empty(nested.Placements);

        await GrantStack(Golf, 25, 3);              // MRE(스택 1×1)
        var hatchet = await GrantInstance(Golf, 60, 100); // 손도끼(유니크 1×2)
        await Stash(g);

        // 스택을 백팩 (0,0)으로 반입.
        (await Move(g, new MoveStashItemRequest(StashEntryKind.Stack, 25, null, 0, 0,
            GridContainer.Stash, GridContainer.Container, 3, null, backpackId))).EnsureSuccessStatusCode();

        var afterStack = await Equipment(g);
        var bp = afterStack.Containers.Single();
        Assert.Contains(bp.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 25
            && p.X == 0 && p.Y == 0 && p.Quantity == 3 && p.ContainerInstanceId == backpackId);

        // 경계 밖: 손도끼(1×2)를 (4,4)에 → 4+2=6 > 5 (높이 초과) → PlacementInvalid.
        var oob = await Move(g, new MoveStashItemRequest(StashEntryKind.Instance, null, hatchet, 4, 4,
            GridContainer.Stash, GridContainer.Container, null, null, backpackId));
        Assert.Equal(ErrorCode.PlacementInvalid, (await Api<StashDto>(oob)).Error!.Code);

        // 겹침: 손도끼를 스택이 있는 (0,0)에 → 겹침 → PlacementInvalid.
        var overlap = await Move(g, new MoveStashItemRequest(StashEntryKind.Instance, null, hatchet, 0, 0,
            GridContainer.Stash, GridContainer.Container, null, null, backpackId));
        Assert.Equal(ErrorCode.PlacementInvalid, (await Api<StashDto>(overlap)).Error!.Code);

        // 유효: 손도끼를 (2,0)에 → 1×2 정상 반입.
        (await Move(g, new MoveStashItemRequest(StashEntryKind.Instance, null, hatchet, 2, 0,
            GridContainer.Stash, GridContainer.Container, null, null, backpackId))).EnsureSuccessStatusCode();
        var afterAxe = await Equipment(g);
        Assert.Contains(afterAxe.Containers.Single().Placements,
            p => p.InstanceId == hatchet && p.X == 2 && p.Y == 0 && p.W == 1 && p.H == 2);

        // 반출: 스택을 백팩 → STASH로 되돌린다.
        (await Move(g, new MoveStashItemRequest(StashEntryKind.Stack, 25, null, 0, 0,
            GridContainer.Container, GridContainer.Stash, 3, backpackId, null))).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await Equipment(g)).Containers.Single().Placements,
            p => p.Kind == StashEntryKind.Stack && p.TemplateId == 25);
        Assert.Contains((await Stash(g)).Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 25);

        await ClearEquipment(g);
    }

    // 레이드 at-risk는 장착 아이템 + 백팩 내용물까지 포함한다 → Die 소실. 스태시(안전)는 무관.
    [Fact]
    public async Task Die_destroys_equipped_and_backpack_contents_stash_untouched()
    {
        var h = await _f.AuthedAs(Hotel);
        await ClearEquipment(h);
        await ClearAtRisk(Hotel);

        var helmet = await GrantInstance(Hotel, Helmet, 120);
        var backpackId = await GrantInstance(Hotel, Backpack, 100);
        await Stash(h);
        await Equip(h, EquipSlot.Helmet, helmet);
        await Equip(h, EquipSlot.Backpack, backpackId);

        await GrantStack(Hotel, 25, 5);               // 백팩에 넣을 스택
        var hatchet = await GrantInstance(Hotel, 60, 100); // 백팩에 넣을 유니크
        await GrantStack(Hotel, 21, 4);               // STASH에 남길 안전 아이템(반입 안 함)
        await Stash(h);
        var safeBefore = await StackQty(h, 21);

        (await Move(h, new MoveStashItemRequest(StashEntryKind.Stack, 25, null, 0, 0,
            GridContainer.Stash, GridContainer.Container, 5, null, backpackId))).EnsureSuccessStatusCode();
        (await Move(h, new MoveStashItemRequest(StashEntryKind.Instance, null, hatchet, 2, 0,
            GridContainer.Stash, GridContainer.Container, null, null, backpackId))).EnsureSuccessStatusCode();

        // StartRaid: 장착(헬멧/백팩) + 백팩 내용물(스택25 + 손도끼)이 모두 위험으로 잠긴다.
        var started = await Api<RaidSessionDto>(await Start(h));
        Assert.True(started.Success);
        var items = started.Data!.Items;
        Assert.Contains(items, i => i.Kind == StashEntryKind.Instance && i.InstanceId == helmet);
        Assert.Contains(items, i => i.Kind == StashEntryKind.Instance && i.InstanceId == backpackId);
        Assert.Contains(items, i => i.Kind == StashEntryKind.Instance && i.InstanceId == hatchet);
        Assert.Contains(items, i => i.Kind == StashEntryKind.Stack && i.TemplateId == 25 && i.Quantity == 5);
        // 안전 아이템(21)은 위험에 포함되지 않는다.
        Assert.DoesNotContain(items, i => i.TemplateId == 21);

        var died = await Api<RaidSessionDto>(await Die(h));
        Assert.Equal(RaidStatus.Died, died.Data!.Status);

        // 위험 전량 소실.
        Assert.False(await Owns(h, helmet));
        Assert.False(await Owns(h, backpackId));
        Assert.False(await Owns(h, hatchet));
        Assert.Equal(0, await StackQty(h, 25));
        // 슬롯도 비었다.
        Assert.Empty((await Equipment(h)).Slots);
        // 스태시(안전) 무관.
        Assert.Equal(safeBefore, await StackQty(h, 21));
    }

    // Extract = 보존: 장착 아이템 + 백팩 내용물이 전량 소유로 복귀. 총량 보존(conservation).
    [Fact]
    public async Task Extract_restores_equipped_and_backpack_contents_conservation()
    {
        var h = await _f.AuthedAs(Hotel);
        await ClearEquipment(h);
        await ClearAtRisk(Hotel);

        var helmet = await GrantInstance(Hotel, Helmet, 120);
        var backpackId = await GrantInstance(Hotel, Backpack, 100);
        await Stash(h);
        await Equip(h, EquipSlot.Helmet, helmet);
        await Equip(h, EquipSlot.Backpack, backpackId);

        await GrantStack(Hotel, 22, 6);               // 백팩에 넣을 스택
        var axe = await GrantInstance(Hotel, 59, 150); // 백팩에 넣을 유니크(소방도끼 1×3)
        await Stash(h);
        var beforeStack22 = await StackQty(h, 22);

        (await Move(h, new MoveStashItemRequest(StashEntryKind.Stack, 22, null, 0, 0,
            GridContainer.Stash, GridContainer.Container, 6, null, backpackId))).EnsureSuccessStatusCode();
        (await Move(h, new MoveStashItemRequest(StashEntryKind.Instance, null, axe, 2, 0,
            GridContainer.Stash, GridContainer.Container, null, null, backpackId))).EnsureSuccessStatusCode();

        (await Start(h)).EnsureSuccessStatusCode();
        // 위험 상태 확인: 반입 스택은 인벤에서 빠졌다.
        Assert.Equal(0, await StackQty(h, 22));
        Assert.False(await Owns(h, axe));

        var extracted = await Api<RaidSessionDto>(await Extract(h));
        Assert.Equal(RaidStatus.Extracted, extracted.Data!.Status);

        // 보존: 장착 아이템 + 백팩 내용물 전량 소유 복귀.
        Assert.True(await Owns(h, helmet));
        Assert.True(await Owns(h, backpackId));
        Assert.True(await Owns(h, axe));
        Assert.Equal(beforeStack22, await StackQty(h, 22));

        // 익스트랙션 시맨틱: 장착 아이템은 원래 슬롯, 백팩 내용물은 원래 백팩 그리드로 복원(STASH 덤프 아님).
        var eq = await Equipment(h);
        Assert.Contains(eq.Slots, s => s.Slot == EquipSlot.Helmet && s.InstanceId == helmet);
        Assert.Contains(eq.Slots, s => s.Slot == EquipSlot.Backpack && s.InstanceId == backpackId);
        var nested = eq.Containers.Single(cnt => cnt.ContainerInstanceId == backpackId);
        Assert.Contains(nested.Placements, p => p.InstanceId == axe && p.X == 2 && p.Y == 0);
        Assert.Contains(nested.Placements, p => p.Kind == StashEntryKind.Stack && p.TemplateId == 22 && p.X == 0 && p.Y == 0);
        // STASH에는 이번 회수분이 덤프되지 않는다.
        var stash = await Stash(h);
        Assert.DoesNotContain(stash.Placements, p => p.InstanceId == axe);

        await ClearEquipment(h);
    }

    // 전리품(loot-unique): 유니크 템플릿을 loot하면 인스턴스가 materialize(origin=RAID)되고 Extract 시 소유가 부여된다.
    // Kind=Stack으로 보내도 템플릿의 stackable=false면 인스턴스로 materialize된다(버그 수정 회귀 방지).
    [Fact]
    public async Task Loot_unique_template_materializes_instance_and_extract_yields_it()
    {
        var h = await _f.AuthedAs(Hotel);
        await ClearEquipment(h);
        await ClearAtRisk(Hotel);

        // StartRaid는 스태시 밖이 완전히 비어 있으면 거부되므로(RaidNothingToDeploy),
        // 이 loot 시나리오와 무관한 최소 장비(헬멧)를 하나 착용해 둔다.
        var helmet = await GrantInstance(Hotel, Helmet, 120);
        await Stash(h);
        await Equip(h, EquipSlot.Helmet, helmet);

        (await Start(h, RaidZone.High)).EnsureSuccessStatusCode(); // 유니크 비중 높은 존

        // 서버 드롭에서 유니크가 나올 때까지 루팅한다(유니크=stackable 아닌 카테고리, 전체의 ~절반).
        Guid instanceId = Guid.Empty;
        for (var i = 0; i < 40 && instanceId == Guid.Empty; i++)
        {
            var dropped = (await Api<LootResultDto>(await Scavenge(h))).Data!.Dropped!;
            if (dropped.Kind == StashEntryKind.Instance) instanceId = dropped.InstanceId!.Value;
        }
        Assert.NotEqual(Guid.Empty, instanceId); // 유니크 드롭이 하나는 나온다

        // materialize 시점엔 위험(owner=NULL) + origin=RAID.
        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            var origin = await db.ExecuteScalarAsync<string>(
                "SELECT origin FROM item_instance WHERE id = @id", new { id = instanceId });
            Assert.Equal("RAID", origin);
            Assert.False(await Owns(h, instanceId)); // 아직 소유 아님(위험).
        }

        await ResetDeathChance(Hotel); // 다수 루팅으로 사망확률이 찼으므로 생존 보장(materialize 회귀가 목적)
        var extracted = await Api<RaidSessionDto>(await Extract(h));
        Assert.Equal(RaidStatus.Extracted, extracted.Data!.Status);

        // Extract 후 소유가 부여된다.
        Assert.True(await Owns(h, instanceId));
    }
}

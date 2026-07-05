using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Equipment;
using ItemMarket.Contracts.Stash;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;

namespace ItemMarket.Grains.Grains;

/// <summary>
/// 스태시 grain(키 = playerId). 컨테이너(STASH/LOADOUT/중첩 백팩·리그) 인지. ===== 동시성 =====
///
/// WalletGrain/PlayerInventoryGrain과 같은 무상태(pass-through) 설계다. 그리드를
/// 인메모리에 캐시하지 않고 매 호출마다 Postgres(소스오브트루스)에서 배치·인벤·카탈로그·장비를
/// 읽어 재구성한다. Orleans는 플레이어당 grain 활성화를 단일로 보장하고 기본 non-reentrant라,
/// 같은 플레이어의 GetStash/MoveItem 요청은 자동으로 직렬화된다 → 컨테이너 간 이동을 포함해
/// 이동 검증(경계·겹침)과 영속화 사이에 다른 이동이 끼어들 수 없어 별도 락이 필요 없다.
///
/// ===== 소유권 모델 =====
/// 아이템(스택 수량의 일부 / 인스턴스)은 정확히 한 컨테이너에 놓인다. 컨테이너 배치는
/// 조직화(위치/반입 여부)일 뿐이고, 총 소유량의 진실은 inventory_stack/item_instance다.
/// 미배치 소유 아이템은 안전을 위해 항상 STASH에 자동 배치된다. 단, 장착(equipment)된 인스턴스는
/// 인형(doll) 위에 있어 그리드 자동 배치 대상에서 제외한다. 장착된 백팩/리그는 내부 그리드
/// (중첩 컨테이너)를 제공하며, 그 그리드에 놓인 배치는 container_instance_id로 주소화된다.
/// </summary>
public sealed class StashGrain(MarketRepository repo) : Grain, IStashGrain
{
    private Guid PlayerId => this.GetPrimaryKey();

    /// <summary>물리 컨테이너 참조: 종류(STASH/LOADOUT/중첩) + 중첩일 때 컨테이너 인스턴스 id.</summary>
    private readonly record struct ContainerRef(GridContainer Kind, Guid? InstanceId);

    /// <summary>한 grain 호출에서 재사용하는 로드 상태(인벤·카탈로그·장비 파생).</summary>
    private sealed record Ctx(
        Dictionary<int, int> OwnedStacks,
        Dictionary<Guid, int> OwnedInstances,
        Dictionary<int, (int W, int H)> Footprints,
        HashSet<Guid> EquippedInstanceIds,
        Dictionary<Guid, (int W, int H)> NestedDims);

    public async Task<StashDto> GetStash(GridContainer container)
    {
        var ctx = await LoadAsync();
        await ReconcileAsync(ctx);
        var placements = await repo.GetStashPlacementsAsync(PlayerId);
        return BuildSnapshot(new ContainerRef(container, null), placements, ctx);
    }

    public async Task<StashDto> GetContainer(Guid containerInstanceId)
    {
        var ctx = await LoadAsync();
        if (!ctx.NestedDims.ContainsKey(containerInstanceId))
            throw new DomainException(ErrorCode.PlacementInvalid, "장착된 백팩/리그 컨테이너가 아닙니다.");
        await ReconcileAsync(ctx);
        var placements = await repo.GetStashPlacementsAsync(PlayerId);
        return BuildSnapshot(new ContainerRef(GridContainer.Container, containerInstanceId), placements, ctx);
    }

    public async Task<StashDto> MoveItem(MoveStashItemRequest req)
    {
        var ctx = await LoadAsync();
        // 이동 전에 정합화: 아직 배치되지 않은 아이템도 STASH에 존재하도록 해 원본 컨테이너 조회가 성립한다.
        await ReconcileAsync(ctx);

        var from = new ContainerRef(req.FromContainer, req.FromContainerInstanceId);
        var to = new ContainerRef(req.ToContainer, req.ToContainerInstanceId);
        ValidateContainerRef(from, ctx);
        ValidateContainerRef(to, ctx);

        // ---- 대상 확인 + 소유권 검증 + footprint 결정 ----
        int templateId;
        int w, h;
        if (req.Kind == StashEntryKind.Stack)
        {
            if (req.TemplateId is not { } tid)
                throw new DomainException(ErrorCode.ValidationError, "스택 이동에는 TemplateId가 필요합니다.");
            if (!ctx.OwnedStacks.ContainsKey(tid))
                throw new DomainException(ErrorCode.PlacementInvalid, "해당 스택 아이템을 소유하고 있지 않습니다.");
            templateId = tid;
            (w, h) = (1, 1);
        }
        else
        {
            if (req.InstanceId is not { } iid)
                throw new DomainException(ErrorCode.ValidationError, "유니크 이동에는 InstanceId가 필요합니다.");
            if (!ctx.OwnedInstances.TryGetValue(iid, out templateId))
                throw new DomainException(ErrorCode.PlacementInvalid, "해당 인스턴스를 소유하고 있지 않습니다.");
            if (ctx.EquippedInstanceIds.Contains(iid))
                throw new DomainException(ErrorCode.PlacementInvalid, "장착 중인 아이템은 이동할 수 없습니다. 먼저 해제하세요.");
            (w, h) = ctx.Footprints.GetValueOrDefault(templateId, (1, 1));
        }

        var placements = (await repo.GetStashPlacementsAsync(PlayerId)).ToList();
        var (gw, gh) = DimsOf(to, ctx);
        var target = new Rect(req.X, req.Y, w, h);

        if (req.Kind == StashEntryKind.Instance)
            await MoveInstanceAsync(req, to, templateId, target, gw, gh, placements, ctx);
        else
            await MoveStackAsync(req, from, to, templateId, target, gw, gh, placements, ctx);

        var after = await repo.GetStashPlacementsAsync(PlayerId);
        return BuildSnapshot(to, after, ctx);
    }

    // ======================================================================
    //  장비(equipment) — 슬롯 조작. 같은 grain 활성화에서 처리되어 스태시 이동/정합화와
    //  직렬화된다(장착이 그리드 배치를 제거하고, 정합화가 다시 놓지 않도록 하는 것이 원자적).
    // ======================================================================

    public async Task<EquipmentDto> GetEquipment()
    {
        var ctx = await LoadAsync();
        await ReconcileAsync(ctx);
        return await BuildEquipmentAsync(ctx);
    }

    public async Task<EquipmentDto> Equip(EquipRequest req)
    {
        await repo.EquipAsync(PlayerId, req.Slot, req.InstanceId);
        var ctx = await LoadAsync();
        await ReconcileAsync(ctx);
        return await BuildEquipmentAsync(ctx);
    }

    public async Task<EquipmentDto> Unequip(UnequipRequest req)
    {
        await repo.UnequipAsync(PlayerId, req.Slot);
        var ctx = await LoadAsync();
        await ReconcileAsync(ctx);
        return await BuildEquipmentAsync(ctx);
    }

    /// <summary>장착 슬롯 + 장착된 백팩/리그의 중첩 그리드 스냅샷을 조립한다.</summary>
    private async Task<EquipmentDto> BuildEquipmentAsync(Ctx ctx)
    {
        var equipment = await repo.GetEquipmentAsync(PlayerId);
        var placements = await repo.GetStashPlacementsAsync(PlayerId);

        var slots = equipment
            .Select(e => new EquippedItemDto(e.Slot, e.InstanceId, e.TemplateId)).ToList();

        var containers = new List<NestedContainerDto>();
        foreach (var e in equipment)
        {
            if (!ctx.NestedDims.TryGetValue(e.InstanceId, out var dims)) continue;
            var snap = BuildSnapshot(new ContainerRef(GridContainer.Container, e.InstanceId), placements, ctx);
            containers.Add(new NestedContainerDto(e.InstanceId, e.TemplateId, e.Slot, dims.W, dims.H, snap.Placements));
        }

        return new EquipmentDto(PlayerId, slots, containers);
    }

    /// <summary>컨테이너 참조 유효성: 중첩이면 InstanceId가 있고 장착된 백팩/리그여야 한다.</summary>
    private static void ValidateContainerRef(ContainerRef c, Ctx ctx)
    {
        if (c.Kind != GridContainer.Container) return;
        if (c.InstanceId is not { } id || !ctx.NestedDims.ContainsKey(id))
            throw new DomainException(ErrorCode.PlacementInvalid, "유효한 중첩 컨테이너(장착된 백팩/리그)가 아닙니다.");
    }

    /// <summary>컨테이너 참조의 그리드 크기. STASH/LOADOUT은 고정, 중첩은 컨테이너 인스턴스의 template에서.</summary>
    private static (int W, int H) DimsOf(ContainerRef c, Ctx ctx)
        => c.Kind == GridContainer.Container
            ? ctx.NestedDims[c.InstanceId!.Value]
            : StashGeometry.Dims(c.Kind);

    /// <summary>배치가 컨테이너 참조와 같은 물리 컨테이너에 속하는가(중첩은 인스턴스 id까지 일치).</summary>
    private static bool SameContainer(StashPlacementRow p, ContainerRef c)
        => p.Container == c.Kind
           && (c.Kind != GridContainer.Container || p.ContainerInstanceId == c.InstanceId);

    // ---- 유니크 인스턴스 이동: 항상 통째로. 대상 컨테이너 경계+겹침 검증 후 upsert(컨테이너+위치 갱신). ----
    private async Task MoveInstanceAsync(
        MoveStashItemRequest req, ContainerRef to, int templateId, Rect target,
        int gw, int gh, List<StashPlacementRow> placements, Ctx ctx)
    {
        if (!StashGeometry.InBounds(gw, gh, target))
            throw new DomainException(ErrorCode.PlacementInvalid, "배치가 대상 컨테이너 경계를 벗어납니다.");

        EnsureNoOverlap(to, target, placements, ctx.Footprints, isSelf: p => p.InstanceId == req.InstanceId);

        await repo.UpsertInstancePlacementAsync(PlayerId, to.Kind, templateId, req.InstanceId!.Value, req.X, req.Y, to.InstanceId);
    }

    // ---- 스택 이동: 같은 컨테이너면 위치 재배치, 다른 컨테이너면 수량(부분) 원자 이동. ----
    private async Task MoveStackAsync(
        MoveStashItemRequest req, ContainerRef from, ContainerRef to, int templateId, Rect target,
        int gw, int gh, List<StashPlacementRow> placements, Ctx ctx)
    {
        if (from == to)
        {
            // 같은 컨테이너 재배치: 경계+겹침 검증(자신 제외) 후 위치만 갱신(수량 유지).
            if (!StashGeometry.InBounds(gw, gh, target))
                throw new DomainException(ErrorCode.PlacementInvalid, "배치가 대상 컨테이너 경계를 벗어납니다.");
            EnsureNoOverlap(to, target, placements, ctx.Footprints,
                isSelf: p => IsStackIn(p, to, templateId));

            var current = placements.FirstOrDefault(p => IsStackIn(p, to, templateId));
            var qty = current?.Quantity ?? 0;
            if (qty <= 0)
                throw new DomainException(ErrorCode.PlacementInvalid, "해당 컨테이너에 스택이 없습니다.");
            await repo.UpsertStackPlacementAsync(PlayerId, to.Kind, templateId, req.X, req.Y, qty, to.InstanceId);
            return;
        }

        // 컨테이너 간 이동(반입/반출).
        var source = placements.FirstOrDefault(p => IsStackIn(p, from, templateId));
        if (source is null || source.Quantity <= 0)
            throw new DomainException(ErrorCode.PlacementInvalid, "원본 컨테이너에 해당 스택이 없습니다.");

        var moveQty = req.Quantity ?? source.Quantity;
        if (moveQty < 1 || moveQty > source.Quantity)
            throw new DomainException(ErrorCode.ValidationError, "이동 수량이 원본 컨테이너의 보유 수량 범위를 벗어납니다.");

        // 대상 컨테이너에 같은 스택 칸이 없으면 새 칸을 만드므로 경계+겹침을 검증한다(있으면 기존 칸에 수량만 가산).
        var destExisting = placements.FirstOrDefault(p => IsStackIn(p, to, templateId));
        if (destExisting is null)
        {
            if (!StashGeometry.InBounds(gw, gh, target))
                throw new DomainException(ErrorCode.PlacementInvalid, "배치가 대상 컨테이너 경계를 벗어납니다.");
            EnsureNoOverlap(to, target, placements, ctx.Footprints, isSelf: _ => false);
        }

        await repo.MoveStackAcrossContainersAsync(PlayerId, templateId, from.Kind, to.Kind, moveQty, req.X, req.Y,
            from.InstanceId, to.InstanceId);
    }

    private static bool IsStackIn(StashPlacementRow p, ContainerRef c, int templateId)
        => p.Kind == StashEntryKind.Stack && p.TemplateId == templateId && SameContainer(p, c);

    /// <summary>대상 컨테이너의 다른 배치와 겹치면 PlacementInvalid. isSelf로 이동 대상 자신은 제외.</summary>
    private static void EnsureNoOverlap(
        ContainerRef container, Rect target, IEnumerable<StashPlacementRow> placements,
        Dictionary<int, (int W, int H)> footprints, Func<StashPlacementRow, bool> isSelf)
    {
        foreach (var p in placements)
        {
            if (!SameContainer(p, container) || isSelf(p)) continue;
            var (pw, ph) = p.Kind == StashEntryKind.Stack
                ? (1, 1)
                : footprints.GetValueOrDefault(p.TemplateId, (1, 1));
            if (StashGeometry.Overlaps(target, new Rect(p.X, p.Y, pw, ph)))
                throw new DomainException(ErrorCode.PlacementInvalid, "다른 아이템과 겹칩니다.");
        }
    }

    /// <summary>
    /// 배치를 소유 현황과 정합화·영속화한다. 자동 배치 대상은 항상 STASH.
    ///   - 더 이상 소유하지 않는 배치 삭제
    ///   - 스택 총 배치량 &lt; 소유량: 부족분을 STASH에 가산/신규 배치
    ///   - 스택 총 배치량 &gt; 소유량: 초과분을 STASH→LOADOUT→중첩 순으로 제거(매도/소비 반영)
    ///   - 미배치 인스턴스: STASH에 first-fit 자동 배치(단, 장착 중인 인스턴스는 인형 위에 있어 제외)
    /// </summary>
    private async Task ReconcileAsync(Ctx ctx)
    {
        var ownedStacks = ctx.OwnedStacks;
        var ownedInstances = ctx.OwnedInstances;
        var footprints = ctx.Footprints;
        var placements = (await repo.GetStashPlacementsAsync(PlayerId)).ToList();

        // 1) 더 이상 소유하지 않는 배치 정리.
        foreach (var p in placements)
        {
            if (p.Kind == StashEntryKind.Stack)
            {
                if (!ownedStacks.ContainsKey(p.TemplateId))
                    await repo.DeleteStackPlacementAsync(PlayerId, p.Container, p.TemplateId, p.ContainerInstanceId);
            }
            else if (p.InstanceId is { } iid && !ownedInstances.ContainsKey(iid))
            {
                await repo.DeleteInstancePlacementAsync(PlayerId, iid);
            }
        }

        var liveStacks = placements
            .Where(p => p.Kind == StashEntryKind.Stack && ownedStacks.ContainsKey(p.TemplateId)).ToList();
        var liveInstances = placements
            .Where(p => p.Kind == StashEntryKind.Instance && p.InstanceId is { } id && ownedInstances.ContainsKey(id))
            .ToList();

        // STASH 점유 사각형(자동 배치 first-fit 기준). 자동 배치는 STASH에만 한다.
        var stashOccupied = new List<Rect>();
        foreach (var p in liveStacks.Where(p => p.Container == GridContainer.Stash))
            stashOccupied.Add(new Rect(p.X, p.Y, 1, 1));
        foreach (var p in liveInstances.Where(p => p.Container == GridContainer.Stash))
        {
            var (w, h) = footprints.GetValueOrDefault(p.TemplateId, (1, 1));
            stashOccupied.Add(new Rect(p.X, p.Y, w, h));
        }

        // 2) 스택 수량 정합화.
        foreach (var (template, total) in ownedStacks)
        {
            var ps = liveStacks.Where(p => p.TemplateId == template).ToList();
            var allocated = ps.Sum(p => p.Quantity);
            if (allocated == total) continue;

            if (allocated < total)
            {
                var deficit = total - allocated;
                var stash = ps.FirstOrDefault(p => p.Container == GridContainer.Stash);
                if (stash is not null)
                {
                    await repo.UpsertStackPlacementAsync(PlayerId, GridContainer.Stash, template, stash.X, stash.Y, stash.Quantity + deficit);
                }
                else
                {
                    var fit = StashGeometry.FirstFit(GridContainer.Stash, stashOccupied, 1, 1);
                    if (fit is null) continue; // 자리 없음 → Unplaced로 노출(스냅샷에서 계산)
                    var (x, y) = fit.Value;
                    stashOccupied.Add(new Rect(x, y, 1, 1));
                    await repo.UpsertStackPlacementAsync(PlayerId, GridContainer.Stash, template, x, y, deficit);
                }
            }
            else // allocated > total: 초과분 제거(STASH → LOADOUT → 중첩 순).
            {
                var surplus = allocated - total;
                foreach (var p in ps.OrderBy(ContainerRank))
                {
                    if (surplus <= 0) break;
                    var take = Math.Min(surplus, p.Quantity);
                    surplus -= take;
                    var newQty = p.Quantity - take;
                    if (newQty == 0)
                        await repo.DeleteStackPlacementAsync(PlayerId, p.Container, template, p.ContainerInstanceId);
                    else
                        await repo.UpsertStackPlacementAsync(PlayerId, p.Container, template, p.X, p.Y, newQty, p.ContainerInstanceId);
                }
            }
        }

        // 3) 미배치 인스턴스 → STASH 자동 배치(장착 중인 인스턴스는 인형 위에 있어 제외).
        foreach (var (id, template) in ownedInstances.OrderBy(i => i.Value).ThenBy(i => i.Key))
        {
            if (ctx.EquippedInstanceIds.Contains(id)) continue;
            if (liveInstances.Any(p => p.InstanceId == id)) continue;
            var (w, h) = footprints.GetValueOrDefault(template, (1, 1));
            var fit = StashGeometry.FirstFit(GridContainer.Stash, stashOccupied, w, h);
            if (fit is null) continue; // 자리 없음 → Unplaced
            var (x, y) = fit.Value;
            stashOccupied.Add(new Rect(x, y, w, h));
            await repo.UpsertInstancePlacementAsync(PlayerId, GridContainer.Stash, template, id, x, y);
        }
    }

    /// <summary>초과분 제거 우선순위: STASH(0) → LOADOUT(1) → 중첩 컨테이너(2).</summary>
    private static int ContainerRank(StashPlacementRow p) => p.Container switch
    {
        GridContainer.Stash => 0,
        GridContainer.Loadout => 1,
        _ => 2
    };

    /// <summary>정합화된 배치에서 요청 컨테이너 스냅샷을 만든다. Unplaced는 STASH 자동 배치의 안전망이라 STASH 뷰에서만 계산.</summary>
    private StashDto BuildSnapshot(ContainerRef container, IReadOnlyList<StashPlacementRow> placements, Ctx ctx)
    {
        var (gw, gh) = DimsOf(container, ctx);
        var ownedStacks = ctx.OwnedStacks;
        var ownedInstances = ctx.OwnedInstances;
        var footprints = ctx.Footprints;
        var placed = new List<StashPlacementDto>();

        foreach (var p in placements.Where(p => SameContainer(p, container)))
        {
            if (p.Kind == StashEntryKind.Stack)
            {
                if (!ownedStacks.ContainsKey(p.TemplateId)) continue;
                placed.Add(new StashPlacementDto(container.Kind, StashEntryKind.Stack, p.TemplateId, null, p.X, p.Y, 1, 1, p.Quantity, p.ContainerInstanceId));
            }
            else if (p.InstanceId is { } iid && ownedInstances.ContainsKey(iid))
            {
                var (w, h) = footprints.GetValueOrDefault(p.TemplateId, (1, 1));
                placed.Add(new StashPlacementDto(container.Kind, StashEntryKind.Instance, p.TemplateId, iid, p.X, p.Y, w, h, 1, p.ContainerInstanceId));
            }
        }

        var unplaced = new List<StashPlacementDto>();
        if (container.Kind == GridContainer.Stash)
        {
            foreach (var (template, total) in ownedStacks)
            {
                var allocated = placements
                    .Where(p => p.Kind == StashEntryKind.Stack && p.TemplateId == template).Sum(p => p.Quantity);
                if (allocated < total)
                    unplaced.Add(new StashPlacementDto(container.Kind, StashEntryKind.Stack, template, null, 0, 0, 1, 1, total - allocated));
            }
            foreach (var (id, template) in ownedInstances)
            {
                if (ctx.EquippedInstanceIds.Contains(id)) continue; // 인형 위 → 스태시 미배치 대상 아님
                if (placements.Any(p => p.InstanceId == id)) continue;
                var (w, h) = footprints.GetValueOrDefault(template, (1, 1));
                unplaced.Add(new StashPlacementDto(container.Kind, StashEntryKind.Instance, template, id, 0, 0, w, h, 1));
            }
        }

        return new StashDto(PlayerId, container.Kind, gw, gh, placed, unplaced);
    }

    /// <summary>인벤·카탈로그·장비를 읽어 한 호출에서 재사용할 파생 상태를 만든다.</summary>
    private async Task<Ctx> LoadAsync()
    {
        var inv = await repo.GetInventoryAsync(PlayerId);
        var catalog = await repo.GetCatalogAsync();
        var footprints = catalog.ToDictionary(t => t.Id, t => (t.GridW, t.GridH));
        var containerDims = catalog.Where(t => t.IsContainer && t.ContainerW is not null && t.ContainerH is not null)
            .ToDictionary(t => t.Id, t => (t.ContainerW!.Value, t.ContainerH!.Value));

        var equipment = await repo.GetEquipmentAsync(PlayerId);
        var equippedIds = equipment.Select(e => e.InstanceId).ToHashSet();
        var nestedDims = equipment
            .Where(e => containerDims.ContainsKey(e.TemplateId))
            .ToDictionary(e => e.InstanceId, e => containerDims[e.TemplateId]);

        var ownedStacks = inv.Stacks.Where(s => s.Quantity > 0).ToDictionary(s => s.TemplateId, s => s.Quantity);
        var ownedInstances = inv.Instances.ToDictionary(i => i.Id, i => i.TemplateId);
        return new Ctx(ownedStacks, ownedInstances, footprints, equippedIds, nestedDims);
    }
}

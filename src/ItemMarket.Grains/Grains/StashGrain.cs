using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Stash;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;

namespace ItemMarket.Grains.Grains;

/// <summary>
/// 스태시 grain(키 = playerId). 컨테이너(STASH/LOADOUT) 인지. ===== 동시성 =====
///
/// WalletGrain/PlayerInventoryGrain과 같은 무상태(pass-through) 설계다. 그리드를
/// 인메모리에 캐시하지 않고 매 호출마다 Postgres(소스오브트루스)에서 배치·인벤·카탈로그를
/// 읽어 재구성한다. Orleans는 플레이어당 grain 활성화를 단일로 보장하고 기본 non-reentrant라,
/// 같은 플레이어의 GetStash/MoveItem 요청은 자동으로 직렬화된다 → 컨테이너 간 이동을 포함해
/// 이동 검증(경계·겹침)과 영속화 사이에 다른 이동이 끼어들 수 없어 별도 락이 필요 없다.
///
/// ===== 소유권 모델 =====
/// 아이템(스택 수량의 일부 / 인스턴스)은 정확히 한 컨테이너에 놓인다. 컨테이너 배치는
/// 조직화(위치/반입 여부)일 뿐이고, 총 소유량의 진실은 inventory_stack/item_instance다.
/// 따라서 컨테이너 간 이동은 inventory를 건드리지 않아 GET /api/inventory 총량이 항상 보존된다.
/// 미배치 소유 아이템(신규 지급/체결 획득)은 안전을 위해 항상 STASH에 자동 배치된다.
/// </summary>
public sealed class StashGrain(MarketRepository repo) : Grain, IStashGrain
{
    private Guid PlayerId => this.GetPrimaryKey();

    public async Task<StashDto> GetStash(GridContainer container)
    {
        var inv = await repo.GetInventoryAsync(PlayerId);
        var footprints = await LoadFootprintsAsync();
        var ownedStacks = inv.Stacks.Where(s => s.Quantity > 0).ToDictionary(s => s.TemplateId, s => s.Quantity);
        var ownedInstances = inv.Instances.ToDictionary(i => i.Id, i => i.TemplateId);

        // 배치를 소유 현황과 정합화(누락분 STASH 자동 배치, 초과분 정리)한 뒤 최신 배치를 다시 읽는다.
        await ReconcileAsync(ownedStacks, ownedInstances, footprints);
        var placements = await repo.GetStashPlacementsAsync(PlayerId);

        return BuildSnapshot(container, placements, ownedStacks, ownedInstances, footprints);
    }

    public async Task<StashDto> MoveItem(MoveStashItemRequest req)
    {
        var inv = await repo.GetInventoryAsync(PlayerId);
        var footprints = await LoadFootprintsAsync();
        var ownedStacks = inv.Stacks.Where(s => s.Quantity > 0).ToDictionary(s => s.TemplateId, s => s.Quantity);
        var ownedInstances = inv.Instances.ToDictionary(i => i.Id, i => i.TemplateId);

        // 이동 전에 정합화: 아직 배치되지 않은 아이템도 STASH에 존재하도록 해 원본 컨테이너 조회가 성립한다.
        await ReconcileAsync(ownedStacks, ownedInstances, footprints);

        // ---- 대상 확인 + 소유권 검증 + footprint 결정 ----
        int templateId;
        int w, h;
        if (req.Kind == StashEntryKind.Stack)
        {
            if (req.TemplateId is not { } tid)
                throw new DomainException(ErrorCode.ValidationError, "스택 이동에는 TemplateId가 필요합니다.");
            if (!ownedStacks.ContainsKey(tid))
                throw new DomainException(ErrorCode.PlacementInvalid, "해당 스택 아이템을 소유하고 있지 않습니다.");
            templateId = tid;
            (w, h) = (1, 1);
        }
        else
        {
            if (req.InstanceId is not { } iid)
                throw new DomainException(ErrorCode.ValidationError, "유니크 이동에는 InstanceId가 필요합니다.");
            if (!ownedInstances.TryGetValue(iid, out templateId))
                throw new DomainException(ErrorCode.PlacementInvalid, "해당 인스턴스를 소유하고 있지 않습니다.");
            (w, h) = footprints.GetValueOrDefault(templateId, (1, 1));
        }

        var placements = (await repo.GetStashPlacementsAsync(PlayerId)).ToList();
        var target = new Rect(req.X, req.Y, w, h);

        if (req.Kind == StashEntryKind.Instance)
            await MoveInstanceAsync(req, templateId, target, placements, footprints);
        else
            await MoveStackAsync(req, templateId, target, placements, footprints);

        return await GetStash(req.ToContainer);
    }

    // ---- 유니크 인스턴스 이동: 항상 통째로. 대상 컨테이너 경계+겹침 검증 후 upsert(컨테이너+위치 갱신). ----
    private async Task MoveInstanceAsync(
        MoveStashItemRequest req, int templateId, Rect target,
        List<StashPlacementRow> placements, Dictionary<int, (int W, int H)> footprints)
    {
        if (!StashGeometry.InBounds(req.ToContainer, target))
            throw new DomainException(ErrorCode.PlacementInvalid, "배치가 대상 컨테이너 경계를 벗어납니다.");

        EnsureNoOverlap(req.ToContainer, target, placements, footprints,
            isSelf: p => p.InstanceId == req.InstanceId);

        await repo.UpsertInstancePlacementAsync(PlayerId, req.ToContainer, templateId, req.InstanceId!.Value, req.X, req.Y);
    }

    // ---- 스택 이동: 같은 컨테이너면 위치 재배치, 다른 컨테이너면 수량(부분) 원자 이동. ----
    private async Task MoveStackAsync(
        MoveStashItemRequest req, int templateId, Rect target,
        List<StashPlacementRow> placements, Dictionary<int, (int W, int H)> footprints)
    {
        if (req.FromContainer == req.ToContainer)
        {
            // 같은 컨테이너 재배치: 경계+겹침 검증(자신 제외) 후 위치만 갱신(수량 유지).
            if (!StashGeometry.InBounds(req.ToContainer, target))
                throw new DomainException(ErrorCode.PlacementInvalid, "배치가 대상 컨테이너 경계를 벗어납니다.");
            EnsureNoOverlap(req.ToContainer, target, placements, footprints,
                isSelf: p => p.Kind == StashEntryKind.Stack && p.Container == req.ToContainer && p.TemplateId == templateId);

            var current = placements.FirstOrDefault(p =>
                p.Kind == StashEntryKind.Stack && p.Container == req.ToContainer && p.TemplateId == templateId);
            var qty = current?.Quantity ?? 0;
            if (qty <= 0)
                throw new DomainException(ErrorCode.PlacementInvalid, "해당 컨테이너에 스택이 없습니다.");
            await repo.UpsertStackPlacementAsync(PlayerId, req.ToContainer, templateId, req.X, req.Y, qty);
            return;
        }

        // 컨테이너 간 이동(반입/반출).
        var source = placements.FirstOrDefault(p =>
            p.Kind == StashEntryKind.Stack && p.Container == req.FromContainer && p.TemplateId == templateId);
        if (source is null || source.Quantity <= 0)
            throw new DomainException(ErrorCode.PlacementInvalid, "원본 컨테이너에 해당 스택이 없습니다.");

        var moveQty = req.Quantity ?? source.Quantity;
        if (moveQty < 1 || moveQty > source.Quantity)
            throw new DomainException(ErrorCode.ValidationError, "이동 수량이 원본 컨테이너의 보유 수량 범위를 벗어납니다.");

        // 대상 컨테이너에 같은 스택 칸이 없으면 새 칸을 만드므로 경계+겹침을 검증한다(있으면 기존 칸에 수량만 가산).
        var destExisting = placements.FirstOrDefault(p =>
            p.Kind == StashEntryKind.Stack && p.Container == req.ToContainer && p.TemplateId == templateId);
        if (destExisting is null)
        {
            if (!StashGeometry.InBounds(req.ToContainer, target))
                throw new DomainException(ErrorCode.PlacementInvalid, "배치가 대상 컨테이너 경계를 벗어납니다.");
            EnsureNoOverlap(req.ToContainer, target, placements, footprints, isSelf: _ => false);
        }

        await repo.MoveStackAcrossContainersAsync(PlayerId, templateId, req.FromContainer, req.ToContainer, moveQty, req.X, req.Y);
    }

    /// <summary>대상 컨테이너의 다른 배치와 겹치면 PlacementInvalid. isSelf로 이동 대상 자신은 제외.</summary>
    private static void EnsureNoOverlap(
        GridContainer container, Rect target, IEnumerable<StashPlacementRow> placements,
        Dictionary<int, (int W, int H)> footprints, Func<StashPlacementRow, bool> isSelf)
    {
        foreach (var p in placements)
        {
            if (p.Container != container || isSelf(p)) continue;
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
    ///   - 스택 총 배치량 &gt; 소유량: 초과분을 STASH→LOADOUT 순으로 제거(매도/소비 반영)
    ///   - 미배치 인스턴스: STASH에 first-fit 자동 배치
    /// </summary>
    private async Task ReconcileAsync(
        Dictionary<int, int> ownedStacks, Dictionary<Guid, int> ownedInstances,
        Dictionary<int, (int W, int H)> footprints)
    {
        var placements = (await repo.GetStashPlacementsAsync(PlayerId)).ToList();

        // 1) 더 이상 소유하지 않는 배치 정리.
        foreach (var p in placements)
        {
            if (p.Kind == StashEntryKind.Stack)
            {
                if (!ownedStacks.ContainsKey(p.TemplateId))
                    await repo.DeleteStackPlacementAsync(PlayerId, p.Container, p.TemplateId);
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
            else // allocated > total: 초과분 제거(STASH 먼저, 그다음 LOADOUT).
            {
                var surplus = allocated - total;
                foreach (var c in new[] { GridContainer.Stash, GridContainer.Loadout })
                {
                    if (surplus <= 0) break;
                    var p = ps.FirstOrDefault(x => x.Container == c);
                    if (p is null) continue;
                    var take = Math.Min(surplus, p.Quantity);
                    surplus -= take;
                    var newQty = p.Quantity - take;
                    if (newQty == 0)
                        await repo.DeleteStackPlacementAsync(PlayerId, c, template);
                    else
                        await repo.UpsertStackPlacementAsync(PlayerId, c, template, p.X, p.Y, newQty);
                }
            }
        }

        // 3) 미배치 인스턴스 → STASH 자동 배치.
        foreach (var (id, template) in ownedInstances.OrderBy(i => i.Value).ThenBy(i => i.Key))
        {
            if (liveInstances.Any(p => p.InstanceId == id)) continue;
            var (w, h) = footprints.GetValueOrDefault(template, (1, 1));
            var fit = StashGeometry.FirstFit(GridContainer.Stash, stashOccupied, w, h);
            if (fit is null) continue; // 자리 없음 → Unplaced
            var (x, y) = fit.Value;
            stashOccupied.Add(new Rect(x, y, w, h));
            await repo.UpsertInstancePlacementAsync(PlayerId, GridContainer.Stash, template, id, x, y);
        }
    }

    /// <summary>정합화된 배치에서 요청 컨테이너 스냅샷을 만든다. Unplaced는 STASH 자동 배치의 안전망이라 STASH 뷰에서만 계산.</summary>
    private StashDto BuildSnapshot(
        GridContainer container, IReadOnlyList<StashPlacementRow> placements,
        Dictionary<int, int> ownedStacks, Dictionary<Guid, int> ownedInstances,
        Dictionary<int, (int W, int H)> footprints)
    {
        var (gw, gh) = StashGeometry.Dims(container);
        var placed = new List<StashPlacementDto>();

        foreach (var p in placements.Where(p => p.Container == container))
        {
            if (p.Kind == StashEntryKind.Stack)
            {
                if (!ownedStacks.ContainsKey(p.TemplateId)) continue;
                placed.Add(new StashPlacementDto(container, StashEntryKind.Stack, p.TemplateId, null, p.X, p.Y, 1, 1, p.Quantity));
            }
            else if (p.InstanceId is { } iid && ownedInstances.ContainsKey(iid))
            {
                var (w, h) = footprints.GetValueOrDefault(p.TemplateId, (1, 1));
                placed.Add(new StashPlacementDto(container, StashEntryKind.Instance, p.TemplateId, iid, p.X, p.Y, w, h, 1));
            }
        }

        var unplaced = new List<StashPlacementDto>();
        if (container == GridContainer.Stash)
        {
            foreach (var (template, total) in ownedStacks)
            {
                var allocated = placements
                    .Where(p => p.Kind == StashEntryKind.Stack && p.TemplateId == template).Sum(p => p.Quantity);
                if (allocated < total)
                    unplaced.Add(new StashPlacementDto(container, StashEntryKind.Stack, template, null, 0, 0, 1, 1, total - allocated));
            }
            foreach (var (id, template) in ownedInstances)
            {
                if (placements.Any(p => p.InstanceId == id)) continue;
                var (w, h) = footprints.GetValueOrDefault(template, (1, 1));
                unplaced.Add(new StashPlacementDto(container, StashEntryKind.Instance, template, id, 0, 0, w, h, 1));
            }
        }

        return new StashDto(PlayerId, container, gw, gh, placed, unplaced);
    }

    /// <summary>템플릿 → (grid_w, grid_h) footprint 맵. 카탈로그에서 로드.</summary>
    private async Task<Dictionary<int, (int W, int H)>> LoadFootprintsAsync()
    {
        var catalog = await repo.GetCatalogAsync();
        return catalog.ToDictionary(t => t.Id, t => (t.GridW, t.GridH));
    }
}

using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Stash;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;

namespace ItemMarket.Grains.Grains;

/// <summary>
/// 스태시 grain(키 = playerId). ===== 동시성 =====
///
/// WalletGrain/PlayerInventoryGrain과 같은 무상태(pass-through) 설계다. 그리드를
/// 인메모리에 캐시하지 않고 매 호출마다 Postgres(소스오브트루스)에서 배치·인벤·카탈로그를
/// 읽어 재구성한다. Orleans는 플레이어당 grain 활성화를 단일로 보장하고 기본 non-reentrant라,
/// 같은 플레이어의 GetStash/MoveItem 요청은 자동으로 직렬화된다 → 이동 검증(경계·겹침)과
/// 영속화 사이에 다른 이동이 끼어들 수 없어 별도 락이 필요 없다.
/// </summary>
public sealed class StashGrain(MarketRepository repo) : Grain, IStashGrain
{
    private Guid PlayerId => this.GetPrimaryKey();

    public async Task<StashDto> GetStash()
    {
        var inv = await repo.GetInventoryAsync(PlayerId);
        var footprints = await LoadFootprintsAsync();
        var placements = (await repo.GetStashPlacementsAsync(PlayerId)).ToList();

        // 소유 아이템 → 배치 대상 엔트리(스택형은 1×1, 유니크는 템플릿 footprint).
        var ownedStacks = inv.Stacks.Where(s => s.Quantity > 0)
            .ToDictionary(s => s.TemplateId, s => s.Quantity);
        var ownedInstances = inv.Instances.ToDictionary(i => i.Id, i => i.TemplateId);

        // 더 이상 소유하지 않는 배치는 정리(DB에서 삭제).
        var existingStackPos = new Dictionary<int, (int X, int Y)>();
        var existingInstancePos = new Dictionary<Guid, (int X, int Y)>();
        foreach (var p in placements)
        {
            if (p.Kind == StashEntryKind.Stack)
            {
                if (ownedStacks.ContainsKey(p.TemplateId))
                    existingStackPos[p.TemplateId] = (p.X, p.Y);
                else
                    await repo.DeleteStackPlacementAsync(PlayerId, p.TemplateId);
            }
            else if (p.InstanceId is { } iid && ownedInstances.ContainsKey(iid))
            {
                existingInstancePos[iid] = (p.X, p.Y);
            }
            else if (p.InstanceId is { } stale)
            {
                await repo.DeleteInstancePlacementAsync(PlayerId, stale);
            }
        }

        var placed = new List<StashPlacementDto>();
        var occupied = new List<Rect>();
        var toAutoPlace = new List<(StashEntryKind Kind, int TemplateId, Guid? InstanceId, int W, int H, int Quantity)>();

        // 1) 기존 배치를 그대로 유지(자리 보존).
        foreach (var (templateId, qty) in ownedStacks.OrderBy(s => s.Key))
        {
            if (existingStackPos.TryGetValue(templateId, out var pos))
            {
                var rect = new Rect(pos.X, pos.Y, 1, 1);
                occupied.Add(rect);
                placed.Add(new StashPlacementDto(StashEntryKind.Stack, templateId, null, rect.X, rect.Y, 1, 1, qty));
            }
            else
            {
                toAutoPlace.Add((StashEntryKind.Stack, templateId, null, 1, 1, qty));
            }
        }
        foreach (var (instanceId, templateId) in ownedInstances.OrderBy(i => i.Value).ThenBy(i => i.Key))
        {
            var (w, h) = footprints.GetValueOrDefault(templateId, (1, 1));
            if (existingInstancePos.TryGetValue(instanceId, out var pos))
            {
                var rect = new Rect(pos.X, pos.Y, w, h);
                occupied.Add(rect);
                placed.Add(new StashPlacementDto(StashEntryKind.Instance, templateId, instanceId, rect.X, rect.Y, w, h, 1));
            }
            else
            {
                toAutoPlace.Add((StashEntryKind.Instance, templateId, instanceId, w, h, 1));
            }
        }

        // 2) 미배치 아이템을 first-fit으로 자동 배치·영속화. 안 들어가면 Unplaced.
        var unplaced = new List<StashPlacementDto>();
        foreach (var e in toAutoPlace)
        {
            var fit = StashGeometry.FirstFit(occupied, e.W, e.H);
            if (fit is null)
            {
                unplaced.Add(new StashPlacementDto(e.Kind, e.TemplateId, e.InstanceId, 0, 0, e.W, e.H, e.Quantity));
                continue;
            }

            var (x, y) = fit.Value;
            occupied.Add(new Rect(x, y, e.W, e.H));
            if (e.Kind == StashEntryKind.Stack)
                await repo.UpsertStackPlacementAsync(PlayerId, e.TemplateId, x, y);
            else
                await repo.UpsertInstancePlacementAsync(PlayerId, e.TemplateId, e.InstanceId!.Value, x, y);
            placed.Add(new StashPlacementDto(e.Kind, e.TemplateId, e.InstanceId, x, y, e.W, e.H, e.Quantity));
        }

        return new StashDto(PlayerId, StashGeometry.GridW, StashGeometry.GridH, placed, unplaced);
    }

    public async Task<StashDto> MoveItem(MoveStashItemRequest req)
    {
        var inv = await repo.GetInventoryAsync(PlayerId);
        var footprints = await LoadFootprintsAsync();

        // ---- 대상 확인 + 소유권 검증 + footprint 결정 ----
        int templateId;
        int w, h;
        if (req.Kind == StashEntryKind.Stack)
        {
            if (req.TemplateId is not { } tid)
                throw new DomainException(ErrorCode.ValidationError, "스택 이동에는 TemplateId가 필요합니다.");
            var stack = inv.Stacks.FirstOrDefault(s => s.TemplateId == tid && s.Quantity > 0);
            if (stack is null)
                throw new DomainException(ErrorCode.PlacementInvalid, "해당 스택 아이템을 소유하고 있지 않습니다.");
            templateId = tid;
            (w, h) = (1, 1);
        }
        else
        {
            if (req.InstanceId is not { } iid)
                throw new DomainException(ErrorCode.ValidationError, "유니크 이동에는 InstanceId가 필요합니다.");
            var inst = inv.Instances.FirstOrDefault(i => i.Id == iid);
            if (inst is null)
                throw new DomainException(ErrorCode.PlacementInvalid, "해당 인스턴스를 소유하고 있지 않습니다.");
            templateId = inst.TemplateId;
            (w, h) = footprints.GetValueOrDefault(templateId, (1, 1));
        }

        // ---- 경계 검증 ----
        var target = new Rect(req.X, req.Y, w, h);
        if (!StashGeometry.InBounds(target))
            throw new DomainException(ErrorCode.PlacementInvalid, "배치가 스태시 그리드 경계를 벗어납니다.");

        // ---- 겹침 검증(이동 대상 자신은 제외) ----
        var placements = await repo.GetStashPlacementsAsync(PlayerId);
        foreach (var p in placements)
        {
            var isSelf = req.Kind == StashEntryKind.Stack
                ? p.Kind == StashEntryKind.Stack && p.TemplateId == templateId
                : p.InstanceId == req.InstanceId;
            if (isSelf) continue;

            var (pw, ph) = p.Kind == StashEntryKind.Stack
                ? (1, 1)
                : footprints.GetValueOrDefault(p.TemplateId, (1, 1));
            if (StashGeometry.Overlaps(target, new Rect(p.X, p.Y, pw, ph)))
                throw new DomainException(ErrorCode.PlacementInvalid, "다른 아이템과 겹칩니다.");
        }

        // ---- 영속화 ----
        if (req.Kind == StashEntryKind.Stack)
            await repo.UpsertStackPlacementAsync(PlayerId, templateId, req.X, req.Y);
        else
            await repo.UpsertInstancePlacementAsync(PlayerId, templateId, req.InstanceId!.Value, req.X, req.Y);

        return await GetStash();
    }

    /// <summary>템플릿 → (grid_w, grid_h) footprint 맵. 카탈로그에서 로드.</summary>
    private async Task<Dictionary<int, (int W, int H)>> LoadFootprintsAsync()
    {
        var catalog = await repo.GetCatalogAsync();
        return catalog.ToDictionary(t => t.Id, t => (t.GridW, t.GridH));
    }
}

using Dapper;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Equipment;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Leaderboard;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Raid;
using ItemMarket.Contracts.Stash;
using ItemMarket.Contracts.Trades;
using ItemMarket.Contracts.Wallet;
using Npgsql;

namespace ItemMarket.Grains.Data;

// (partial) 스태시(그리드 인벤토리)·장비 슬롯·중첩 컨테이너
public sealed partial class MarketRepository
{

    /// <summary>
    /// 스태시 행 확장(+6행) 구매 — 단일 트랜잭션(캡 싱크). 현재 stash_rows로 점증 가격을 계산해
    /// 잔액에서 차감(음수/부족 방어)하고 wallet_ledger에 STASH_UPGRADE로 회계한 뒤 stash_rows를 늘린다.
    /// 상한(500) 초과는 ValidationError, 잔액 부족은 InsufficientFunds로 거부(원자적 롤백).
    /// </summary>
    public async Task<StashUpgradeResultDto> UpgradeStashRowsAsync(Guid playerId)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            var rows = await db.ExecuteScalarAsync<int?>(
                "SELECT stash_rows FROM player WHERE id = @playerId FOR UPDATE", new { playerId }, tx);
            if (rows is null)
                throw new DomainException(ErrorCode.PlayerNotFound, "플레이어를 찾을 수 없습니다.");
            if (rows.Value + StashRowsPerUpgrade > StashRowsMax)
                throw new DomainException(ErrorCode.ValidationError, $"창고가 이미 최대 크기({StashRowsMax}행)입니다.");

            var cost = StashUpgradeCost(rows.Value);
            var balance = await db.ExecuteScalarAsync<long>(
                "SELECT balance FROM wallet WHERE player_id = @playerId FOR UPDATE", new { playerId }, tx);
            if (balance < cost)
                throw new DomainException(ErrorCode.InsufficientFunds, $"창고 확장에 {cost} 캡이 필요합니다(보유 {balance}).");

            var after = balance - cost;
            await db.ExecuteAsync("UPDATE wallet SET balance = @after WHERE player_id = @playerId",
                new { after, playerId }, tx);
            await InsertLedgerAsync(db, tx, playerId, -cost, after, WalletLedgerReason.StashUpgrade, null);

            var newRows = rows.Value + StashRowsPerUpgrade;
            await db.ExecuteAsync("UPDATE player SET stash_rows = @newRows WHERE id = @playerId",
                new { newRows, playerId }, tx);

            await tx.CommitAsync();
            return new StashUpgradeResultDto(newRows, cost, after);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ======================================================================
    //  스태시(그리드 인벤토리) — 컨테이너(STASH/POCKETS/CONTAINER) 인지
    //  Postgres가 소스오브트루스. 스택은 이제 (player, 물리 컨테이너, template) 당 여러 칸(다중 스택)을
    //  가질 수 있다(각 칸은 template.max_stack 상한). 유니크는 instance 단위로 전역 유일(정확히 한 컨테이너+한 칸).
    //  소유 수량 자체의 진실은 inventory_stack/item_instance이며, 배치는 조직화(위치/컨테이너)일 뿐.
    // ======================================================================

    /// <summary>플레이어의 모든 컨테이너 스태시 배치를 로드(중첩 컨테이너 배치 포함). 순서는 (y,x) 안정 정렬.</summary>
    public async Task<IReadOnlyList<StashPlacementRow>> GetStashPlacementsAsync(Guid playerId)
    {
        await using var db = Open();
        var rows = await db.QueryAsync(
            @"SELECT container, kind, template_id, instance_id, container_instance_id, x, y, quantity
              FROM stash_placement WHERE player_id = @playerId
              ORDER BY container, container_instance_id, y, x",
            new { playerId });
        return rows.Select(r => new StashPlacementRow(
            Enums.ToContainer((string)r.container), Enums.ToStashKind((string)r.kind),
            (int)r.template_id, (Guid?)r.instance_id,
            (int)r.x, (int)r.y, (int)r.quantity, (Guid?)r.container_instance_id)).ToList();
    }

    /// <summary>
    /// 스택형 배치 upsert: 정확히 한 칸(player, 물리 컨테이너, x, y)의 수량을 절대값으로 설정한다.
    /// 다중 스택 지원 후에는 같은 (컨테이너, template) 조합이 여러 칸에 존재할 수 있으므로,
    /// 갱신 대상은 반드시 (x, y)까지 일치하는 그 칸이다(위치 이동에는 쓰지 않는다 — 그건 MoveStackAsync 담당).
    /// containerInstanceId는 중첩 컨테이너(container=Container)일 때만 지정, STASH/POCKETS은 null.
    /// grain이 플레이어당 직렬화되므로 update-then-insert가 안전(경합 없음).
    /// </summary>
    public async Task UpsertStackPlacementAsync(Guid playerId, GridContainer container, int templateId, int x, int y, int quantity, Guid? containerInstanceId = null)
    {
        await using var db = Open();
        await UpsertStackPlacementAsync(db, null, playerId, container, templateId, x, y, quantity, containerInstanceId);
    }

    private static async Task UpsertStackPlacementAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, Guid playerId, GridContainer container,
        int templateId, int x, int y, int quantity, Guid? containerInstanceId)
    {
        var updated = await db.ExecuteAsync(
            @"UPDATE stash_placement SET quantity = @quantity
              WHERE player_id = @playerId AND container = @container AND kind = 'STACK' AND template_id = @templateId
                    AND container_instance_id IS NOT DISTINCT FROM @cid AND x = @x AND y = @y",
            new { playerId, container = container.ToDb(), templateId, x, y, quantity, cid = containerInstanceId }, tx);
        if (updated == 0)
            await db.ExecuteAsync(
                @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
                  VALUES (@playerId, @container, 'STACK', @templateId, NULL, @cid, @x, @y, @quantity)",
                new { playerId, container = container.ToDb(), templateId, cid = containerInstanceId, x, y, quantity }, tx);
    }

    /// <summary>유니크 배치 upsert: 인스턴스 단위로 전역 유일. 컨테이너+위치 갱신.</summary>
    public async Task UpsertInstancePlacementAsync(Guid playerId, GridContainer container, int templateId, Guid instanceId, int x, int y, Guid? containerInstanceId = null)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
              VALUES (@playerId, @container, 'INSTANCE', @templateId, @instanceId, @cid, @x, @y, 1)
              ON CONFLICT (instance_id)
              DO UPDATE SET container = EXCLUDED.container, container_instance_id = EXCLUDED.container_instance_id,
                            x = EXCLUDED.x, y = EXCLUDED.y, player_id = EXCLUDED.player_id",
            new { playerId, container = container.ToDb(), templateId, instanceId, cid = containerInstanceId, x, y });
    }

    /// <summary>사용자의 유니크 인스턴스 이동(MoveItem 경로) — 레이드 시작과 DB 레벨 직렬화 후 ACTIVE 재확인해
    /// 변이를 거부한다(F-1). 정합화(ReconcileAsync)의 자동 배치는 레이드 중에도 STASH-안전 아이템을 재배치해야
    /// 하므로 락 없는 UpsertInstancePlacementAsync를 그대로 쓴다 — 사용자 이동만 이 잠금 경로를 탄다.</summary>
    public async Task MoveInstancePlacementAsync(Guid playerId, GridContainer container, int templateId, Guid instanceId, int x, int y, Guid? containerInstanceId = null)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            await LockPlayerAsync(db, tx, playerId);
            await ThrowIfActiveRaidAsync(db, tx, playerId);
            await db.ExecuteAsync(
                @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
                  VALUES (@playerId, @container, 'INSTANCE', @templateId, @instanceId, @cid, @x, @y, 1)
                  ON CONFLICT (instance_id)
                  DO UPDATE SET container = EXCLUDED.container, container_instance_id = EXCLUDED.container_instance_id,
                                x = EXCLUDED.x, y = EXCLUDED.y, player_id = EXCLUDED.player_id",
                new { playerId, container = container.ToDb(), templateId, instanceId, cid = containerInstanceId, x, y }, tx);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 특정 물리 컨테이너의 스택형 배치 삭제. x/y를 지정하면 그 정확한 칸 하나만(다중 스택 중 하나가
    /// 0이 됐을 때), 생략하면 그 컨테이너의 해당 템플릿 스택 전부(더 이상 소유하지 않을 때 일괄 정리).
    /// </summary>
    public async Task DeleteStackPlacementAsync(Guid playerId, GridContainer container, int templateId, Guid? containerInstanceId = null, int? x = null, int? y = null)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            @"DELETE FROM stash_placement WHERE player_id = @playerId AND container = @container AND kind = 'STACK'
              AND template_id = @templateId AND container_instance_id IS NOT DISTINCT FROM @cid
              AND (@x::int IS NULL OR x = @x) AND (@y::int IS NULL OR y = @y)",
            new { playerId, container = container.ToDb(), templateId, cid = containerInstanceId, x, y });
    }

    /// <summary>더 이상 소유하지 않는 유니크 배치 정리(컨테이너 무관, 전역 유일).</summary>
    public async Task DeleteInstancePlacementAsync(Guid playerId, Guid instanceId)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            "DELETE FROM stash_placement WHERE player_id = @playerId AND kind = 'INSTANCE' AND instance_id = @instanceId",
            new { playerId, instanceId });
    }

    /// <summary>
    /// 다중 스택 지원 이동(같은 컨테이너 재배치 + 컨테이너 간 반입/반출을 하나로 통일). 한 트랜잭션으로:
    ///   1) 원본 물리 컨테이너(from/fromCid)에 있는 이 template의 모든 칸(= 풀)을 잠그고 합계를 낸다.
    ///      (다중 스택은 서로 교환 가능한 수량 뭉치로 취급 — 어느 칸에서 얼마나 빠지는지는 UX상 의미가
    ///      없고, 총량 보존만 중요하다.) requestedQty가 없으면 그 풀 전체를 이동 대상으로 삼는다.
    ///   2) 목적지 칸(to/toCid의 정확한 toX,toY)에 이미 같은 template 스택이 있으면 그 칸이 병합 대상,
    ///      없으면 새 칸이다. 이동량은 min(requestedQty, maxStack - 목적지 기존 수량)으로 캡핑되고,
    ///      캡을 넘는 초과분은 원본 풀에서 애초에 빼지 않는다("초과분은 원본에 남는다" — 부분 병합).
    ///   3) 목적지 칸이 곧 원본 풀의 칸 중 하나(같은 물리 컨테이너 안에서 스스로에게 병합/재배치)이면
    ///      그 칸은 "풀"에서 제외한다 — 자기 자신에게서 빼서 자기 자신에게 넣는 자기상쇄를 방지한다.
    /// 목적지 칸에 다른 템플릿/유니크가 있으면 호출자(grain)가 사전에 걸러낸다(여기서는 unique violation을
    /// PlacementInvalid로 방어 변환).
    /// </summary>
    public async Task MoveStackAsync(
        Guid playerId, int templateId, GridContainer from, Guid? fromCid,
        GridContainer to, Guid? toCid, int toX, int toY, int? requestedQty, int maxStack)
    {
        // 수량이 지정됐으면 하한(≥1)을 두 분기 공통으로 검증한다 — 빈 풀 분기가 음수·0 요청을
        // 조용히 no-op 성공으로 흘려보내던 계약 비일관을 없앤다(L2/BUG4). 상한은 분기별로 검증.
        if (requestedQty is { } rq && rq < 1)
            throw new DomainException(ErrorCode.ValidationError, "이동 수량은 1 이상이어야 합니다.");

        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            await LockPlayerAsync(db, tx, playerId);        // F-1: 레이드 시작과 DB 레벨 직렬화
            await ThrowIfActiveRaidAsync(db, tx, playerId);

            var fromDb = from.ToDb();
            var toDb = to.ToDb();
            var sourceRows = (await db.QueryAsync(
                @"SELECT x, y, quantity FROM stash_placement
                  WHERE player_id = @playerId AND container = @fromDb AND kind = 'STACK' AND template_id = @templateId
                        AND container_instance_id IS NOT DISTINCT FROM @fromCid
                  ORDER BY y, x FOR UPDATE",
                new { playerId, fromDb, templateId, fromCid }, tx))
                .Select(r => (X: (int)r.x, Y: (int)r.y, Quantity: (int)r.quantity))
                .ToList();

            var sameContainer = from == to && fromCid == toCid;
            var destRowIsSource = sameContainer ? sourceRows.FirstOrDefault(r => r.X == toX && r.Y == toY) : default;
            var destInPool = sameContainer && sourceRows.Any(r => r.X == toX && r.Y == toY);

            // 목적지가 원본 풀의 칸이면(자기 자신) 그 칸을 풀 계산에서 제외한다.
            var poolRows = destInPool ? sourceRows.Where(r => !(r.X == toX && r.Y == toY)).ToList() : sourceRows;
            var poolTotal = poolRows.Sum(r => r.Quantity);

            // 목적지 기존 수량: 자기 자신 칸이면 방금 뺀 그 값, 아니면 별도 조회(다른 템플릿/유니크면 0 취급 —
            // 실제 충돌은 grain이 사전 검증하고, 여기선 INSERT 시 uq_stash_cell 위반으로 최종 방어).
            int destExistingQty;
            if (destInPool)
            {
                destExistingQty = destRowIsSource.Quantity;
            }
            else
            {
                destExistingQty = await db.ExecuteScalarAsync<int?>(
                    @"SELECT quantity FROM stash_placement
                      WHERE player_id = @playerId AND container = @toDb AND kind = 'STACK' AND template_id = @templateId
                            AND container_instance_id IS NOT DISTINCT FROM @toCid AND x = @toX AND y = @toY
                      FOR UPDATE",
                    new { playerId, toDb, templateId, toCid, toX, toY }, tx) ?? 0;
            }

            if (poolRows.Count == 0)
            {
                // 이동할 다른 칸이 없다(자기 자신 위로의 이동 등) — 사실상 no-op. 요청량이 이미 그 자리에
                // 있는 수량을 넘지 않으면 조용히 성공, 넘으면 검증 오류.
                var want = requestedQty ?? destExistingQty;
                if (want > destExistingQty)
                    throw new DomainException(ErrorCode.ValidationError, "이동 수량이 원본 컨테이너의 보유 수량 범위를 벗어납니다.");
                await tx.CommitAsync();
                return;
            }

            if (poolTotal <= 0)
                throw new DomainException(ErrorCode.PlacementInvalid, "원본 컨테이너에 해당 스택이 없습니다.");

            var qty = requestedQty ?? poolTotal;
            if (qty < 1 || qty > poolTotal)
                throw new DomainException(ErrorCode.ValidationError, "이동 수량이 원본 컨테이너의 보유 수량 범위를 벗어납니다.");

            var capacity = Math.Max(0, maxStack - destExistingQty);
            var actualMove = Math.Min(qty, capacity);
            if (actualMove <= 0)
                throw new DomainException(ErrorCode.PlacementInvalid, "대상 칸이 이미 가득 찼습니다.");

            // 원본 풀에서 actualMove만큼 (y,x) 순서로 차감(오버플로는 자연히 풀에 남는다).
            var remaining = actualMove;
            foreach (var r in poolRows)
            {
                if (remaining <= 0) break;
                var take = Math.Min(remaining, r.Quantity);
                var newQty = r.Quantity - take;
                if (newQty == 0)
                    await db.ExecuteAsync(
                        @"DELETE FROM stash_placement WHERE player_id = @playerId AND container = @fromDb AND kind = 'STACK'
                          AND template_id = @templateId AND container_instance_id IS NOT DISTINCT FROM @fromCid AND x = @X AND y = @Y",
                        new { playerId, fromDb, templateId, fromCid, r.X, r.Y }, tx);
                else
                    await db.ExecuteAsync(
                        @"UPDATE stash_placement SET quantity = @newQty WHERE player_id = @playerId AND container = @fromDb
                          AND kind = 'STACK' AND template_id = @templateId AND container_instance_id IS NOT DISTINCT FROM @fromCid
                          AND x = @X AND y = @Y",
                        new { newQty, playerId, fromDb, templateId, fromCid, r.X, r.Y }, tx);
                remaining -= take;
            }

            // 목적지 upsert(actualMove만큼 가산 또는 신규 생성).
            if (destExistingQty > 0)
                await db.ExecuteAsync(
                    @"UPDATE stash_placement SET quantity = quantity + @actualMove
                      WHERE player_id = @playerId AND container = @toDb AND kind = 'STACK' AND template_id = @templateId
                        AND container_instance_id IS NOT DISTINCT FROM @toCid AND x = @toX AND y = @toY",
                    new { actualMove, playerId, toDb, templateId, toCid, toX, toY }, tx);
            else
                await db.ExecuteAsync(
                    @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
                      VALUES (@playerId, @toDb, 'STACK', @templateId, NULL, @toCid, @toX, @toY, @actualMove)",
                    new { playerId, toDb, templateId, toCid, toX, toY, actualMove }, tx);

            await tx.CommitAsync();
        }
        catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync();
            throw new DomainException(ErrorCode.PlacementInvalid, "다른 아이템과 겹칩니다.");
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ======================================================================
    //  장비(equipment) — 슬롯 → 인스턴스 매핑 + 중첩 컨테이너(백팩/리그) 지원.
    //  장착된 인스턴스는 소유(owner=player) 상태이나 스태시 그리드에는 배치되지 않는다
    //  (인형 위에 있음 → StashGrain 정합화가 이 인스턴스를 STASH로 자동 배치하지 않도록 제외).
    // ======================================================================

    /// <summary>플레이어의 장착 슬롯 목록(slot → instance + template).</summary>
    public async Task<IReadOnlyList<EquipmentSlotRow>> GetEquipmentAsync(Guid playerId)
    {
        await using var db = Open();
        var rows = await db.QueryAsync(
            @"SELECT pe.slot, pe.instance_id, ii.template_id
              FROM player_equipment pe
              JOIN item_instance ii ON ii.id = pe.instance_id
              WHERE pe.player_id = @playerId AND ii.owner_player_id = @playerId
              ORDER BY pe.slot",
            new { playerId });
        return rows.Select(r => new EquipmentSlotRow(
            Enums.ToEquipSlot((string)r.slot), (Guid)r.instance_id, (int)r.template_id)).ToList();
    }

    /// <summary>
    /// 장착(한 트랜잭션): 소유 인스턴스를 호환 슬롯에 장착. 검증 —
    ///   인스턴스 존재/소유, template.equip_slot == 요청 슬롯, 슬롯 미점유. 위반 시 도메인 에러.
    /// 성공 시 그 인스턴스의 스태시 배치를 제거한다(인형 위로 이동).
    /// </summary>
    public async Task EquipAsync(Guid playerId, EquipSlot slot, Guid instanceId)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            // 레이드 시작과 DB 레벨 직렬화 후 레이드 중 변이 거부(F-1 — grain 경계 TOCTOU 차단).
            await LockPlayerAsync(db, tx, playerId);
            await ThrowIfActiveRaidAsync(db, tx, playerId);

            var inst = await db.QuerySingleOrDefaultAsync(
                "SELECT owner_player_id, template_id FROM item_instance WHERE id = @instanceId FOR UPDATE",
                new { instanceId }, tx);
            if (inst is null)
                throw new DomainException(ErrorCode.InstanceNotFound, "인스턴스를 찾을 수 없습니다.");
            if ((Guid?)inst.owner_player_id != playerId)
                throw new DomainException(ErrorCode.InstanceNotOwned, "해당 인스턴스를 소유하고 있지 않습니다.");

            int templateId = (int)inst.template_id;
            var equipSlot = await db.ExecuteScalarAsync<string?>(
                "SELECT equip_slot FROM item_template WHERE id = @templateId", new { templateId }, tx);
            if (equipSlot is null || Enums.ToEquipSlot(equipSlot) != slot)
                throw new DomainException(ErrorCode.SlotMismatch, "이 아이템은 해당 슬롯에 장착할 수 없습니다.");

            var occupied = await db.ExecuteScalarAsync<Guid?>(
                "SELECT instance_id FROM player_equipment WHERE player_id = @playerId AND slot = @slot",
                new { playerId, slot = slot.ToDb() }, tx);
            if (occupied is not null)
                throw new DomainException(ErrorCode.SlotMismatch, "슬롯이 이미 사용 중입니다. 먼저 해제하세요.");

            await db.ExecuteAsync(
                "INSERT INTO player_equipment(player_id, slot, instance_id) VALUES (@playerId, @slot, @instanceId)",
                new { playerId, slot = slot.ToDb(), instanceId }, tx);
            // 장착된 인스턴스는 그리드에서 제거(인형 위로 이동) → 정합화가 다시 STASH에 놓지 않도록.
            await db.ExecuteAsync(
                "DELETE FROM stash_placement WHERE player_id = @playerId AND kind = 'INSTANCE' AND instance_id = @instanceId",
                new { playerId, instanceId }, tx);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 해제(한 트랜잭션): 슬롯을 비운다. 해제된 인스턴스는 소유 상태로 남아 다음 GET /api/stash에서
    /// STASH에 자동 배치된다. 백팩/리그였다면 내용물 배치도 제거해 STASH로 회수되게 한다(유실 없음).
    /// </summary>
    public async Task UnequipAsync(Guid playerId, EquipSlot slot)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            await LockPlayerAsync(db, tx, playerId);        // F-1: 레이드 시작과 DB 레벨 직렬화
            await ThrowIfActiveRaidAsync(db, tx, playerId);

            var instanceId = await db.ExecuteScalarAsync<Guid?>(
                "SELECT instance_id FROM player_equipment WHERE player_id = @playerId AND slot = @slot FOR UPDATE",
                new { playerId, slot = slot.ToDb() }, tx);
            if (instanceId is null)
                throw new DomainException(ErrorCode.InstanceNotFound, "해당 슬롯에 장착된 아이템이 없습니다.");

            // 중첩 컨테이너였다면 내용물 배치 제거(소유는 유지 → STASH로 자동 회수).
            await db.ExecuteAsync(
                "DELETE FROM stash_placement WHERE player_id = @playerId AND container_instance_id = @cid",
                new { playerId, cid = instanceId.Value }, tx);
            await db.ExecuteAsync(
                "DELETE FROM player_equipment WHERE player_id = @playerId AND slot = @slot",
                new { playerId, slot = slot.ToDb() }, tx);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}

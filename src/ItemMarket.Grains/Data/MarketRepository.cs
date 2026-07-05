using Dapper;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Stash;
using ItemMarket.Contracts.Trades;
using ItemMarket.Contracts.Wallet;
using Npgsql;

namespace ItemMarket.Grains.Data;

/// <summary>
/// Postgres 데이터 접근(Dapper). 얇은 리포지토리 + "정산" 트랜잭션 한 개.
/// Postgres가 소스오브트루스이며 grain은 여기를 통해 상태를 읽고 쓴다.
/// </summary>
public sealed class MarketRepository(string connectionString)
{
    private NpgsqlConnection Open()
    {
        var c = new NpgsqlConnection(connectionString);
        c.Open();
        return c;
    }

    /// <summary>
    /// 수수료 계산의 단일 지점. gross(체결 총액) × bps / 10000.
    /// 중간 곱을 Int128로 수행해 long 오버플로를 차단한다(결과는 gross 이하라 안전).
    /// </summary>
    public static long CalcFee(long execPrice, int quantity, int feeBps)
        => (long)((Int128)execPrice * quantity * feeBps / 10000);

    // ======================================================================
    //  설정 / 플레이어 / 카탈로그
    // ======================================================================
    public async Task<int> GetFeeBpsAsync()
    {
        await using var db = Open();
        var v = await db.ExecuteScalarAsync<string?>(
            "SELECT value FROM market_config WHERE key = 'fee_bps'");
        return int.TryParse(v, out var bps) ? bps : 500;
    }

    public async Task<PlayerRow?> GetPlayerAsync(Guid id)
    {
        await using var db = Open();
        var row = await db.QuerySingleOrDefaultAsync(
            "SELECT id, display_name FROM player WHERE id = @id", new { id });
        return row is null ? null : new PlayerRow((Guid)row.id, (string)row.display_name);
    }

    public async Task<IReadOnlyList<ItemTemplateDto>> GetCatalogAsync()
    {
        await using var db = Open();
        var rows = await db.QueryAsync(
            @"SELECT id, code, name, category, rarity, stackable, max_durability, icon, base_value, grid_w, grid_h
              FROM item_template ORDER BY id");
        return rows.Select(r => new ItemTemplateDto(
            (int)r.id, (string)r.code, (string)r.name,
            Enums.ToCategory((string)r.category), Enums.ToRarity((string)r.rarity),
            (bool)r.stackable, (int?)r.max_durability, (string)r.icon, (long)r.base_value,
            (int)r.grid_w, (int)r.grid_h)).ToList();
    }

    public async Task<TemplateRow?> GetTemplateAsync(int id)
    {
        await using var db = Open();
        var row = await db.QuerySingleOrDefaultAsync(
            "SELECT id, stackable FROM item_template WHERE id = @id", new { id });
        return row is null ? null : new TemplateRow((int)row.id, (bool)row.stackable);
    }

    // ======================================================================
    //  지갑
    // ======================================================================
    public async Task<WalletDto> GetWalletAsync(Guid playerId)
    {
        await using var db = Open();
        var bal = await db.ExecuteScalarAsync<long?>(
            "SELECT balance FROM wallet WHERE player_id = @playerId", new { playerId });
        return new WalletDto(playerId, bal ?? 0);
    }

    /// <summary>매수 주문 등록 시 대금 잠금. 잔액 부족이면 false(원장 미기록).</summary>
    public async Task<bool> TryEscrowCapsAsync(Guid playerId, long amount, Guid orderId)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();

        var balance = await db.ExecuteScalarAsync<long?>(
            "SELECT balance FROM wallet WHERE player_id = @playerId FOR UPDATE", new { playerId }, tx);
        if (balance is null || balance < amount)
        {
            await tx.RollbackAsync();
            return false;
        }

        var after = balance.Value - amount;
        await db.ExecuteAsync("UPDATE wallet SET balance = @after WHERE player_id = @playerId",
            new { after, playerId }, tx);
        await InsertLedgerAsync(db, tx, playerId, -amount, after, WalletLedgerReason.OrderEscrow, orderId);
        await tx.CommitAsync();
        return true;
    }

    /// <summary>취소/차익 등으로 병뚜껑 환불(+).</summary>
    public async Task RefundCapsAsync(Guid playerId, long amount, Guid refId)
    {
        if (amount <= 0) return;
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        var after = await db.ExecuteScalarAsync<long>(
            "UPDATE wallet SET balance = balance + @amount WHERE player_id = @playerId RETURNING balance",
            new { amount, playerId }, tx);
        await InsertLedgerAsync(db, tx, playerId, amount, after, WalletLedgerReason.OrderRefund, refId);
        await tx.CommitAsync();
    }

    public async Task<WalletDto> AdminAdjustAsync(Guid playerId, long delta, string reason)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        var balance = await db.ExecuteScalarAsync<long?>(
            "SELECT balance FROM wallet WHERE player_id = @playerId FOR UPDATE", new { playerId }, tx);
        if (balance is null)
        {
            await tx.RollbackAsync();
            throw new DomainException(ErrorCode.PlayerNotFound, "지갑을 찾을 수 없습니다.");
        }
        long after;
        try { after = checked(balance.Value + delta); }
        catch (OverflowException)
        {
            await tx.RollbackAsync();
            throw new DomainException(ErrorCode.ValidationError, "조정 결과가 잔액 표현 범위를 벗어납니다.");
        }
        if (after < 0)
        {
            await tx.RollbackAsync();
            throw new DomainException(ErrorCode.InsufficientFunds, "잔액이 음수가 될 수 없습니다.");
        }
        await db.ExecuteAsync("UPDATE wallet SET balance = @after WHERE player_id = @playerId",
            new { after, playerId }, tx);
        // reason 텍스트는 감사용 메모지만, 원장 태그는 ADMIN_ADJUST로 고정.
        await InsertLedgerAsync(db, tx, playerId, delta, after, WalletLedgerReason.AdminAdjust, null);
        await tx.CommitAsync();
        return new WalletDto(playerId, after);
    }

    public async Task<PagedResult<WalletLedgerEntryDto>> GetLedgerAsync(Guid playerId, int page, int size)
    {
        await using var db = Open();
        var total = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM wallet_ledger WHERE player_id = @playerId", new { playerId });
        var rows = await db.QueryAsync(
            @"SELECT id, player_id, delta, balance_after, reason, ref_id, created_at
              FROM wallet_ledger WHERE player_id = @playerId
              ORDER BY id DESC LIMIT @size OFFSET @offset",
            new { playerId, size, offset = (page - 1) * size });
        var items = rows.Select(r => new WalletLedgerEntryDto(
            (long)r.id, (Guid)r.player_id, (long)r.delta, (long)r.balance_after,
            Enums.ToReason((string)r.reason), (Guid?)r.ref_id,
            (DateTimeOffset)r.created_at)).ToList();
        return new PagedResult<WalletLedgerEntryDto>(items, page, size, total);
    }

    private static Task InsertLedgerAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid playerId, long delta, long balanceAfter, WalletLedgerReason reason, Guid? refId)
        => db.ExecuteAsync(
            @"INSERT INTO wallet_ledger(player_id, delta, balance_after, reason, ref_id)
              VALUES (@playerId, @delta, @balanceAfter, @reason, @refId)",
            new { playerId, delta, balanceAfter, reason = reason.ToDb(), refId }, tx);

    // ======================================================================
    //  인벤토리
    // ======================================================================
    public async Task<InventoryDto> GetInventoryAsync(Guid playerId)
    {
        await using var db = Open();
        var stacks = (await db.QueryAsync(
            "SELECT template_id, quantity FROM inventory_stack WHERE player_id = @playerId AND quantity > 0 ORDER BY template_id",
            new { playerId }))
            .Select(r => new InventoryStackDto((int)r.template_id, (int)r.quantity)).ToList();

        var instances = (await db.QueryAsync(
            @"SELECT id, template_id, durability, attachments, created_at
              FROM item_instance WHERE owner_player_id = @playerId ORDER BY template_id",
            new { playerId }))
            .Select(r => new ItemInstanceDto(
                (Guid)r.id, (int)r.template_id, (int?)r.durability,
                System.Text.Json.JsonSerializer.Deserialize<List<string>>((string)r.attachments) ?? [],
                (DateTimeOffset)r.created_at)).ToList();

        return new InventoryDto(playerId, stacks, instances);
    }

    /// <summary>스택형 매도 에스크로: 인벤에서 수량 차감. 부족하면 false.</summary>
    public async Task<bool> TryEscrowStackAsync(Guid playerId, int templateId, int qty)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        var have = await db.ExecuteScalarAsync<int?>(
            "SELECT quantity FROM inventory_stack WHERE player_id = @playerId AND template_id = @templateId FOR UPDATE",
            new { playerId, templateId }, tx);
        if (have is null || have < qty)
        {
            await tx.RollbackAsync();
            return false;
        }
        await db.ExecuteAsync(
            "UPDATE inventory_stack SET quantity = quantity - @qty WHERE player_id = @playerId AND template_id = @templateId",
            new { qty, playerId, templateId }, tx);
        await tx.CommitAsync();
        return true;
    }

    /// <summary>스택형 매도 취소: 수량 인벤으로 반환.</summary>
    public async Task ReturnStackAsync(Guid playerId, int templateId, int qty)
    {
        if (qty <= 0) return;
        await using var db = Open();
        await UpsertStackAsync(db, null, playerId, templateId, qty);
    }

    /// <summary>유니크 매도 에스크로: 소유·템플릿 확인 후 owner=NULL.</summary>
    public async Task<EscrowInstanceOutcome> TryEscrowInstanceAsync(Guid playerId, Guid instanceId, int templateId)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        var row = await db.QuerySingleOrDefaultAsync(
            "SELECT owner_player_id, template_id FROM item_instance WHERE id = @instanceId FOR UPDATE",
            new { instanceId }, tx);
        if (row is null) { await tx.RollbackAsync(); return EscrowInstanceOutcome.NotFound; }
        if ((int)row.template_id != templateId) { await tx.RollbackAsync(); return EscrowInstanceOutcome.TemplateMismatch; }
        var owner = (Guid?)row.owner_player_id;
        if (owner != playerId) { await tx.RollbackAsync(); return EscrowInstanceOutcome.NotOwned; }

        await db.ExecuteAsync(
            "UPDATE item_instance SET owner_player_id = NULL WHERE id = @instanceId",
            new { instanceId }, tx);
        await tx.CommitAsync();
        return EscrowInstanceOutcome.Ok;
    }

    /// <summary>유니크 매도 취소: 인스턴스 소유권 원복.</summary>
    public async Task ReturnInstanceAsync(Guid playerId, Guid instanceId)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            "UPDATE item_instance SET owner_player_id = @playerId WHERE id = @instanceId",
            new { playerId, instanceId });
    }

    public async Task<InventoryDto> AdminGrantStackAsync(Guid playerId, int templateId, int qty)
    {
        if (qty < 1 || qty > 1_000_000)
            throw new DomainException(ErrorCode.ValidationError, "지급 수량은 1 이상 1,000,000 이하이어야 합니다.");
        await using var db = Open();
        await ValidateGrantTargetAsync(db, playerId, templateId, expectStackable: true);
        await UpsertStackAsync(db, null, playerId, templateId, qty);
        return await GetInventoryAsync(playerId);
    }

    public async Task<ItemInstanceDto> AdminGrantInstanceAsync(Guid playerId, int templateId, int? durability, IReadOnlyList<string>? attachments)
    {
        if (durability is < 0)
            throw new DomainException(ErrorCode.ValidationError, "내구도는 음수일 수 없습니다.");
        await using var db = Open();
        await ValidateGrantTargetAsync(db, playerId, templateId, expectStackable: false);
        var id = Guid.NewGuid();
        var attJson = System.Text.Json.JsonSerializer.Serialize(attachments ?? []);
        var row = await db.QuerySingleAsync(
            @"INSERT INTO item_instance(id, template_id, owner_player_id, durability, attachments)
              VALUES (@id, @templateId, @playerId, @durability, @attJson::jsonb)
              RETURNING id, template_id, durability, attachments, created_at",
            new { id, templateId, playerId, durability, attJson });
        return new ItemInstanceDto(
            (Guid)row.id, (int)row.template_id, (int?)row.durability,
            System.Text.Json.JsonSerializer.Deserialize<List<string>>((string)row.attachments) ?? [],
            (DateTimeOffset)row.created_at);
    }

    /// <summary>어드민 지급 대상 검증: 플레이어 존재 + 템플릿 존재/스택 여부 일치.
    /// 검증 없이는 FK/CHECK 위반이 원시 500으로 새고, 스택형 템플릿의 유령
    /// 인스턴스(판매 불가) 같은 오염 데이터가 생긴다.</summary>
    private async Task ValidateGrantTargetAsync(NpgsqlConnection db, Guid playerId, int templateId, bool expectStackable)
    {
        var playerExists = await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM player WHERE id = @playerId)", new { playerId });
        if (!playerExists)
            throw new DomainException(ErrorCode.PlayerNotFound, "플레이어를 찾을 수 없습니다.");

        var stackable = await db.ExecuteScalarAsync<bool?>(
            "SELECT stackable FROM item_template WHERE id = @templateId", new { templateId });
        if (stackable is null)
            throw new DomainException(ErrorCode.TemplateNotFound, "아이템 템플릿을 찾을 수 없습니다.");
        if (stackable != expectStackable)
            throw new DomainException(ErrorCode.StackableMismatch, expectStackable
                ? "유니크 템플릿은 스택 지급이 불가합니다. grant/instance를 사용하세요."
                : "스택형 템플릿은 인스턴스 지급이 불가합니다. grant/stack을 사용하세요.");
    }

    private static Task UpsertStackAsync(NpgsqlConnection db, NpgsqlTransaction? tx, Guid playerId, int templateId, int qty)
        => db.ExecuteAsync(
            @"INSERT INTO inventory_stack(player_id, template_id, quantity)
              VALUES (@playerId, @templateId, @qty)
              ON CONFLICT (player_id, template_id)
              DO UPDATE SET quantity = inventory_stack.quantity + EXCLUDED.quantity",
            new { playerId, templateId, qty }, tx);

    // ======================================================================
    //  스태시(그리드 인벤토리)
    //  Postgres가 소스오브트루스. 배치는 (player, template, kind) 또는 instance 단위로 유일.
    // ======================================================================

    /// <summary>플레이어의 모든 스태시 배치를 로드.</summary>
    public async Task<IReadOnlyList<StashPlacementRow>> GetStashPlacementsAsync(Guid playerId)
    {
        await using var db = Open();
        var rows = await db.QueryAsync(
            "SELECT kind, template_id, instance_id, x, y FROM stash_placement WHERE player_id = @playerId",
            new { playerId });
        return rows.Select(r => new StashPlacementRow(
            Enums.ToStashKind((string)r.kind), (int)r.template_id, (Guid?)r.instance_id,
            (int)r.x, (int)r.y)).ToList();
    }

    /// <summary>스택형 배치 upsert: (player, template) 당 한 칸. 위치만 갱신.</summary>
    public async Task UpsertStackPlacementAsync(Guid playerId, int templateId, int x, int y)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            @"INSERT INTO stash_placement(player_id, kind, template_id, instance_id, x, y)
              VALUES (@playerId, 'STACK', @templateId, NULL, @x, @y)
              ON CONFLICT (player_id, template_id) WHERE kind = 'STACK'
              DO UPDATE SET x = EXCLUDED.x, y = EXCLUDED.y",
            new { playerId, templateId, x, y });
    }

    /// <summary>유니크 배치 upsert: 인스턴스 단위로 유일. 위치만 갱신.</summary>
    public async Task UpsertInstancePlacementAsync(Guid playerId, int templateId, Guid instanceId, int x, int y)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            @"INSERT INTO stash_placement(player_id, kind, template_id, instance_id, x, y)
              VALUES (@playerId, 'INSTANCE', @templateId, @instanceId, @x, @y)
              ON CONFLICT (instance_id)
              DO UPDATE SET x = EXCLUDED.x, y = EXCLUDED.y, player_id = EXCLUDED.player_id",
            new { playerId, templateId, instanceId, x, y });
    }

    /// <summary>더 이상 소유하지 않는 스택형 배치 정리.</summary>
    public async Task DeleteStackPlacementAsync(Guid playerId, int templateId)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            "DELETE FROM stash_placement WHERE player_id = @playerId AND kind = 'STACK' AND template_id = @templateId",
            new { playerId, templateId });
    }

    /// <summary>더 이상 소유하지 않는 유니크 배치 정리.</summary>
    public async Task DeleteInstancePlacementAsync(Guid playerId, Guid instanceId)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            "DELETE FROM stash_placement WHERE player_id = @playerId AND kind = 'INSTANCE' AND instance_id = @instanceId",
            new { playerId, instanceId });
    }

    // ======================================================================
    //  주문
    // ======================================================================
    public async Task InsertOrderAsync(OrderRow o)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            @"INSERT INTO market_order
                (id, player_id, side, template_id, unit_price, quantity, remaining_quantity,
                 instance_id, status, escrow_caps, created_at, updated_at)
              VALUES
                (@Id, @PlayerId, @Side, @TemplateId, @UnitPrice, @Quantity, @RemainingQuantity,
                 @InstanceId, @Status, @EscrowCaps, @CreatedAt, @CreatedAt)",
            new
            {
                o.Id,
                o.PlayerId,
                Side = o.Side.ToDb(),
                o.TemplateId,
                o.UnitPrice,
                o.Quantity,
                o.RemainingQuantity,
                o.InstanceId,
                Status = o.Status.ToDb(),
                o.EscrowCaps,
                o.CreatedAt
            });
    }

    public async Task<OrderRow?> GetOrderAsync(Guid id)
    {
        await using var db = Open();
        var r = await db.QuerySingleOrDefaultAsync(
            @"SELECT id, player_id, side, template_id, unit_price, quantity, remaining_quantity,
                     instance_id, status, escrow_caps, created_at
              FROM market_order WHERE id = @id", new { id });
        return r is null ? null : MapOrderRow(r);
    }

    public async Task<IReadOnlyList<OrderDto>> GetOrdersByPlayerAsync(Guid playerId)
    {
        await using var db = Open();
        var rows = await db.QueryAsync(
            @"SELECT id, player_id, side, template_id, unit_price, quantity, remaining_quantity,
                     instance_id, status, escrow_caps, created_at
              FROM market_order WHERE player_id = @playerId ORDER BY created_at DESC",
            new { playerId });
        return rows.Select(r => ((OrderRow)MapOrderRow(r)).ToDto()).ToList();
    }

    /// <summary>매칭 엔진 재수화용: 특정 템플릿의 미체결(OPEN/PARTIALLY_FILLED) 주문.</summary>
    public async Task<IReadOnlyList<OrderRow>> GetLiveOrdersAsync(int templateId)
    {
        await using var db = Open();
        var rows = await db.QueryAsync(
            @"SELECT id, player_id, side, template_id, unit_price, quantity, remaining_quantity,
                     instance_id, status, escrow_caps, created_at
              FROM market_order
              WHERE template_id = @templateId AND status IN ('OPEN','PARTIALLY_FILLED')
              ORDER BY created_at",
            new { templateId });
        return rows.Select(r => (OrderRow)MapOrderRow(r)).ToList();
    }

    /// <summary>
    /// 밴드 매칭 엔진 재수화용: 특정 템플릿에서 <b>가격 밴드에 속한</b> 미체결 주문.
    /// 밴드 = unit_price / bandSize (정수 나눗셈; 단가는 항상 양수라 절삭이 안전).
    /// </summary>
    public async Task<IReadOnlyList<OrderRow>> GetLiveOrdersInBandAsync(int templateId, int bandSize, long band)
    {
        await using var db = Open();
        var rows = await db.QueryAsync(
            @"SELECT id, player_id, side, template_id, unit_price, quantity, remaining_quantity,
                     instance_id, status, escrow_caps, created_at
              FROM market_order
              WHERE template_id = @templateId AND status IN ('OPEN','PARTIALLY_FILLED')
                    AND (unit_price / @bandSize) = @band
              ORDER BY created_at",
            new { templateId, bandSize, band });
        return rows.Select(r => (OrderRow)MapOrderRow(r)).ToList();
    }

    /// <summary>
    /// 코디네이터 스냅샷 팬아웃용: 특정 템플릿에서 미체결 주문이 존재하는 <b>밴드 목록</b>(중복 제거).
    /// 밴드 = unit_price / bandSize.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetLiveBandsAsync(int templateId, int bandSize)
    {
        await using var db = Open();
        var rows = await db.QueryAsync<long>(
            @"SELECT DISTINCT (unit_price / @bandSize) AS band
              FROM market_order
              WHERE template_id = @templateId AND status IN ('OPEN','PARTIALLY_FILLED')",
            new { templateId, bandSize });
        return rows.ToList();
    }

    public async Task<PagedResult<OrderDto>> GetOrdersAdminAsync(int? templateId, OrderStatus? status, int page, int size)
    {
        await using var db = Open();
        var where = "WHERE 1=1";
        if (templateId is not null) where += " AND template_id = @templateId";
        if (status is not null) where += " AND status = @status";
        var args = new { templateId, status = status?.ToDb(), size, offset = (page - 1) * size };
        var total = await db.ExecuteScalarAsync<long>($"SELECT count(*) FROM market_order {where}", args);
        var rows = await db.QueryAsync(
            $@"SELECT id, player_id, side, template_id, unit_price, quantity, remaining_quantity,
                      instance_id, status, escrow_caps, created_at
               FROM market_order {where}
               ORDER BY created_at DESC LIMIT @size OFFSET @offset", args);
        var items = rows.Select(r => ((OrderRow)MapOrderRow(r)).ToDto()).ToList();
        return new PagedResult<OrderDto>(items, page, size, total);
    }

    private static OrderRow MapOrderRow(dynamic r) => new(
        (Guid)r.id, (Guid)r.player_id, Enums.ToSide((string)r.side), (int)r.template_id,
        (long)r.unit_price, (int)r.quantity, (int)r.remaining_quantity, (Guid?)r.instance_id,
        Enums.ToStatus((string)r.status), (long)r.escrow_caps, (DateTimeOffset)r.created_at);

    // ======================================================================
    //  취소(에스크로 환불) — 한 트랜잭션
    // ======================================================================
    public async Task<OrderDto> CancelOrderAsync(Guid orderId)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();

        // 데드락 방지: 전역 락 순서 = wallet → market_order (SettleFillAsync와 동일).
        //   과거엔 market_order를 먼저 FOR UPDATE로 잠근 뒤 wallet을 갱신해
        //   순서가 정산과 반대(order→wallet)여서 동시 취소+정산이 교착(40P01)할 수 있었다.
        //   player_id는 주문 생성 후 불변이므로, 잠그지 않은 SELECT로 먼저 읽어
        //   그 지갑 행을 FOR UPDATE로 잠근 다음, 비로소 주문 행을 잠근다.
        var ownerPid = await db.ExecuteScalarAsync<Guid?>(
            "SELECT player_id FROM market_order WHERE id = @orderId", new { orderId }, tx);
        if (ownerPid is null) { await tx.RollbackAsync(); throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다."); }

        // 지갑 행을 먼저 잠근다(매수 취소의 환불 대상; 매도 취소여도 순서 일관성을 위해 잠금).
        await db.ExecuteScalarAsync<long?>(
            "SELECT balance FROM wallet WHERE player_id = @pid FOR UPDATE", new { pid = ownerPid.Value }, tx);

        var r = await db.QuerySingleOrDefaultAsync(
            @"SELECT id, player_id, side, template_id, unit_price, quantity, remaining_quantity,
                     instance_id, status, escrow_caps, created_at
              FROM market_order WHERE id = @orderId FOR UPDATE", new { orderId }, tx);
        if (r is null) { await tx.RollbackAsync(); throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다."); }
        OrderRow o = MapOrderRow(r);
        if (o.Status is OrderStatus.Filled or OrderStatus.Cancelled)
        {
            await tx.RollbackAsync();
            throw new DomainException(ErrorCode.OrderAlreadyClosed, "이미 종료된 주문입니다.");
        }

        if (o.Side == OrderSide.Buy)
        {
            // 남은 잠금 병뚜껑 환불
            if (o.EscrowCaps > 0)
            {
                var after = await db.ExecuteScalarAsync<long>(
                    "UPDATE wallet SET balance = balance + @amt WHERE player_id = @pid RETURNING balance",
                    new { amt = o.EscrowCaps, pid = o.PlayerId }, tx);
                await InsertLedgerAsync(db, tx, o.PlayerId, o.EscrowCaps, after, WalletLedgerReason.OrderRefund, o.Id);
            }
        }
        else if (o.InstanceId is not null)
        {
            // 유니크: 인스턴스 소유권 원복
            await db.ExecuteAsync(
                "UPDATE item_instance SET owner_player_id = @pid WHERE id = @iid",
                new { pid = o.PlayerId, iid = o.InstanceId }, tx);
        }
        else if (o.RemainingQuantity > 0)
        {
            // 스택형: 남은 수량 인벤 반환
            await UpsertStackAsync(db, tx, o.PlayerId, o.TemplateId, o.RemainingQuantity);
        }

        await db.ExecuteAsync(
            "UPDATE market_order SET status = 'CANCELLED', escrow_caps = 0, updated_at = now() WHERE id = @orderId",
            new { orderId }, tx);
        await tx.CommitAsync();

        return (o with { Status = OrderStatus.Cancelled, EscrowCaps = 0 }).ToDto();
    }

    // ======================================================================
    //  리프레시 토큰 — 저장/조회/로테이션/폐기 (원문 대신 SHA-256 해시 저장)
    // ======================================================================

    /// <summary>토큰 원문의 SHA-256 해시(hex). DB에는 이 값만 저장/조회한다.</summary>
    private static string HashToken(string rawToken)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>새 리프레시 토큰(원문)을 해시해서 저장한다.</summary>
    public async Task StoreRefreshTokenAsync(Guid id, Guid playerId, string rawToken, DateTime expiresAt)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            @"INSERT INTO refresh_token(id, player_id, token_hash, expires_at)
              VALUES (@id, @playerId, @hash, @expiresAt)",
            new { id, playerId, hash = HashToken(rawToken), expiresAt });
    }

    /// <summary>원문 토큰으로 행을 조회한다(해시 매칭). 없으면 null.</summary>
    public async Task<RefreshTokenRow?> GetRefreshTokenAsync(string rawToken)
    {
        await using var db = Open();
        var row = await db.QuerySingleOrDefaultAsync(
            @"SELECT id, player_id, expires_at, revoked
              FROM refresh_token WHERE token_hash = @hash",
            new { hash = HashToken(rawToken) });
        return row is null
            ? null
            : new RefreshTokenRow((Guid)row.id, (Guid)row.player_id, (DateTime)row.expires_at, (bool)row.revoked);
    }

    /// <summary>
    /// 로테이션의 원자적 폐기: 아직 유효(revoked=false)한 행만 폐기한다.
    /// 폐기에 성공하면 true, 이미 폐기됐거나(동시 회전/재사용) 없으면 false.
    /// </summary>
    public async Task<bool> TryRevokeRefreshTokenAsync(Guid id)
    {
        await using var db = Open();
        var rows = await db.ExecuteAsync(
            "UPDATE refresh_token SET revoked = true WHERE id = @id AND revoked = false",
            new { id });
        return rows == 1;
    }

    /// <summary>원문 토큰으로 폐기(로그아웃). 없거나 이미 폐기여도 무해(멱등).</summary>
    public async Task RevokeRefreshTokenAsync(string rawToken)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            "UPDATE refresh_token SET revoked = true WHERE token_hash = @hash",
            new { hash = HashToken(rawToken) });
    }

    /// <summary>플레이어의 모든 리프레시 토큰을 폐기한다(재사용 탐지 시 체인 무효화).</summary>
    public async Task RevokeAllRefreshTokensAsync(Guid playerId)
    {
        await using var db = Open();
        await db.ExecuteAsync(
            "UPDATE refresh_token SET revoked = true WHERE player_id = @playerId AND revoked = false",
            new { playerId });
    }

    // ======================================================================
    //  정산(SETTLEMENT) — 체결 1건을 한 트랜잭션으로 원자 처리
    //  (판매대금 지급 + 수수료 소각 + 매수 차익 환불 + 아이템 이전 +
    //   trade 기록 + 양쪽 주문 갱신). 어떤 예외에도 전부 롤백.
    // ======================================================================
    public async Task SettleFillAsync(SettleFillArgs a)
    {
        var gross = a.ExecPrice * a.Quantity;                 // 판매 총액(주문 검증으로 상한 보장)
        var fee = CalcFee(a.ExecPrice, a.Quantity, a.FeeBps); // 수수료(소각) = 총액 × bps/10000, Int128 안전
        var improvement = (a.BuyLimitPrice - a.ExecPrice) * a.Quantity; // 매수 상한가 대비 차익
        var releasedEscrow = a.BuyLimitPrice * a.Quantity;    // 이 체결분에 잠겼던 매수 에스크로

        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            // 0) 데드락 방지: 이 트랜잭션이 건드릴 지갑 행을 player_id 오름차순으로 미리 잠근다.
            //    서로 다른 OrderBookGrain의 동시 정산이 매수자/판매자 지갑을 '서로 다른 순서'로
            //    잠가 생기던 Postgres 교착(40P01)을 일관된 락 순서로 제거한다.
            await db.ExecuteAsync(
                "SELECT player_id FROM wallet WHERE player_id = ANY(@ids) ORDER BY player_id FOR UPDATE",
                new { ids = new[] { a.BuyerId, a.SellerId } }, tx);

            // 1) 체결 기록
            await db.ExecuteAsync(
                @"INSERT INTO trade
                    (id, template_id, buy_order_id, sell_order_id, buyer_id, seller_id,
                     unit_price, quantity, instance_id, fee_amount, executed_at)
                  VALUES
                    (@TradeId, @TemplateId, @BuyOrderId, @SellOrderId, @BuyerId, @SellerId,
                     @ExecPrice, @Quantity, @InstanceId, @fee, @ExecutedAt)",
                new
                {
                    a.TradeId,
                    a.TemplateId,
                    a.BuyOrderId,
                    a.SellOrderId,
                    a.BuyerId,
                    a.SellerId,
                    a.ExecPrice,
                    a.Quantity,
                    a.InstanceId,
                    fee,
                    a.ExecutedAt
                }, tx);

            // 2) 판매자: 총액 수령(+) 후 수수료 소각(-). 순수령 = gross - fee.
            var sellerAfterGross = await db.ExecuteScalarAsync<long>(
                "UPDATE wallet SET balance = balance + @gross WHERE player_id = @pid RETURNING balance",
                new { gross, pid = a.SellerId }, tx);
            await InsertLedgerAsync(db, tx, a.SellerId, gross, sellerAfterGross, WalletLedgerReason.TradeProceeds, a.TradeId);
            if (fee > 0)
            {
                var sellerAfterFee = await db.ExecuteScalarAsync<long>(
                    "UPDATE wallet SET balance = balance - @fee WHERE player_id = @pid RETURNING balance",
                    new { fee, pid = a.SellerId }, tx);
                await InsertLedgerAsync(db, tx, a.SellerId, -fee, sellerAfterFee, WalletLedgerReason.Fee, a.TradeId);
            }

            // 3) 매수자: 대금은 이미 에스크로에서 빠졌으므로, 상한가 대비 차익만 환불(+).
            if (improvement > 0)
            {
                var buyerAfter = await db.ExecuteScalarAsync<long>(
                    "UPDATE wallet SET balance = balance + @imp WHERE player_id = @pid RETURNING balance",
                    new { imp = improvement, pid = a.BuyerId }, tx);
                await InsertLedgerAsync(db, tx, a.BuyerId, improvement, buyerAfter, WalletLedgerReason.OrderRefund, a.TradeId);
            }

            // 4) 아이템 이전: 스택형은 수량 가산, 유니크는 소유권 이전.
            if (a.Stackable)
            {
                await UpsertStackAsync(db, tx, a.BuyerId, a.TemplateId, a.Quantity);
            }
            else if (a.InstanceId is not null)
            {
                await db.ExecuteAsync(
                    "UPDATE item_instance SET owner_player_id = @pid WHERE id = @iid",
                    new { pid = a.BuyerId, iid = a.InstanceId }, tx);
            }

            // 5) 매수 주문 갱신: 잔량/상태 + 잠금 병뚜껑 차감.
            //    낙관적 동시성 가드: 갱신 직전의 잔량(= 체결 후 잔량 + 체결 수량)이
            //    DB와 일치할 때만 갱신. 드문 이중 활성화(double-activation)로 다른
            //    활성화가 먼저 이 체결을 반영했다면 rows=0 → 롤백하여 DB가 최종 심판이 된다.
            var buyBefore = a.BuyRemaining + a.Quantity;
            var buyRows = await db.ExecuteAsync(
                @"UPDATE market_order
                  SET remaining_quantity = @rem, status = @st,
                      escrow_caps = escrow_caps - @rel, updated_at = now()
                  WHERE id = @id AND remaining_quantity = @before
                    AND status IN ('OPEN','PARTIALLY_FILLED')",
                new { rem = a.BuyRemaining, st = a.BuyStatus.ToDb(), rel = releasedEscrow, id = a.BuyOrderId, before = buyBefore }, tx);
            if (buyRows != 1)
                throw new DomainException(ErrorCode.OrderAlreadyClosed, "동시성 충돌: 매수 주문 잔량이 예상과 다릅니다.");

            // 6) 매도 주문 갱신: 잔량/상태(같은 낙관적 가드).
            var sellBefore = a.SellRemaining + a.Quantity;
            var sellRows = await db.ExecuteAsync(
                @"UPDATE market_order SET remaining_quantity = @rem, status = @st, updated_at = now()
                  WHERE id = @id AND remaining_quantity = @before
                    AND status IN ('OPEN','PARTIALLY_FILLED')",
                new { rem = a.SellRemaining, st = a.SellStatus.ToDb(), id = a.SellOrderId, before = sellBefore }, tx);
            if (sellRows != 1)
                throw new DomainException(ErrorCode.OrderAlreadyClosed, "동시성 충돌: 매도 주문 잔량이 예상과 다릅니다.");

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ======================================================================
    //  체결 내역
    // ======================================================================
    public async Task<PagedResult<TradeDto>> GetTradesByTemplateAsync(int templateId, int page, int size)
    {
        await using var db = Open();
        var total = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM trade WHERE template_id = @templateId", new { templateId });
        var rows = await db.QueryAsync(
            @"SELECT id, template_id, unit_price, quantity, buyer_id, seller_id, buy_order_id,
                     sell_order_id, instance_id, fee_amount, executed_at
              FROM trade WHERE template_id = @templateId
              ORDER BY executed_at DESC LIMIT @size OFFSET @offset",
            new { templateId, size, offset = (page - 1) * size });
        return new PagedResult<TradeDto>(rows.Select(MapTrade).ToList(), page, size, total);
    }

    public async Task<PagedResult<TradeDto>> GetTradesAllAsync(int page, int size)
    {
        await using var db = Open();
        var total = await db.ExecuteScalarAsync<long>("SELECT count(*) FROM trade");
        var rows = await db.QueryAsync(
            @"SELECT id, template_id, unit_price, quantity, buyer_id, seller_id, buy_order_id,
                     sell_order_id, instance_id, fee_amount, executed_at
              FROM trade ORDER BY executed_at DESC LIMIT @size OFFSET @offset",
            new { size, offset = (page - 1) * size });
        return new PagedResult<TradeDto>(rows.Select(MapTrade).ToList(), page, size, total);
    }

    private static TradeDto MapTrade(dynamic r) => new(
        (Guid)r.id, (int)r.template_id, (long)r.unit_price, (int)r.quantity,
        (Guid)r.buyer_id, (Guid)r.seller_id, (Guid)r.buy_order_id, (Guid)r.sell_order_id,
        (Guid?)r.instance_id, (long)r.fee_amount, (DateTimeOffset)r.executed_at);
}

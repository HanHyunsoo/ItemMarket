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

/// <summary>
/// Postgres 데이터 접근(Dapper). 얇은 리포지토리 + "정산" 트랜잭션 한 개.
/// Postgres가 소스오브트루스이며 grain은 여기를 통해 상태를 읽고 쓴다.
/// </summary>
public sealed partial class MarketRepository(
    string connectionString,
    int raidDurationSeconds = 180,
    long stashUpgradeBasePrice = 2000,
    long stashUpgradeStep = 1000,
    long raidEntryFeeLow = 150,
    long raidEntryFeeMed = 400,
    long raidEntryFeeHigh = 600)
{
    /// <summary>스태시 확장 단위(행)와 상한(DDL CHECK와 일치).</summary>
    private const int StashRowsPerUpgrade = 6;
    private const int StashRowsMax = 500;
    private const int StashRowsStart = 60;

    /// <summary>다음 +6행 확장 가격(점증). 확장할수록 비싸져 무한 저가 확장을 막는 캡 싱크.</summary>
    private long StashUpgradeCost(int currentRows)
        => stashUpgradeBasePrice + Math.Max(0, (currentRows - StashRowsStart) / StashRowsPerUpgrade) * stashUpgradeStep;
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
        // fee_bps는 [0,10000](=0~100%)로 클램프한다. 설정 경로가 없어 이 읽기 지점이 유일 방어 —
        // 음수는 음수 수수료(돈 발행), 10000 초과는 체결액을 넘는 수수료가 되므로 원천 차단(L7).
        return Math.Clamp(int.TryParse(v, out var bps) ? bps : 500, 0, 10000);
    }

    public async Task<PlayerRow?> GetPlayerAsync(Guid id)
    {
        await using var db = Open();
        var row = await db.QuerySingleOrDefaultAsync(
            "SELECT id, display_name, stash_rows FROM player WHERE id = @id", new { id });
        return row is null ? null : new PlayerRow((Guid)row.id, (string)row.display_name, (int)row.stash_rows);
    }

    public async Task<IReadOnlyList<ItemTemplateDto>> GetCatalogAsync()
    {
        await using var db = Open();
        var rows = await db.QueryAsync(
            @"SELECT id, code, name, category, rarity, stackable, max_durability, icon, base_value, grid_w, grid_h,
                     equip_slot, is_container, container_w, container_h, max_stack
              FROM item_template ORDER BY id");
        return rows.Select(r => new ItemTemplateDto(
            (int)r.id, (string)r.code, (string)r.name,
            Enums.ToCategory((string)r.category), Enums.ToRarity((string)r.rarity),
            (bool)r.stackable, (int?)r.max_durability, (string)r.icon, (long)r.base_value,
            (int)r.grid_w, (int)r.grid_h,
            Enums.ToEquipSlotOrNull((string?)r.equip_slot), (bool)r.is_container,
            (int?)r.container_w, (int?)r.container_h, (int)r.max_stack)).ToList();
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
        await CreditWalletAsync(db, tx, playerId, amount, WalletLedgerReason.OrderRefund, refId);
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

    /// <summary>
    /// 지갑 잔액을 delta만큼 조정(+입금/−출금)하고 같은 트랜잭션에 원장을 기록한 뒤 갱신 후 잔액을 반환한다.
    /// "UPDATE ... RETURNING balance + InsertLedger" 쌍의 단일 지점 — 정산/환불/벤더 등 잔액 불변식이
    /// 이미 보장된 경로에서 쓴다. (음수 잔액 가드가 필요한 AdminAdjust·에스크로 선점은 자체 검증을 유지.)
    /// </summary>
    private static async Task<long> CreditWalletAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid playerId, long delta, WalletLedgerReason reason, Guid? refId)
    {
        var after = await db.ExecuteScalarAsync<long>(
            "UPDATE wallet SET balance = balance + @delta WHERE player_id = @playerId RETURNING balance",
            new { delta, playerId }, tx);
        await InsertLedgerAsync(db, tx, playerId, delta, after, reason, refId);
        return after;
    }

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
        // 지급을 한 트랜잭션으로: inventory_stack 가산 + item_ledger(ADMIN_GRANT) 기록(A-2 —
        // 지갑 AdminAdjust와 대칭. 광고된 ADMIN_GRANT 사유를 실제로 방출해 아이템 원장을 완결화).
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            await UpsertStackAsync(db, tx, playerId, templateId, qty);
            await InsertItemLedgerAsync(db, tx, playerId, StashEntryKind.Stack, templateId, null, qty, ItemLedgerReason.AdminGrant, null);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
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
        // 지급을 한 트랜잭션으로: item_instance 생성 + item_ledger(ADMIN_GRANT) 기록(A-2 — 지갑 AdminAdjust와 대칭).
        await using var tx = await db.BeginTransactionAsync();
        dynamic row;
        try
        {
            row = await db.QuerySingleAsync(
                @"INSERT INTO item_instance(id, template_id, owner_player_id, durability, attachments, origin)
                  VALUES (@id, @templateId, @playerId, @durability, @attJson::jsonb, 'ADMIN_GRANT')
                  RETURNING id, template_id, durability, attachments, created_at",
                new { id, templateId, playerId, durability, attJson }, tx);
            await InsertItemLedgerAsync(db, tx, playerId, StashEntryKind.Instance, templateId, id, 1, ItemLedgerReason.AdminGrant, null);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
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

    /// <summary>주문이 영속화됐는지(커밋됐는지) 확인. 에스크로 보상 전에 "정말 INSERT가 안 됐는지"를
    /// 재조정해 커밋-후-예외 창에서의 이중환불을 막는 데 쓴다(L9a).</summary>
    public async Task<bool> OrderExistsAsync(Guid id)
    {
        await using var db = Open();
        return await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM market_order WHERE id = @id)", new { id });
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

    // 순자산 = 지갑 잔액 + 보유 스택 가치(Σ 수량×기준가) + 소유 유니크 가치(Σ 기준가).
    // at-risk(레이드 중 owner=NULL) 아이템은 자연히 제외돼 출격이 순자산 하락으로 반영된다.
    // Top-N 쿼리와 "내 순위" 랭크 쿼리가 동일 식을 쓰도록 한 곳에 둔다(정렬 기준 drift 방지).
    private const string NetWorthExpr =
        @"w.balance
          + COALESCE((SELECT SUM(s.quantity * t.base_value)
                      FROM inventory_stack s JOIN item_template t ON t.id = s.template_id
                      WHERE s.player_id = p.id), 0)
          + COALESCE((SELECT SUM(t.base_value)
                      FROM item_instance i JOIN item_template t ON t.id = i.template_id
                      WHERE i.owner_player_id = p.id), 0)";

    /// <summary>
    /// 리더보드: 최다 순자산(지갑+보유 아이템 가치)과 최다 생환(EXTRACTED 세션 수) 상위 N명 +
    /// 호출자 본인의 순위(Top-N 밖이어도). 경제에 판돈이 생긴 뒤의 사회적 목표(#8). 이름 동률은 표시명으로 안정 정렬.
    /// 랭크 = 나보다 앞선 사람 수 + 1 (값 내림차순, 동률은 표시명 오름차순 — Top-N 정렬과 동일 순서).
    /// </summary>
    public async Task<LeaderboardDto> GetLeaderboardAsync(Guid playerId, int limit = 10)
    {
        await using var db = Open();

        // base_value는 참고 시세라 "대략적" 순자산이다(에스크로 잠긴 캡은 balance에서 이미 빠져 미포함 — MVP).
        var netWorth = (await db.QueryAsync(
            $@"SELECT p.id, p.display_name, {NetWorthExpr} AS value
              FROM player p JOIN wallet w ON w.player_id = p.id
              ORDER BY value DESC, p.display_name
              LIMIT @limit", new { limit }))
            .Select(r => new LeaderEntryDto((Guid)r.id, (string)r.display_name, (long)r.value)).ToList();

        var extractions = (await db.QueryAsync(
            @"SELECT p.id, p.display_name, count(rs.id) AS value
              FROM player p JOIN raid_session rs ON rs.player_id = p.id AND rs.status = 'EXTRACTED'
              GROUP BY p.id, p.display_name
              ORDER BY value DESC, p.display_name
              LIMIT @limit", new { limit }))
            .Select(r => new LeaderEntryDto((Guid)r.id, (string)r.display_name, (long)r.value)).ToList();

        var me = await LoadLeaderMeAsync(db, playerId);
        return new LeaderboardDto(netWorth, extractions, me);
    }

    /// <summary>호출자 본인의 순자산·생환 순위(전체 대비). Top-N 밖 플레이어의 위치 피드백용.</summary>
    private async Task<LeaderMeDto> LoadLeaderMeAsync(NpgsqlConnection db, Guid playerId)
    {
        var totalPlayers = await db.ExecuteScalarAsync<long>("SELECT count(*) FROM player");

        // 내 순자산 + 표시명(동률 tie-break에 필요).
        var mine = await db.QuerySingleAsync(
            $@"SELECT p.display_name, {NetWorthExpr} AS value
               FROM player p JOIN wallet w ON w.player_id = p.id
               WHERE p.id = @playerId", new { playerId });
        long myNetWorth = (long)mine.value;
        string myName = (string)mine.display_name;

        // 순자산 순위 = 나보다 앞선(값 큼, 또는 값 동률+표시명 앞선) 사람 수 + 1.
        var netWorthRank = await db.ExecuteScalarAsync<long>(
            $@"SELECT 1 + count(*) FROM (
                 SELECT p.display_name, {NetWorthExpr} AS value
                 FROM player p JOIN wallet w ON w.player_id = p.id
               ) nw
               WHERE nw.value > @myNetWorth
                  OR (nw.value = @myNetWorth AND nw.display_name < @myName)",
            new { myNetWorth, myName });

        // 내 생환 횟수.
        long myExtractions = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM raid_session WHERE player_id = @playerId AND status = 'EXTRACTED'",
            new { playerId });

        // 생환 순위: 생환 기록이 있는 사람들 사이에서만. 0회면 순위 미정(null).
        long? extractionsRank = null;
        if (myExtractions > 0)
        {
            extractionsRank = await db.ExecuteScalarAsync<long>(
                @"SELECT 1 + count(*) FROM (
                     SELECT p.display_name, count(rs.id) AS value
                     FROM player p JOIN raid_session rs ON rs.player_id = p.id AND rs.status = 'EXTRACTED'
                     GROUP BY p.id, p.display_name
                   ) ex
                   WHERE ex.value > @myExtractions
                      OR (ex.value = @myExtractions AND ex.display_name < @myName)",
                new { myExtractions, myName });
        }

        return new LeaderMeDto(playerId, myNetWorth, netWorthRank, myExtractions, extractionsRank, totalPlayers);
    }

    /// <summary>
    /// 전 종목 시세 요약(마켓 카드 목록용). 종목별 최우선 매수(MAX BUY)·매도(MIN SELL) 호가,
    /// 최근 체결가/시각, 활성 주문 수를 한 번의 집계로 반환한다(카드마다 book/trades를 따로 치는
    /// N+1을 피한다). 활동 없는 종목은 null/0으로 "시장 없음"을 나타낸다.
    /// </summary>
    public async Task<IReadOnlyList<MarketTickerDto>> GetTickersAsync()
    {
        await using var db = Open();
        // base_value ± 스프레드로 벤더 참고가(vendor_bid/ask)를 함께 낸다 — 실제 주문/매칭/경제와 무관한
        // 순수 참고가로, 플레이어 호가가 없는 종목의 "죽은 첫 화면"을 카드에서 시각적으로 해소한다.
        var rows = await db.QueryAsync(
            $@"SELECT t.id AS template_id,
                (SELECT MAX(unit_price) FROM market_order o
                   WHERE o.template_id = t.id AND o.side = 'BUY'
                     AND o.status IN ('OPEN','PARTIALLY_FILLED')) AS best_bid,
                (SELECT MIN(unit_price) FROM market_order o
                   WHERE o.template_id = t.id AND o.side = 'SELL'
                     AND o.status IN ('OPEN','PARTIALLY_FILLED')) AS best_ask,
                (SELECT unit_price FROM trade tr
                   WHERE tr.template_id = t.id ORDER BY tr.executed_at DESC LIMIT 1) AS last_price,
                (SELECT MAX(executed_at) FROM trade tr WHERE tr.template_id = t.id) AS last_trade_at,
                (SELECT count(*) FROM market_order o
                   WHERE o.template_id = t.id AND o.status IN ('OPEN','PARTIALLY_FILLED')) AS open_orders,
                GREATEST(1, floor(t.base_value * (10000 - {VendorSpreadBps}) / 10000.0)::bigint) AS vendor_bid,
                GREATEST(1, ceil (t.base_value * (10000 + {VendorSpreadBps}) / 10000.0)::bigint) AS vendor_ask
              FROM item_template t
              ORDER BY t.id");
        return rows.Select(r => new MarketTickerDto(
            (int)r.template_id, (long?)r.best_bid, (long?)r.best_ask, (long?)r.last_price,
            (DateTimeOffset?)r.last_trade_at, (int)r.open_orders,
            (long)r.vendor_bid, (long)r.vendor_ask)).ToList();
    }

    /// <summary>벤더 참고가 스프레드(bps). 매수=base×(1-s), 매도=base×(1+s). 참고 표시용(실거래 아님).</summary>
    private const int VendorSpreadBps = 1500;

    /// <summary>NPC 벤더 매입가(캡 faucet) = base_value × (1 - 스프레드), 최소 1. 플레이어 시장가보다 낮은
    /// 최후 유동성 창구다. tickers의 vendor_bid와 동일 공식.</summary>
    private static long VendorBid(long baseValue) =>
        Math.Max(1, (long)Math.Floor(baseValue * (10000.0 - VendorSpreadBps) / 10000.0));

    /// <summary>
    /// NPC 벤더 매입(한 트랜잭션): 보유 아이템을 벤더가(VendorBid)로 즉시 판매해 캡을 발행한다(faucet).
    /// 스택은 inventory_stack 차감, 유니크는 owner=NULL·origin=VENDOR_SOLD로 소각(FK 안전 tombstone).
    /// 지갑에 대금을 넣고 wallet_ledger(VENDOR_SELL,+)·item_ledger(VendorSell,-)로 회계한다.
    /// </summary>
    public async Task<VendorSellResultDto> VendorSellAsync(Guid playerId, VendorSellRequest req)
    {
        await using var db = Open();
        var isStack = req.Kind == StashEntryKind.Stack;
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            long unitPrice, proceeds;
            int templateId;

            if (isStack)
            {
                if (req.TemplateId is not { } tid)
                    throw new DomainException(ErrorCode.ValidationError, "스택 판매에는 TemplateId가 필요합니다.");
                var qty = req.Quantity ?? 0;
                if (qty < 1)
                    throw new DomainException(ErrorCode.ValidationError, "판매 수량은 1 이상이어야 합니다.");
                templateId = tid;
                var have = await db.ExecuteScalarAsync<int?>(
                    "SELECT quantity FROM inventory_stack WHERE player_id = @playerId AND template_id = @tid FOR UPDATE",
                    new { playerId, tid }, tx);
                if (have is null || have < qty)
                    throw new DomainException(ErrorCode.InsufficientQuantity, "판매 수량이 보유량을 초과합니다.");
                var baseValue = await db.ExecuteScalarAsync<long>(
                    "SELECT base_value FROM item_template WHERE id = @tid", new { tid }, tx);
                unitPrice = VendorBid(baseValue);
                proceeds = unitPrice * qty;
                await db.ExecuteAsync(
                    "UPDATE inventory_stack SET quantity = quantity - @qty WHERE player_id = @playerId AND template_id = @tid",
                    new { qty, playerId, tid }, tx);
                await InsertItemLedgerAsync(db, tx, playerId, StashEntryKind.Stack, templateId, null, -qty, ItemLedgerReason.VendorSell, null);
            }
            else
            {
                if (req.InstanceId is not { } iid)
                    throw new DomainException(ErrorCode.ValidationError, "유니크 판매에는 InstanceId가 필요합니다.");
                var inst = await db.QuerySingleOrDefaultAsync(
                    "SELECT owner_player_id, template_id FROM item_instance WHERE id = @iid FOR UPDATE",
                    new { iid }, tx);
                if (inst is null)
                    throw new DomainException(ErrorCode.InstanceNotFound, "인스턴스를 찾을 수 없습니다.");
                if ((Guid?)inst.owner_player_id != playerId)
                    throw new DomainException(ErrorCode.InstanceNotOwned, "소유하지 않은 인스턴스입니다.");
                // 장착 중(player_equipment)이면 거부 — 소각해도 장착 행은 별도 테이블이라 남아
                // "캡 수령 + 장비 유지(가치 복제)" 후 출격 시 owner=NULL 롤백(소프트락)을 유발한다.
                // 인형 위 아이템은 먼저 해제 후 창고에서 팔아야 한다.
                var equipped = await db.ExecuteScalarAsync<int?>(
                    "SELECT 1 FROM player_equipment WHERE player_id = @playerId AND instance_id = @iid",
                    new { playerId, iid }, tx);
                if (equipped is not null)
                    throw new DomainException(ErrorCode.ValidationError, "장착 중인 아이템은 판매할 수 없습니다. 먼저 해제하세요.");
                // 내용물이 있는 컨테이너(백팩/리그)를 팔면 중첩 배치가 유령 컨테이너를 가리켜 orphan이 된다.
                // 먼저 비운 뒤 판매하도록 거부한다.
                var hasContents = await db.ExecuteScalarAsync<int?>(
                    "SELECT 1 FROM stash_placement WHERE player_id = @playerId AND container_instance_id = @iid LIMIT 1",
                    new { playerId, iid }, tx);
                if (hasContents is not null)
                    throw new DomainException(ErrorCode.ValidationError, "내용물이 있는 컨테이너는 판매할 수 없습니다. 먼저 비우세요.");
                templateId = (int)inst.template_id;
                var baseValue = await db.ExecuteScalarAsync<long>(
                    "SELECT base_value FROM item_template WHERE id = @templateId", new { templateId }, tx);
                unitPrice = VendorBid(baseValue);
                proceeds = unitPrice;
                // 소각: owner=NULL + origin=VENDOR_SOLD(FK 안전 tombstone) + 배치 제거.
                await db.ExecuteAsync(
                    "UPDATE item_instance SET owner_player_id = NULL, origin = 'VENDOR_SOLD' WHERE id = @iid",
                    new { iid }, tx);
                await db.ExecuteAsync(
                    "DELETE FROM stash_placement WHERE player_id = @playerId AND kind = 'INSTANCE' AND instance_id = @iid",
                    new { playerId, iid }, tx);
                await InsertItemLedgerAsync(db, tx, playerId, StashEntryKind.Instance, templateId, iid, -1, ItemLedgerReason.VendorSell, null);
            }

            // 캡 발행(faucet): 지갑에 대금 지급 + wallet_ledger(VENDOR_SELL,+).
            var after = await CreditWalletAsync(db, tx, playerId, proceeds, WalletLedgerReason.VendorSell, null);

            await tx.CommitAsync();
            return new VendorSellResultDto(unitPrice, proceeds, after);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

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
            //    과거엔 `WHERE player_id = ANY(@ids) ORDER BY player_id FOR UPDATE` 한 문장으로
            //    잠갔으나, Postgres의 행 락은 '정렬 노드 이전' 스캔 단계에서 획득되므로 락 순서는
            //    ordered index scan일 때만 보장된다(bitmap/seq scan이면 ctid 순 → 교착 재발 소지).
            //    플랜에 의존하지 않도록 C#에서 정렬한 뒤 한 지갑씩 개별 FOR UPDATE로 잠근다.
            //    (자전거래는 매칭 단계에서 차단되어 두 id는 항상 다르지만, 방어적으로 중복 제거.)
            foreach (var pid in new[] { a.BuyerId, a.SellerId }.Distinct().OrderBy(x => x))
                await db.ExecuteAsync(
                    "SELECT 1 FROM wallet WHERE player_id = @pid FOR UPDATE", new { pid }, tx);

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
            await CreditWalletAsync(db, tx, a.SellerId, gross, WalletLedgerReason.TradeProceeds, a.TradeId);
            if (fee > 0)
                await CreditWalletAsync(db, tx, a.SellerId, -fee, WalletLedgerReason.Fee, a.TradeId);

            // 3) 매수자: 대금은 이미 에스크로에서 빠졌으므로, 상한가 대비 차익만 환불(+).
            if (improvement > 0)
                await CreditWalletAsync(db, tx, a.BuyerId, improvement, WalletLedgerReason.OrderRefund, a.TradeId);

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

    private static Task InsertItemLedgerAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, Guid playerId, StashEntryKind kind,
        int templateId, Guid? instanceId, int deltaQty, ItemLedgerReason reason, Guid? refId)
        => db.ExecuteAsync(
            @"INSERT INTO item_ledger(player_id, kind, template_id, instance_id, delta_qty, reason, ref_id)
              VALUES (@playerId, @kind, @templateId, @instanceId, @deltaQty, @reason, @refId)",
            new { playerId, kind = kind.ToDb(), templateId, instanceId, deltaQty, reason = reason.ToDb(), refId }, tx);

    /// <summary>플레이어 아이템 원장(item_ledger, append-only)을 최신순으로 페이지네이션 조회.</summary>
    public async Task<PagedResult<ItemLedgerEntryDto>> GetItemLedgerAsync(Guid playerId, int page, int size)
    {
        await using var db = Open();
        var total = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM item_ledger WHERE player_id = @playerId", new { playerId });
        var rows = await db.QueryAsync(
            @"SELECT id, player_id, kind, template_id, instance_id, delta_qty, reason, ref_id, created_at
              FROM item_ledger WHERE player_id = @playerId
              ORDER BY id DESC LIMIT @size OFFSET @offset",
            new { playerId, size, offset = (page - 1) * size });
        var items = rows.Select(r => new ItemLedgerEntryDto(
            (long)r.id, (Guid)r.player_id, Enums.ToStashKind((string)r.kind),
            (int)r.template_id, (Guid?)r.instance_id, (int)r.delta_qty,
            Enums.ToItemLedgerReason((string)r.reason), (Guid?)r.ref_id,
            (DateTimeOffset)r.created_at)).ToList();
        return new PagedResult<ItemLedgerEntryDto>(items, page, size, total);
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

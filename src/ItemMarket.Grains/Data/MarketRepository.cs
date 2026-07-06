using Dapper;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Equipment;
using ItemMarket.Contracts.Items;
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
public sealed class MarketRepository(
    string connectionString,
    int raidDurationSeconds = 180)
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

    // ── 드롭테이블(존 티어) ────────────────────────────────────────────────
    // rarity 순서 고정. 존별 가중치[COMMON,UNCOMMON,RARE,EPIC,LEGENDARY] + loot당 사망확률 상승률.
    // 고위험 존일수록 좋은 등급이 잘 나오지만 사망확률이 빠르게 오른다(리스크/보상, #1 연동).
    private static readonly string[] Rarities = ["COMMON", "UNCOMMON", "RARE", "EPIC", "LEGENDARY"];
    private static readonly Dictionary<RaidZone, (int[] Weights, int DeathIncBps)> ZoneConfig = new()
    {
        [RaidZone.Low] = ([55, 30, 12, 3, 0], 800),
        [RaidZone.Med] = ([35, 32, 22, 9, 2], 1200),
        [RaidZone.High] = ([15, 30, 35, 15, 5], 2000),
    };

    /// <summary>존 가중치로 rarity 하나를 뽑는다(Random.Shared). weight=0인 등급은 뽑히지 않는다.</summary>
    private static string RollRarity(int[] weights)
    {
        int total = weights.Sum();
        int r = Random.Shared.Next(total);
        for (int i = 0; i < weights.Length; i++)
        {
            if (r < weights[i]) return Rarities[i];
            r -= weights[i];
        }
        return Rarities[0];
    }

    private static RaidZone ParseZone(string z) => z switch
    {
        "Low" => RaidZone.Low,
        "High" => RaidZone.High,
        _ => RaidZone.Med
    };

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
              WHERE pe.player_id = @playerId
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
    //  익스트랙션 레이드 — 서비스 계층 상태기계 + 원자적 정산(단일 Postgres 트랜잭션).
    //  at-risk(위험) = 스태시 밖 전부: 장착 장비(HELMET/ARMOR/WEAPON/BACKPACK/RIG) +
    //    장착된 백팩/리그의 중첩 그리드 내용물 + 주머니(POCKETS). STASH는 절대 무관(항상 안전).
    //  매도 에스크로와 동일한 "자산 잠금" 이동을 재사용한다:
    //    StartRaid = 위 대상을 위험(at-risk)으로 잠금(inventory_stack 차감 / instance owner=NULL /
    //                장착 슬롯 해제),
    //    Extract   = 반입+획득 전량을 소유로 복귀(스택 가산 / instance owner 복원) 후 원위치(장착
    //                슬롯/컨테이너 내부/주머니)로 정확히 복원, 획득분은 반입 공간에 first-fit,
    //    Die       = 위험 전량 소실(스택 미복귀 / instance tombstone). 스태시(안전)는 무관.
    //  모든 이동은 item_ledger(append-only)에 기록한다(wallet_ledger 패턴 차용).
    // ======================================================================

    /// <summary>현재 ACTIVE 레이드 세션 스냅샷. 활성 세션이 없으면 null(계약: null = 진행 중 레이드 없음).
    /// 해결된(EXTRACTED/DIED) 세션은 반환하지 않는다 — 결과 화면은 extract/die 응답으로 표시한다.</summary>
    /// <summary>플레이어에게 진행 중(ACTIVE) 레이드 세션이 있는가. 레이드 중에는 스태시/장비 변이를
    /// 잠가야 한다 — 반입 아이템은 필드에 나가 있고, 변이를 허용하면 Extract 원위치 복원이 새 배치와
    /// 고유 제약(슬롯/셀)에서 충돌해 정산이 깨진다(A-1).</summary>
    public async Task<bool> HasActiveRaidAsync(Guid playerId)
    {
        await using var db = Open();
        return await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM raid_session WHERE player_id = @playerId AND status = 'ACTIVE')",
            new { playerId });
    }

    public async Task<RaidSessionDto?> GetRaidSnapshotAsync(Guid playerId)
    {
        await using var db = Open();
        var s = await db.QuerySingleOrDefaultAsync(
            @"SELECT id, player_id, status
              FROM raid_session WHERE player_id = @playerId AND status = 'ACTIVE'
              LIMIT 1",
            new { playerId });
        if (s is null) return null;
        return await LoadRaidDtoAsync(db, null, (Guid)s.id, (Guid)s.player_id,
            Enums.ToRaidStatus((string)s.status));
    }

    /// <summary>
    /// StartRaid(한 트랜잭션): ACTIVE 세션이 없어야 하며, 스태시 밖 전부(장착 장비 + 장착된
    /// 백팩/리그 내용물 + 주머니)를 위험 스냅샷으로 옮기고 인벤토리 가용성에서 제거(에스크로)한 뒤
    /// 그 배치를 비운다. STASH는 절대 건드리지 않는다(항상 안전). RAID_BROUGHT 기록.
    /// 스태시 밖이 전부 비어 있으면(장비도 없고 주머니도 비고 컨테이너도 없으면) RaidNothingToDeploy로
    /// 거부한다 — 반대로 장착 장비만 있어도(주머니가 비어도) 출격은 항상 성공해야 한다.
    /// </summary>
    public async Task<RaidSessionDto> StartRaidAsync(Guid playerId, RaidZone zone)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            // 1) ACTIVE 중복 방지(부분 유니크 인덱스가 최종 강판; 명확한 에러 위해 선검사).
            var existing = await db.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM raid_session WHERE player_id = @playerId AND status = 'ACTIVE'",
                new { playerId }, tx);
            if (existing is not null)
                throw new DomainException(ErrorCode.RaidActive, "이미 진행 중인 레이드가 있습니다.");

            // 2) 위험(at-risk) 대상 수집 = 스태시 밖 전부: 장착 슬롯(전부) + 장착된 백팩/리그의
            //    중첩 그리드 내용 + 주머니(POCKETS). 장착 인스턴스(+백팩/리그 자체)와 그 안의 내용물,
            //    주머니 아이템이 모두 위험이다. STASH는 이 수집 대상에 전혀 포함되지 않는다(항상 안전).
            var equipment = (await db.QueryAsync(
                @"SELECT pe.slot, pe.instance_id, ii.template_id
                  FROM player_equipment pe JOIN item_instance ii ON ii.id = pe.instance_id
                  WHERE pe.player_id = @playerId ORDER BY pe.slot",
                new { playerId }, tx)).ToList();
            var equippedIds = equipment.Select(e => (Guid)e.instance_id).ToArray();

            // 주머니 배치 + 장착된 백팩/리그의 중첩 배치. x,y까지 읽어 원위치 스냅샷에 쓴다.
            var placements = (await db.QueryAsync(
                @"SELECT container, kind, template_id, instance_id, container_instance_id, x, y, quantity
                  FROM stash_placement
                  WHERE player_id = @playerId
                    AND (container = 'POCKETS'
                         OR (container = 'CONTAINER' AND container_instance_id = ANY(@equippedIds)))",
                new { playerId, equippedIds }, tx)).ToList();

            // 스택(주머니+중첩): template 오름차순 락 순서. 각 물리 컨테이너별 칸(다중 스택 가능). 원위치(x,y) 포함.
            var stackItems = placements.Where(p => (string)p.kind == "STACK")
                .Select(p => (Container: (string)p.container, Cid: (Guid?)p.container_instance_id,
                              TemplateId: (int)p.template_id, Qty: (int)p.quantity,
                              X: (int)p.x, Y: (int)p.y))
                .OrderBy(x => x.TemplateId).ThenBy(x => x.Container).ThenBy(x => x.Y).ThenBy(x => x.X).ToList();

            // 인스턴스: 중첩 그리드 내용(배치=CONTAINER 원위치) + 장착 슬롯(백팩/리그 본체 포함, EQUIP 원위치).
            var instanceItems = placements.Where(p => (string)p.kind == "INSTANCE")
                .Select(p => (TemplateId: (int)p.template_id, InstanceId: (Guid)p.instance_id,
                              OriginContainer: (string)p.container, OriginCid: (Guid?)p.container_instance_id,
                              OriginSlot: (string?)null, OriginX: (int?)(int)p.x, OriginY: (int?)(int)p.y))
                .Concat(equipment.Select(e => (TemplateId: (int)e.template_id, InstanceId: (Guid)e.instance_id,
                              OriginContainer: "EQUIP", OriginCid: (Guid?)null,
                              OriginSlot: (string?)(string)e.slot, OriginX: (int?)null, OriginY: (int?)null)))
                .OrderBy(x => x.InstanceId).ToList();

            // 스태시 밖이 전부 비어 있으면(장비도 없고 주머니/컨테이너도 없으면) 출격할 것이 없다.
            // 장착 장비만 있어도(주머니가 비어 있어도) instanceItems가 equipment로 채워지므로 여기를 통과한다
            // ("장비를 착용했는데 출격이 안됨" 버그 수정 — 장비 유무만으로 반드시 출격 가능해야 한다).
            if (stackItems.Count == 0 && instanceItems.Count == 0)
                throw new DomainException(ErrorCode.RaidNothingToDeploy,
                    "반입할 아이템이 없습니다. 장비를 착용하거나 주머니에 물건을 채운 뒤 출격하세요.");

            // 3) 세션 생성. 출격 마감(deadline)을 now() + 제한시간으로 설정 — 초과 후 extract/loot 시
            //    탈출 실패=사망으로 정산한다(lazy expiry). death_chance_bps는 0에서 시작해 loot마다 상승.
            //    zone은 드롭 등급 가중치·loot당 사망확률 상승률을 결정한다.
            var sessionId = Guid.NewGuid();
            await db.ExecuteAsync(
                @"INSERT INTO raid_session(id, player_id, status, deadline_at, zone)
                  VALUES (@sessionId, @playerId, 'ACTIVE', now() + make_interval(secs => @dur), @zone)",
                new { sessionId, playerId, dur = raidDurationSeconds, zone = zone.ToString() }, tx);

            // 4) 스택 반입: inventory_stack 차감(= 매도 에스크로 이동) + 스냅샷 + 원장 + 해당 배치 제거.
            foreach (var s in stackItems)
            {
                int templateId = s.TemplateId;
                int qty = s.Qty;
                var have = await db.ExecuteScalarAsync<int?>(
                    "SELECT quantity FROM inventory_stack WHERE player_id = @playerId AND template_id = @templateId FOR UPDATE",
                    new { playerId, templateId }, tx);
                if (have is null || have < qty)
                    throw new DomainException(ErrorCode.InsufficientQuantity, "반입 스택 수량이 인벤토리 보유량을 초과합니다.");
                await db.ExecuteAsync(
                    "UPDATE inventory_stack SET quantity = quantity - @qty WHERE player_id = @playerId AND template_id = @templateId",
                    new { qty, playerId, templateId }, tx);
                await db.ExecuteAsync(
                    @"INSERT INTO raid_session_item
                        (session_id, kind, template_id, instance_id, quantity, source,
                         origin_container, origin_container_instance_id, origin_slot, origin_x, origin_y)
                      VALUES (@sessionId, 'STACK', @templateId, NULL, @qty, 'BROUGHT',
                              @originContainer, @originCid, NULL, @originX, @originY)",
                    new { sessionId, templateId, qty, originContainer = s.Container, originCid = s.Cid, originX = s.X, originY = s.Y }, tx);
                await InsertItemLedgerAsync(db, tx, playerId, StashEntryKind.Stack, templateId, null, -qty, ItemLedgerReason.RaidBrought, sessionId);
                await db.ExecuteAsync(
                    @"DELETE FROM stash_placement WHERE player_id = @playerId AND container = @container AND kind = 'STACK'
                      AND template_id = @templateId AND container_instance_id IS NOT DISTINCT FROM @cid",
                    new { playerId, container = s.Container, templateId, cid = s.Cid }, tx);
            }

            // 5) 유니크 반입(장착 + 중첩 내용): owner=NULL + 스냅샷 + 원장 + 배치/장착 제거.
            foreach (var it in instanceItems)
            {
                int templateId = it.TemplateId;
                Guid instanceId = it.InstanceId;
                var owner = await db.ExecuteScalarAsync<Guid?>(
                    "SELECT owner_player_id FROM item_instance WHERE id = @instanceId FOR UPDATE",
                    new { instanceId }, tx);
                if (owner != playerId)
                    throw new DomainException(ErrorCode.InstanceNotOwned, "위험 유니크 아이템의 소유권이 유효하지 않습니다.");
                await db.ExecuteAsync(
                    "UPDATE item_instance SET owner_player_id = NULL WHERE id = @instanceId",
                    new { instanceId }, tx);
                await db.ExecuteAsync(
                    @"INSERT INTO raid_session_item
                        (session_id, kind, template_id, instance_id, quantity, source,
                         origin_container, origin_container_instance_id, origin_slot, origin_x, origin_y)
                      VALUES (@sessionId, 'INSTANCE', @templateId, @instanceId, 1, 'BROUGHT',
                              @originContainer, @originCid, @originSlot, @originX, @originY)",
                    new
                    {
                        sessionId,
                        templateId,
                        instanceId,
                        originContainer = it.OriginContainer,
                        originCid = it.OriginCid,
                        originSlot = it.OriginSlot,
                        originX = it.OriginX,
                        originY = it.OriginY
                    }, tx);
                await InsertItemLedgerAsync(db, tx, playerId, StashEntryKind.Instance, templateId, instanceId, -1, ItemLedgerReason.RaidBrought, sessionId);
                if (it.OriginSlot is not null)
                    await db.ExecuteAsync(
                        "DELETE FROM player_equipment WHERE player_id = @playerId AND slot = @slot",
                        new { playerId, slot = it.OriginSlot }, tx);
                else
                    await db.ExecuteAsync(
                        "DELETE FROM stash_placement WHERE player_id = @playerId AND kind = 'INSTANCE' AND instance_id = @instanceId",
                        new { playerId, instanceId }, tx);
            }

            var dto = await LoadRaidDtoAsync(db, tx, sessionId, playerId, RaidStatus.Active);
            await tx.CommitAsync();
            return dto;
        }
        catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 부분 유니크 인덱스 위반(드문 동시 StartRaid 레이스) → 명확한 도메인 에러.
            await tx.RollbackAsync();
            throw new DomainException(ErrorCode.RaidActive, "이미 진행 중인 레이드가 있습니다.");
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Scavenge(한 트랜잭션, 서버 드롭테이블): 세션 존(zone)의 rarity 가중치로 무엇을·얼마나 드롭할지
    /// 서버가 결정해 ACTIVE 세션에 LOOTED 위험 아이템으로 추가한다(클라이언트는 아이템·수량을 못 정함 →
    /// 무한 인플레 차단). 스택은 스냅샷만(소유 인벤은 Extract 시 가산), 유니크는 item_instance를
    /// owner=NULL·origin=RAID로 즉시 생성한다. loot마다 존별 사망확률을 올린다("한 상자 더"의 대가).
    /// 마감(deadline)을 넘겼으면 탈출 실패=사망으로 정산하고 Dropped=null을 반환한다(lazy expiry).
    /// </summary>
    public async Task<LootResultDto> ScavengeAsync(Guid playerId)
    {
        var timing = await GetActiveRaidTimingAsync(playerId);
        if (timing is { Expired: true })
            return new LootResultDto(null, await ResolveRaidAsync(playerId, extracted: false));

        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            var sess = await db.QuerySingleOrDefaultAsync(
                "SELECT id, zone FROM raid_session WHERE player_id = @playerId AND status = 'ACTIVE' FOR UPDATE",
                new { playerId }, tx);
            if (sess is null)
                throw new DomainException(ErrorCode.RaidNotFound, "진행 중인 레이드가 없습니다.");
            Guid sessionId = (Guid)sess.id;
            var (weights, deathInc) = ZoneConfig[ParseZone((string)sess.zone)];

            // 존 가중치로 rarity 롤 → 그 rarity의 템플릿 중 랜덤 1종(무한생성 대신 서버 권위 드롭).
            var rarity = RollRarity(weights);
            var tpl = await db.QuerySingleAsync(
                @"SELECT id, stackable, max_durability, max_stack FROM item_template
                  WHERE rarity = @rarity ORDER BY random() LIMIT 1",
                new { rarity }, tx);
            int templateId = (int)tpl.id;
            bool stackable = (bool)tpl.stackable;
            int maxStack = (int)tpl.max_stack;

            RaidSessionItemDto dropped;
            if (stackable)
            {
                // 수량 = 1..max_stack(한 스택 상한 이내 — 서버가 보장하므로 상한 초과가 원천 불가).
                int qty = 1 + Random.Shared.Next(maxStack);
                await db.ExecuteAsync(
                    @"INSERT INTO raid_session_item(session_id, kind, template_id, instance_id, quantity, source)
                      VALUES (@sessionId, 'STACK', @templateId, NULL, @qty, 'LOOTED')",
                    new { sessionId, templateId, qty }, tx);
                dropped = new RaidSessionItemDto(StashEntryKind.Stack, templateId, null, qty, RaidItemSource.Looted);
            }
            else
            {
                var instanceId = Guid.NewGuid();
                var durability = (int?)tpl.max_durability;
                // 위험 상태의 유니크(owner=NULL) 즉시 생성 — origin=RAID로 프로버넌스 표시.
                await db.ExecuteAsync(
                    @"INSERT INTO item_instance(id, template_id, owner_player_id, durability, attachments, origin)
                      VALUES (@instanceId, @templateId, NULL, @durability, '[]'::jsonb, 'RAID')",
                    new { instanceId, templateId, durability }, tx);
                await db.ExecuteAsync(
                    @"INSERT INTO raid_session_item(session_id, kind, template_id, instance_id, quantity, source)
                      VALUES (@sessionId, 'INSTANCE', @templateId, @instanceId, 1, 'LOOTED')",
                    new { sessionId, templateId, instanceId }, tx);
                dropped = new RaidSessionItemDto(StashEntryKind.Instance, templateId, instanceId, 1, RaidItemSource.Looted);
            }

            // loot 성공 — 존별 사망확률 상승(extract에서 이 확률로 사망 롤).
            await db.ExecuteAsync(
                "UPDATE raid_session SET death_chance_bps = death_chance_bps + @inc WHERE id = @sessionId",
                new { inc = deathInc, sessionId }, tx);

            var session = await LoadRaidDtoAsync(db, tx, sessionId, playerId, RaidStatus.Active);
            await tx.CommitAsync();
            return new LootResultDto(dropped, session);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>ACTIVE 세션의 만료 여부(deadline 초과)와 누적 사망확률(bps)을 읽는다. 없으면 null.
    /// 만료 판정은 DB now() 기준으로 해 앱-DB 시계차를 피한다.</summary>
    private async Task<(bool Expired, int ChanceBps)?> GetActiveRaidTimingAsync(Guid playerId)
    {
        await using var db = Open();
        var r = await db.QuerySingleOrDefaultAsync(
            @"SELECT (deadline_at IS NOT NULL AND deadline_at < now()) AS expired, death_chance_bps
              FROM raid_session WHERE player_id = @playerId AND status = 'ACTIVE'",
            new { playerId });
        if (r is null) return null;
        return ((bool)r.expired, (int)r.death_chance_bps);
    }

    /// <summary>
    /// Extract 시도: 마감(deadline)을 넘겼으면 탈출 실패=사망으로 정산한다. 아니면 누적 사망확률로
    /// 롤 — 성공하면 EXTRACTED(반입+획득 전량 소유·원위치 복원), 실패하면 DIED(전량 소실).
    /// chance=0이면 항상 생존, chance≥100%면 항상 사망(경계는 결정론적). "한 상자 더 vs 지금 탈출" 도박.
    /// 생존 시 RAID_EXTRACT(반입)/RAID_LOOT(획득) 기록.
    /// </summary>
    public async Task<RaidSessionDto> ExtractAsync(Guid playerId)
    {
        var t = await GetActiveRaidTimingAsync(playerId);
        if (t is null) return await ResolveRaidAsync(playerId, extracted: true); // 없으면 ResolveRaid가 RaidNotFound
        bool died = t.Value.Expired || Random.Shared.NextDouble() < t.Value.ChanceBps / 10000.0;
        return await ResolveRaidAsync(playerId, extracted: !died);
    }

    /// <summary>
    /// Die(한 트랜잭션): ACTIVE→DIED. 위험 아이템 전량 소실(스택 미복귀 / 유니크 tombstone: owner=NULL,
    /// origin=RAID_LOST). 스태시(안전)는 무관. 소실은 item_ledger에 별도 기록하지 않는다(M4 대칭화 —
    /// 반입분은 RaidBrought에서 이미 debit, 전리품은 credit이 없음). 손실 감사는 raid_session(DIED)+raid_session_item.
    /// </summary>
    public async Task<RaidSessionDto> DieAsync(Guid playerId)
        => await ResolveRaidAsync(playerId, extracted: false);

    private async Task<RaidSessionDto> ResolveRaidAsync(Guid playerId, bool extracted)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            var session = await db.QuerySingleOrDefaultAsync(
                @"SELECT id FROM raid_session
                  WHERE player_id = @playerId AND status = 'ACTIVE' FOR UPDATE",
                new { playerId }, tx);
            if (session is null)
                throw new DomainException(ErrorCode.RaidNotFound, "진행 중인 레이드가 없습니다.");
            Guid sessionId = (Guid)session.id;

            var items = (await db.QueryAsync(
                @"SELECT kind, template_id, instance_id, quantity, source,
                         origin_container, origin_container_instance_id, origin_slot, origin_x, origin_y
                  FROM raid_session_item WHERE session_id = @sessionId ORDER BY id",
                new { sessionId }, tx)).ToList();

            foreach (var it in items)
            {
                var kind = Enums.ToStashKind((string)it.kind);
                var source = Enums.ToRaidSource((string)it.source);
                int templateId = (int)it.template_id;
                Guid? instanceId = (Guid?)it.instance_id;
                int qty = (int)it.quantity;

                if (extracted)
                {
                    var reason = source == RaidItemSource.Looted ? ItemLedgerReason.RaidLoot : ItemLedgerReason.RaidExtract;
                    if (kind == StashEntryKind.Stack)
                    {
                        await UpsertStackAsync(db, tx, playerId, templateId, qty);
                        await InsertItemLedgerAsync(db, tx, playerId, kind, templateId, null, qty, reason, sessionId);
                    }
                    else
                    {
                        // 반입/획득 유니크 모두 owner를 플레이어로 복원(획득분은 이미 origin=RAID).
                        await db.ExecuteAsync(
                            "UPDATE item_instance SET owner_player_id = @playerId WHERE id = @instanceId",
                            new { playerId, instanceId }, tx);
                        await InsertItemLedgerAsync(db, tx, playerId, kind, templateId, instanceId, 1, reason, sessionId);
                    }
                }
                else // died: 소실.
                {
                    // item_ledger는 소유량 프로버넌스 로그다 — sum(delta_qty) == 실제 소유량이 불변식.
                    // 사망 시 소유량 변화는 0이므로 RaidLoss를 남기지 않는다(M4, 대칭화):
                    //   - 반입분: 출격 RaidBrought(-)에서 이미 debit됨. 미복귀=영구 손실이 그 debit으로 이미 표현됨
                    //     → 재차감하면 이중차감(유령 음수).
                    //   - 전리품(LOOTED): 인벤에 들어온 적 없어 사전 credit이 없음 → RaidLoss(-)만 남기면 유령 음수.
                    // "무엇을 잃었나"의 감사는 raid_session(status=DIED)+raid_session_item이 보유한다.
                    if (kind == StashEntryKind.Instance)
                    {
                        // tombstone: owner=NULL 유지 + origin=RAID_LOST(FK 안전한 소각 — 삭제 대신 표식).
                        await db.ExecuteAsync(
                            "UPDATE item_instance SET owner_player_id = NULL, origin = 'RAID_LOST' WHERE id = @instanceId",
                            new { instanceId }, tx);
                    }
                    // 반입 스택은 StartRaid에서 이미 인벤 차감됨(미복귀 = 소실) — 추가 물리/원장 처리 없음.
                }
            }

            // Extract(생존): 소유 복원에 더해 원위치로 복원한다(스태시 자동 덤프가 아님).
            //   BROUGHT은 스냅샷한 정확한 위치(주머니 칸/장착 슬롯/백팩·리그 내부)로,
            //   LOOTED은 반입 공간(백팩·리그 중첩 → 주머니 → STASH 오버플로 순)에 first-fit으로.
            if (extracted)
            {
                var stashRows = await db.ExecuteScalarAsync<int>(
                    "SELECT stash_rows FROM player WHERE id = @playerId", new { playerId }, tx);
                await RestoreExtractedPlacementsAsync(db, tx, playerId, items, stashRows);
            }

            var newStatus = extracted ? RaidStatus.Extracted : RaidStatus.Died;
            await db.ExecuteAsync(
                "UPDATE raid_session SET status = @status, resolved_at = now() WHERE id = @sessionId",
                new { status = newStatus.ToDb(), sessionId }, tx);

            var dto = await LoadRaidDtoAsync(db, tx, sessionId, playerId, newStatus);
            await tx.CommitAsync();
            return dto;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>정합화용 배치 스크래치(메모리 점유 계산). Quantity는 스택 합산으로 갱신 가능.</summary>
    private sealed class PlacementScratch(
        string container, Guid? cid, string kind, int templateId, Guid? instanceId, int x, int y, int quantity)
    {
        public string Container { get; } = container;
        public Guid? Cid { get; } = cid;
        public string Kind { get; } = kind;
        public int TemplateId { get; } = templateId;
        public Guid? InstanceId { get; } = instanceId;
        public int X { get; } = x;
        public int Y { get; } = y;
        public int Quantity { get; set; } = quantity;
    }

    /// <summary>
    /// Extract 원위치 복원(같은 트랜잭션). 소유는 이미 복원된 상태이고 여기서 물리 위치를 복원한다.
    ///   1) BROUGHT: StartRaid에서 스냅샷한 origin으로 정확히 복원 — EQUIP은 player_equipment,
    ///      POCKETS/CONTAINER는 stash_placement의 원래 칸(스태시 자동 덤프가 아님).
    ///   2) LOOTED: 원위치가 없으므로 반입 공간에 first-fit 배치한다 —
    ///      <b>장착된 백팩/리그의 중첩 그리드(슬롯 순) → POCKETS(4×1) → STASH(12×stashRows) 오버플로</b> 순.
    ///      어디에도 안 들어가면 미배치로 남고(소유 유지), 다음 GET /api/stash에서 STASH로 정합화된다.
    /// </summary>
    private static async Task RestoreExtractedPlacementsAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, Guid playerId, List<dynamic> items, int stashRows)
    {
        // 1) BROUGHT 원위치 복원.
        foreach (var it in items)
        {
            if (Enums.ToRaidSource((string)it.source) != RaidItemSource.Brought) continue;
            var originContainer = (string?)it.origin_container;
            if (originContainer is null) continue; // 방어(BROUGHT은 항상 origin 보유)
            var kind = Enums.ToStashKind((string)it.kind);
            int templateId = (int)it.template_id;
            Guid? instanceId = (Guid?)it.instance_id;

            if (originContainer == "EQUIP")
            {
                await db.ExecuteAsync(
                    "INSERT INTO player_equipment(player_id, slot, instance_id) VALUES (@playerId, @slot, @instanceId)",
                    new { playerId, slot = (string?)it.origin_slot, instanceId }, tx);
                continue;
            }

            Guid? cid = (Guid?)it.origin_container_instance_id;
            int x = (int)it.origin_x, y = (int)it.origin_y;
            if (kind == StashEntryKind.Stack)
                await db.ExecuteAsync(
                    @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
                      VALUES (@playerId, @container, 'STACK', @templateId, NULL, @cid, @x, @y, @qty)",
                    new { playerId, container = originContainer, templateId, cid, x, y, qty = (int)it.quantity }, tx);
            else
                await db.ExecuteAsync(
                    @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
                      VALUES (@playerId, @container, 'INSTANCE', @templateId, @instanceId, @cid, @x, @y, 1)",
                    new { playerId, container = originContainer, templateId, instanceId, cid, x, y }, tx);
        }

        // 2) LOOTED 반입 공간 배치.
        var looted = items.Where(it => Enums.ToRaidSource((string)it.source) == RaidItemSource.Looted).ToList();
        if (looted.Count == 0) return;

        // 카탈로그 footprint + max_stack + 중첩 컨테이너 크기.
        var templates = (await db.QueryAsync(
            "SELECT id, grid_w, grid_h, is_container, container_w, container_h, max_stack FROM item_template", null, tx)).ToList();
        var footprints = templates.ToDictionary(t => (int)t.id, t => ((int)t.grid_w, (int)t.grid_h));
        var maxStacks = templates.ToDictionary(t => (int)t.id, t => (int)t.max_stack);
        var containerDims = templates.Where(t => (bool)t.is_container)
            .ToDictionary(t => (int)t.id, t => ((int)t.container_w, (int)t.container_h));

        // 장착된 백팩/리그(중첩 컨테이너) — 슬롯 순. 배치 우선순위의 앞쪽.
        var equipped = (await db.QueryAsync(
            @"SELECT pe.slot, pe.instance_id, ii.template_id
              FROM player_equipment pe JOIN item_instance ii ON ii.id = pe.instance_id
              WHERE pe.player_id = @playerId ORDER BY pe.slot",
            new { playerId }, tx)).ToList();

        // 배치 대상 컨테이너 우선순위: 중첩(백팩/리그, 슬롯 순) → POCKETS(4×1) → STASH(오버플로).
        var targets = new List<(string Container, Guid? Cid, int W, int H)>();
        foreach (var e in equipped)
            if (containerDims.TryGetValue((int)e.template_id, out var dims))
                targets.Add(("CONTAINER", (Guid)e.instance_id, dims.Item1, dims.Item2));
        targets.Add(("POCKETS", null, StashGeometry.PocketsWidth, StashGeometry.PocketsHeight));
        targets.Add(("STASH", null, StashGeometry.StashWidth, stashRows));

        // 현재 배치(방금 복원한 BROUGHT + 남아있던 안전 배치)를 메모리로 로드해 점유 계산에 쓴다.
        var placements = (await db.QueryAsync(
            @"SELECT container, kind, template_id, instance_id, container_instance_id, x, y, quantity
              FROM stash_placement WHERE player_id = @playerId",
            new { playerId }, tx))
            .Select(p => new PlacementScratch(
                (string)p.container, (Guid?)p.container_instance_id, (string)p.kind,
                (int)p.template_id, (Guid?)p.instance_id, (int)p.x, (int)p.y, (int)p.quantity))
            .ToList();

        static bool SameTarget(PlacementScratch p, (string Container, Guid? Cid, int W, int H) t)
            => p.Container == t.Container && p.Cid == t.Cid;

        foreach (var it in looted)
        {
            var kind = Enums.ToStashKind((string)it.kind);
            int templateId = (int)it.template_id;
            Guid? instanceId = (Guid?)it.instance_id;
            int qty = (int)it.quantity;
            var (w, h) = kind == StashEntryKind.Stack ? (1, 1) : footprints.GetValueOrDefault(templateId, (1, 1));

            if (kind == StashEntryKind.Instance)
            {
                // 유니크는 항상 통째로 한 칸에.
                if (!TryPlaceInAnyTarget(w, h, out var fx, out var fy, out var chosen)) continue;
                await db.ExecuteAsync(
                    @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
                      VALUES (@playerId, @container, 'INSTANCE', @templateId, @instanceId, @cid, @x, @y, 1)",
                    new { playerId, container = chosen.Container, templateId, instanceId, cid = chosen.Cid, x = fx, y = fy }, tx);
                placements.Add(new PlacementScratch(chosen.Container, chosen.Cid, "INSTANCE", templateId, instanceId, fx, fy, 1));
                continue;
            }

            // 스택은 max_stack 단위로 쪼개 배치한다(다중 스택 — 한 칸이 상한을 넘지 않게).
            var maxStack = maxStacks.GetValueOrDefault(templateId, 1);
            var remaining = qty;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, maxStack);
                var placedChunk = false;

                foreach (var t in targets)
                {
                    // 같은 물리 컨테이너에 동일 템플릿 칸이 있으면 여유분만큼 병합(초과분은 다음 target으로).
                    var merge = placements.FirstOrDefault(p => p.Kind == "STACK" && p.TemplateId == templateId && SameTarget(p, t));
                    if (merge is not null)
                    {
                        var room = maxStack - merge.Quantity;
                        if (room <= 0) continue;
                        var add = Math.Min(room, chunk);
                        await db.ExecuteAsync(
                            @"UPDATE stash_placement SET quantity = quantity + @add
                              WHERE player_id = @playerId AND container = @container AND kind = 'STACK'
                                AND template_id = @templateId AND container_instance_id IS NOT DISTINCT FROM @cid
                                AND x = @x AND y = @y",
                            new { add, playerId, container = t.Container, templateId, cid = t.Cid, merge.X, merge.Y }, tx);
                        merge.Quantity += add;
                        remaining -= add;
                        placedChunk = true;
                        break;
                    }

                    var occupied = placements.Where(p => SameTarget(p, t)).Select(p =>
                    {
                        var (pw, ph) = p.Kind == "STACK" ? (1, 1) : footprints.GetValueOrDefault(p.TemplateId, (1, 1));
                        return new Rect(p.X, p.Y, pw, ph);
                    }).ToList();
                    var fit = StashGeometry.FirstFit(t.W, t.H, occupied, 1, 1);
                    if (fit is null) continue;
                    var (fx, fy) = fit.Value;

                    await db.ExecuteAsync(
                        @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
                          VALUES (@playerId, @container, 'STACK', @templateId, NULL, @cid, @x, @y, @chunk)",
                        new { playerId, container = t.Container, templateId, cid = t.Cid, x = fx, y = fy, chunk }, tx);
                    placements.Add(new PlacementScratch(t.Container, t.Cid, "STACK", templateId, null, fx, fy, chunk));
                    remaining -= chunk;
                    placedChunk = true;
                    break;
                }

                // 어느 target에도 이 칸을 놓을 자리가 없으면 남은 수량은 미배치로 남긴다
                // (소유는 이미 부여됨 — 다음 GET /api/stash의 정합화가 STASH에 자동 배치한다).
                if (!placedChunk) break;
            }

            bool TryPlaceInAnyTarget(int fw, int fh, out int fx, out int fy, out (string Container, Guid? Cid, int W, int H) chosen)
            {
                foreach (var t in targets)
                {
                    var occupied = placements.Where(p => SameTarget(p, t)).Select(p =>
                    {
                        var (pw, ph) = p.Kind == "STACK" ? (1, 1) : footprints.GetValueOrDefault(p.TemplateId, (1, 1));
                        return new Rect(p.X, p.Y, pw, ph);
                    }).ToList();
                    var fit = StashGeometry.FirstFit(t.W, t.H, occupied, fw, fh);
                    if (fit is null) continue;
                    (fx, fy) = fit.Value;
                    chosen = t;
                    return true;
                }
                fx = fy = 0;
                chosen = default;
                return false;
            }
        }
    }

    /// <summary>세션 + 위험 아이템 목록을 RaidSessionDto로 조립. started_at/resolved_at은
    /// 앱 시각을 넘겨받지 않고 DB에서 읽는다(단일 진실 소스) — AddLoot가 출격 시각 대신 loot
    /// 호출 시각을 반환하던 버그를 근본 제거하고, StartRaid/Resolve의 앱시각↔DB now() 불일치도 없앤다.</summary>
    private static async Task<RaidSessionDto> LoadRaidDtoAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, Guid sessionId, Guid playerId, RaidStatus status)
    {
        var s = await db.QuerySingleAsync(
            "SELECT started_at, resolved_at, deadline_at, death_chance_bps FROM raid_session WHERE id = @sessionId",
            new { sessionId }, tx);
        var items = (await db.QueryAsync(
            "SELECT kind, template_id, instance_id, quantity, source FROM raid_session_item WHERE session_id = @sessionId ORDER BY id",
            new { sessionId }, tx))
            .Select(r => new RaidSessionItemDto(
                Enums.ToStashKind((string)r.kind), (int)r.template_id, (Guid?)r.instance_id,
                (int)r.quantity, Enums.ToRaidSource((string)r.source))).ToList();
        // deadline_at·death_chance_bps는 ACTIVE 세션에서만 의미가 있다(해결된 세션은 null/무시).
        var deadlineAt = status == RaidStatus.Active ? (DateTimeOffset?)s.deadline_at : null;
        var deathChanceBps = status == RaidStatus.Active ? (int)s.death_chance_bps : 0;
        return new RaidSessionDto(sessionId, playerId, status,
            (DateTimeOffset)s.started_at, (DateTimeOffset?)s.resolved_at, items, deadlineAt, deathChanceBps);
    }

    private static Task InsertItemLedgerAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, Guid playerId, StashEntryKind kind,
        int templateId, Guid? instanceId, int deltaQty, ItemLedgerReason reason, Guid? refId)
        => db.ExecuteAsync(
            @"INSERT INTO item_ledger(player_id, kind, template_id, instance_id, delta_qty, reason, ref_id)
              VALUES (@playerId, @kind, @templateId, @instanceId, @deltaQty, @reason, @refId)",
            new { playerId, kind = kind.ToDb(), templateId, instanceId, deltaQty, reason = reason.ToDb(), refId }, tx);

    /// <summary>
    /// 레이드 이력(해결된 세션: EXTRACTED/DIED)을 페이지네이션으로 조회. ACTIVE는 제외한다
    /// (진행 중 세션은 GET /api/raid로 본다). 각 세션의 아이템 스냅샷을 한 번의 쿼리로 묶어 붙인다(N+1 회피).
    /// </summary>
    public async Task<PagedResult<RaidHistoryEntryDto>> GetRaidHistoryAsync(Guid playerId, int page, int size)
    {
        await using var db = Open();
        var total = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM raid_session WHERE player_id = @playerId AND status IN ('EXTRACTED','DIED')",
            new { playerId });
        var sessions = (await db.QueryAsync(
            @"SELECT id, status, started_at, resolved_at
              FROM raid_session
              WHERE player_id = @playerId AND status IN ('EXTRACTED','DIED')
              ORDER BY started_at DESC, id DESC
              LIMIT @size OFFSET @offset",
            new { playerId, size, offset = (page - 1) * size })).ToList();

        var ids = sessions.Select(s => (Guid)s.id).ToArray();
        var itemsBySession = new Dictionary<Guid, List<RaidSessionItemDto>>();
        if (ids.Length > 0)
        {
            var itemRows = await db.QueryAsync(
                @"SELECT session_id, kind, template_id, instance_id, quantity, source
                  FROM raid_session_item WHERE session_id = ANY(@ids) ORDER BY id",
                new { ids });
            foreach (var r in itemRows)
            {
                var sid = (Guid)r.session_id;
                if (!itemsBySession.TryGetValue(sid, out var list))
                    itemsBySession[sid] = list = [];
                list.Add(new RaidSessionItemDto(
                    Enums.ToStashKind((string)r.kind), (int)r.template_id, (Guid?)r.instance_id,
                    (int)r.quantity, Enums.ToRaidSource((string)r.source)));
            }
        }

        var items = sessions.Select(s => new RaidHistoryEntryDto(
            (Guid)s.id, Enums.ToRaidStatus((string)s.status),
            (DateTimeOffset)s.started_at, (DateTimeOffset?)s.resolved_at,
            itemsBySession.GetValueOrDefault((Guid)s.id, []))).ToList();
        return new PagedResult<RaidHistoryEntryDto>(items, page, size, total);
    }

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

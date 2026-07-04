using System.Text;
using Npgsql;

namespace ItemMarket.LoadTest;

/// <summary>
/// 격리 부하 DB에 대한 직접 접근(Npgsql). 시딩은 빠르게 대량 INSERT 하고,
/// 불변식(invariant) 검증은 부하 종료 후 SQL 집계로 수행한다.
///
/// 테스트 템플릿은 스택형(FOOD/MEDICAL, id 1..52) 중 앞쪽 T개를 사용한다.
/// 스택형이라 매도 에스크로 = 인벤 수량 차감이므로, 넉넉히 지급하면 매도가 항상 성립.
/// </summary>
public sealed class Db(string connString)
{
    /// <summary>index → 결정적 플레이어 GUID (시드 플레이어와 충돌하지 않는 프리픽스).</summary>
    public static Guid PlayerId(int index) => Guid.Parse($"10000000-0000-0000-0000-{index:D12}");

    /// <summary>테스트 템플릿 id 목록(1부터). 전부 스택형.</summary>
    public static int[] TemplateIds(int count) => Enumerable.Range(1, count).ToArray();

    private NpgsqlConnection Open()
    {
        var c = new NpgsqlConnection(connString);
        c.Open();
        return c;
    }

    /// <summary>
    /// 도메인 상태를 초기화하고 합성 플레이어/지갑/재고를 시딩한다.
    /// item_template / market_config 는 보존(카탈로그·수수료율).
    /// </summary>
    public async Task SetupAsync(Options o)
    {
        await using var db = Open();

        // 1) 도메인 상태 리셋(카탈로그/설정은 유지). 매 실행 깨끗한 baseline 확보.
        await using (var reset = new NpgsqlCommand(
            @"TRUNCATE trade, market_order, wallet_ledger, stash_placement,
                       inventory_stack, item_instance, wallet, player
              RESTART IDENTITY CASCADE;", db))
        {
            await reset.ExecuteNonQueryAsync();
        }

        var templates = TemplateIds(o.Templates);

        // 2) 플레이어 + 지갑 대량 삽입 (COPY 로 빠르게).
        await using (var import = db.BeginBinaryImport(
            "COPY player (id, display_name) FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 0; i < o.Players; i++)
            {
                await import.StartRowAsync();
                await import.WriteAsync(PlayerId(i), NpgsqlTypes.NpgsqlDbType.Uuid);
                await import.WriteAsync($"load_{i}", NpgsqlTypes.NpgsqlDbType.Text);
            }
            await import.CompleteAsync();
        }

        await using (var import = db.BeginBinaryImport(
            "COPY wallet (player_id, balance) FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 0; i < o.Players; i++)
            {
                await import.StartRowAsync();
                await import.WriteAsync(PlayerId(i), NpgsqlTypes.NpgsqlDbType.Uuid);
                await import.WriteAsync(o.PlayerBalance, NpgsqlTypes.NpgsqlDbType.Bigint);
            }
            await import.CompleteAsync();
        }

        // 3) 재고: 플레이어 × 템플릿 매트릭스로 지급(매도가 항상 성립하도록 넉넉히).
        await using (var import = db.BeginBinaryImport(
            "COPY inventory_stack (player_id, template_id, quantity) FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 0; i < o.Players; i++)
            {
                foreach (var t in templates)
                {
                    await import.StartRowAsync();
                    await import.WriteAsync(PlayerId(i), NpgsqlTypes.NpgsqlDbType.Uuid);
                    await import.WriteAsync(t, NpgsqlTypes.NpgsqlDbType.Integer);
                    await import.WriteAsync(o.GrantQty, NpgsqlTypes.NpgsqlDbType.Integer);
                }
            }
            await import.CompleteAsync();
        }
    }

    /// <summary>부하 시작 전 baseline: 발행된 총 병뚜껑(= 전 지갑 잔액 합, 이 시점엔 주문/체결 없음).</summary>
    public async Task<long> InitialCapsAsync()
    {
        await using var db = Open();
        await using var cmd = new NpgsqlCommand("SELECT COALESCE(SUM(balance),0)::bigint FROM wallet", db);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>부하 종료 후 불변식 검증 결과.</summary>
    public async Task<InvariantReport> CheckInvariantsAsync(Options o, long initialCaps)
    {
        await using var db = Open();

        async Task<long> Scalar(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, db);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }

        var negativeWallets = await Scalar("SELECT count(*) FROM wallet WHERE balance < 0");

        // 보존(conservation): 지갑 잔액 + 매수 잠금 에스크로 + 소각된 수수료 == 최초 발행량.
        var walletSum = await Scalar("SELECT COALESCE(SUM(balance),0) FROM wallet");
        var escrowSum = await Scalar("SELECT COALESCE(SUM(escrow_caps),0) FROM market_order");
        var feeSum = await Scalar("SELECT COALESCE(SUM(fee_amount),0) FROM trade");
        var capsAccounted = walletSum + escrowSum + feeSum;

        // 아이템 보존: 템플릿별 (인벤 수량 + 미체결 매도 잔량) == 최초 지급량(플레이어수 × 지급량).
        var expectedPerTemplate = (long)o.Players * o.GrantQty;
        var itemMismatches = new List<(int Template, long Actual)>();
        foreach (var t in TemplateIds(o.Templates))
        {
            var inv = await Scalar($"SELECT COALESCE(SUM(quantity),0) FROM inventory_stack WHERE template_id = {t}");
            var sellEscrow = await Scalar(
                $"SELECT COALESCE(SUM(remaining_quantity),0) FROM market_order " +
                $"WHERE template_id = {t} AND side = 'SELL' AND status IN ('OPEN','PARTIALLY_FILLED')");
            if (inv + sellEscrow != expectedPerTemplate)
                itemMismatches.Add((t, inv + sellEscrow));
        }

        // 주문 상태 정합성: 있어선 안 되는 상태 조합의 개수.
        var remGtQty = await Scalar("SELECT count(*) FROM market_order WHERE remaining_quantity > quantity");
        var openZero = await Scalar("SELECT count(*) FROM market_order WHERE status = 'OPEN' AND remaining_quantity = 0");
        var filledNonZero = await Scalar("SELECT count(*) FROM market_order WHERE status = 'FILLED' AND remaining_quantity <> 0");
        var partialBad = await Scalar(
            "SELECT count(*) FROM market_order WHERE status = 'PARTIALLY_FILLED' " +
            "AND (remaining_quantity = 0 OR remaining_quantity >= quantity)");
        var cancelledEscrow = await Scalar("SELECT count(*) FROM market_order WHERE status = 'CANCELLED' AND escrow_caps <> 0");

        var trades = await Scalar("SELECT count(*) FROM trade");
        var orders = await Scalar("SELECT count(*) FROM market_order");

        return new InvariantReport(
            NegativeWallets: negativeWallets,
            InitialCaps: initialCaps,
            CapsAccounted: capsAccounted,
            WalletSum: walletSum,
            EscrowSum: escrowSum,
            FeeSum: feeSum,
            ItemMismatches: itemMismatches,
            ExpectedPerTemplate: expectedPerTemplate,
            RemainingGtQuantity: remGtQty,
            OpenWithZeroRemaining: openZero,
            FilledWithNonZeroRemaining: filledNonZero,
            PartiallyFilledInvalid: partialBad,
            CancelledWithEscrow: cancelledEscrow,
            DbTrades: trades,
            DbOrders: orders);
    }
}

public sealed record InvariantReport(
    long NegativeWallets,
    long InitialCaps,
    long CapsAccounted,
    long WalletSum,
    long EscrowSum,
    long FeeSum,
    IReadOnlyList<(int Template, long Actual)> ItemMismatches,
    long ExpectedPerTemplate,
    long RemainingGtQuantity,
    long OpenWithZeroRemaining,
    long FilledWithNonZeroRemaining,
    long PartiallyFilledInvalid,
    long CancelledWithEscrow,
    long DbTrades,
    long DbOrders)
{
    public bool CapsConserved => InitialCaps == CapsAccounted;
    public bool ItemsConserved => ItemMismatches.Count == 0;
    public bool StatesConsistent =>
        RemainingGtQuantity == 0 && OpenWithZeroRemaining == 0 && FilledWithNonZeroRemaining == 0
        && PartiallyFilledInvalid == 0 && CancelledWithEscrow == 0;
    public bool AllPass => NegativeWallets == 0 && CapsConserved && ItemsConserved && StatesConsistent;

    public string Render()
    {
        var sb = new StringBuilder();
        void Line(string name, bool ok, string detail) =>
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name,-26} {detail}");

        Line("no negative wallets", NegativeWallets == 0, $"count={NegativeWallets}");
        Line("caps conservation", CapsConserved,
            $"initial={InitialCaps:N0}  accounted={CapsAccounted:N0}  " +
            $"(wallets={WalletSum:N0} + escrow={EscrowSum:N0} + fees={FeeSum:N0})  diff={InitialCaps - CapsAccounted}");
        Line("item conservation", ItemsConserved,
            ItemsConserved ? $"all templates == {ExpectedPerTemplate:N0}"
                           : $"mismatches={string.Join(",", ItemMismatches.Select(m => $"t{m.Template}:{m.Actual}"))}");
        Line("order state sanity", StatesConsistent,
            $"rem>qty={RemainingGtQuantity} openRem0={OpenWithZeroRemaining} " +
            $"filledRem!=0={FilledWithNonZeroRemaining} partialBad={PartiallyFilledInvalid} cancelEscrow={CancelledWithEscrow}");
        return sb.ToString();
    }
}

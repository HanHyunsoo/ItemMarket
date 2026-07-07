using System.Net;
using System.Net.Http.Json;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Leaderboard;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Trades;
using ItemMarket.Contracts.Wallet;
using ItemMarket.Grains.Data;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static ItemMarket.IntegrationTests.MarketAppFixture;

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 엔드투엔드 통합테스트(머니샷). 실제 API+Orleans+Postgres를 통과.
/// 각 테스트는 서로 다른 아이템 템플릿을 써서 호가창을 격리하고,
/// 지갑은 before/after 델타로 검증해 실행 순서에 무관하게 만든다.
/// </summary>
[Collection("market")]
public class MarketFlowTests(MarketAppFixture f)
{
    private readonly MarketAppFixture _f = f;

    private static async Task<long> Balance(HttpClient c)
        => (await Api<WalletDto>(await c.GetAsync("/api/wallet"))).Data!.Balance;

    private static async Task<int> StackQty(HttpClient c, int templateId)
    {
        var inv = (await Api<InventoryDto>(await c.GetAsync("/api/inventory"))).Data!;
        return inv.Stacks.FirstOrDefault(s => s.TemplateId == templateId)?.Quantity ?? 0;
    }

    [Fact]
    public async Task Catalog_returns_149_templates()
    {
        var c = await _f.AuthedAs(Alpha);
        var res = await Api<List<ItemTemplateDto>>(await c.GetAsync("/api/catalog"));
        Assert.True(res.Success);
        // 아이템 마스터 132종(FOOD/MEDICAL/MELEE/GUN/AMMO) + 장비(GEAR) 17종
        // (헬멧/방어구/리그/백팩 티어, id 103~106 + 133~149) = 149.
        Assert.Equal(149, res.Data!.Count);
    }

    // 머니샷: 매도 대기 → 매칭 매수 → 원자 정산(대금·아이템·수수료 sink)
    [Fact]
    public async Task Sell_then_matching_buy_settles_atomically()
    {
        var seller = await _f.AuthedAs(Alpha); // 시드: 95번(7.62mm) 120개 보유
        var buyer = await _f.AuthedAs(Bravo);

        var sBefore = await Balance(seller);
        var bBefore = await Balance(buyer);
        var buyerQty0 = await StackQty(buyer, 95);

        var sell = await Api<PlaceOrderResult>(await seller.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Sell, 95, 10, 10), Json));
        Assert.True(sell.Success);
        Assert.Empty(sell.Data!.Fills); // 아직 매수 없음 → 호가창 대기

        var buy = await Api<PlaceOrderResult>(await buyer.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Buy, 95, 10, 10), Json));
        Assert.True(buy.Success);

        var trade = Assert.Single(buy.Data!.Fills);
        Assert.Equal(10, trade.Quantity);
        Assert.Equal(10, trade.UnitPrice);
        Assert.Equal(5, trade.FeeAmount); // 100 * 5% = 5 소각

        Assert.Equal(sBefore + 95, await Balance(seller));  // +100 -5 수수료
        Assert.Equal(bBefore - 100, await Balance(buyer));  // 대금 지불
        Assert.Equal(buyerQty0 + 10, await StackQty(buyer, 95));
    }

    // 부분 체결: 남은 물량이 호가창에 잔존
    [Fact]
    public async Task Partial_fill_leaves_remainder_on_book()
    {
        var seller = await _f.AuthedAs(Bravo); // 시드: 93번(9mm) 200개 보유
        var buyer = await _f.AuthedAs(Alpha);

        var sell = await Api<PlaceOrderResult>(await seller.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Sell, 93, 4, 5), Json)); // 5개 @4 매도
        Assert.True(sell.Success);

        var buy = await Api<PlaceOrderResult>(await buyer.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Buy, 93, 4, 3), Json)); // 3개 @4 매수
        Assert.True(buy.Success);
        Assert.Equal(3, buy.Data!.Fills.Sum(x => x.Quantity));
        Assert.Equal(OrderStatus.Filled, buy.Data!.Order.Status);
        Assert.Equal(0, buy.Data!.Order.RemainingQuantity);

        var book = await Api<OrderBookSnapshotDto>(await buyer.GetAsync("/api/market/93/book"));
        var ask = Assert.Single(book.Data!.Asks, a => a.UnitPrice == 4);
        Assert.Equal(2, ask.Quantity); // 남은 매도 2
    }

    // 주문 취소 → 에스크로 전액 환불
    [Fact]
    public async Task Cancel_refunds_escrow()
    {
        var buyer = await _f.AuthedAs(Alpha);
        var before = await Balance(buyer);

        var buy = await Api<PlaceOrderResult>(await buyer.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Buy, 30, 50, 2), Json)); // 매도자 없는 30번
        Assert.True(buy.Success);
        Assert.Empty(buy.Data!.Fills);
        Assert.Equal(before - 100, await Balance(buyer)); // 에스크로 잠김

        var del = await buyer.DeleteAsync($"/api/orders/{buy.Data!.Order.Id}");
        del.EnsureSuccessStatusCode();
        Assert.Equal(before, await Balance(buyer)); // 전액 환불
    }

    // 인증/인가: 무토큰 401, 비어드민 403, 어드민 200
    [Fact]
    public async Task Auth_and_admin_role_enforced()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _f.Anon().GetAsync("/api/wallet")).StatusCode);

        var bravo = await _f.AuthedAs(Bravo);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await bravo.GetAsync("/api/admin/trades")).StatusCode);

        var admin = await _f.AuthedAs(Charlie);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync("/api/admin/trades")).StatusCode);
    }

    // 동시성: 단일 재고(수량1)에 다수 동시 매수 → 정확히 1건만 체결(dupe/이중판매 0)
    [Fact]
    public async Task Concurrent_buys_against_single_unit_never_double_sell()
    {
        var admin = await _f.AuthedAs(Charlie);
        var seller = await _f.AuthedAs(Alpha);
        var buyer = await _f.AuthedAs(Bravo);

        // 깨끗한 94번에 판매자 재고 1개 지급
        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Alpha, 94, 1), Json)).EnsureSuccessStatusCode();

        var sell = await Api<PlaceOrderResult>(await seller.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Sell, 94, 5, 1), Json));
        Assert.True(sell.Success);

        // 같은 매도에 8건 동시 매수
        var buys = Enumerable.Range(0, 8).Select(_ =>
            buyer.PostAsJsonAsync("/api/orders",
                new PlaceOrderRequest(OrderSide.Buy, 94, 5, 1), Json));
        await Task.WhenAll(buys);

        var trades = await Api<PagedResult<TradeDto>>(
            await buyer.GetAsync("/api/market/94/trades?page=1&size=50"));
        Assert.Equal(1, trades.Data!.TotalCount);      // 체결 정확히 1건
        Assert.Equal(1, await StackQty(buyer, 94));    // 구매자 1개만 수령
    }

    // L7: fee_bps는 설정 경로가 없어 읽는 지점(GetFeeBpsAsync)이 유일 방어 — [0,10000]으로 클램프한다.
    // 음수(음수 수수료=돈 발행)·10000 초과(체결액 초과 수수료)를 차단한다. 전역 설정이라 finally로 원복.
    [Fact]
    public async Task Fee_bps_is_clamped_to_valid_range()
    {
        var repo = _f.Services.GetRequiredService<MarketRepository>();
        await using var db = new NpgsqlConnection(_f.ConnString);
        await db.OpenAsync();
        try
        {
            await db.ExecuteAsync("UPDATE market_config SET value = '-100' WHERE key = 'fee_bps'");
            Assert.Equal(0, await repo.GetFeeBpsAsync());

            await db.ExecuteAsync("UPDATE market_config SET value = '20000' WHERE key = 'fee_bps'");
            Assert.Equal(10000, await repo.GetFeeBpsAsync());

            await db.ExecuteAsync("UPDATE market_config SET value = '250' WHERE key = 'fee_bps'");
            Assert.Equal(250, await repo.GetFeeBpsAsync());
        }
        finally
        {
            await db.ExecuteAsync("UPDATE market_config SET value = '500' WHERE key = 'fee_bps'"); // 시드값 원복
        }
    }

    // A-2: 어드민 아이템 지급이 item_ledger에 ADMIN_GRANT로 기록된다(지갑 AdminAdjust와 대칭).
    [Fact]
    public async Task Admin_item_grant_is_recorded_in_item_ledger()
    {
        var admin = await _f.AuthedAs(Charlie);
        var player = await _f.AuthedAs(Bravo);

        // 스택 지급 → ADMIN_GRANT +qty.
        (await admin.PostAsJsonAsync("/api/admin/grant/stack", new AdminGrantStackRequest(Bravo, 5, 7), Json))
            .EnsureSuccessStatusCode();
        var afterStack = await Api<PagedResult<ItemLedgerEntryDto>>(
            await player.GetAsync("/api/inventory/ledger?page=1&size=200"));
        Assert.Contains(afterStack.Data!.Items,
            l => l.Reason == ItemLedgerReason.AdminGrant && l.TemplateId == 5 && l.DeltaQty == 7);

        // 유니크 지급 → ADMIN_GRANT +1 (인스턴스 id 일치).
        var g = await Api<ItemInstanceDto>(await admin.PostAsJsonAsync(
            "/api/admin/grant/instance", new AdminGrantInstanceRequest(Bravo, 74, 300, null), Json));
        Assert.True(g.Success);
        var afterInst = await Api<PagedResult<ItemLedgerEntryDto>>(
            await player.GetAsync("/api/inventory/ledger?page=1&size=200"));
        Assert.Contains(afterInst.Data!.Items,
            l => l.Reason == ItemLedgerReason.AdminGrant && l.InstanceId == g.Data!.Id && l.DeltaQty == 1);
    }

    // fun#2: 마켓 시세 배치(tickers)가 최우선 호가·최근 체결·활성 주문 수를 종목별로 반영한다.
    [Fact]
    public async Task Tickers_reflect_best_quotes_last_trade_and_open_orders()
    {
        var admin = await _f.AuthedAs(Charlie);
        var seller = await _f.AuthedAs(Bravo);
        var buyer = await _f.AuthedAs(Alpha);
        const int tid = 5; // 복숭아 통조림(FOOD, stackable) — 통합 테스트에서 호가가 안 쓰이는 종목

        // 매도 3@50(잔존) + 매수 40@2(미체결 잔존) + 매수 50@1(매도와 체결 → 최근 체결가 50).
        (await admin.PostAsJsonAsync("/api/admin/grant/stack", new AdminGrantStackRequest(Bravo, tid, 3), Json))
            .EnsureSuccessStatusCode();
        (await seller.PostAsJsonAsync("/api/orders", new PlaceOrderRequest(OrderSide.Sell, tid, 50, 3), Json))
            .EnsureSuccessStatusCode();
        (await buyer.PostAsJsonAsync("/api/orders", new PlaceOrderRequest(OrderSide.Buy, tid, 40, 2), Json))
            .EnsureSuccessStatusCode();
        (await buyer.PostAsJsonAsync("/api/orders", new PlaceOrderRequest(OrderSide.Buy, tid, 50, 1), Json))
            .EnsureSuccessStatusCode();

        var tickers = await Api<IReadOnlyList<MarketTickerDto>>(await buyer.GetAsync("/api/market/tickers"));
        Assert.True(tickers.Success);
        Assert.Equal(149, tickers.Data!.Count); // 전 종목 반환

        var t = tickers.Data.Single(x => x.TemplateId == tid);
        Assert.Equal(40L, t.BestBid);        // 미체결 매수 40 잔존
        Assert.Equal(50L, t.BestAsk);        // 매도 50 잔존(3-1=2)
        Assert.Equal(50L, t.LastPrice);      // 방금 체결가
        Assert.NotNull(t.LastTradeAt);
        Assert.True(t.OpenOrders >= 2);      // 매도 잔여 + 매수40 잔여

        // 활동 없는 종목은 실호가 전부 null·0("시장 없음")이지만, 벤더 참고가는 base_value 스프레드로 항상 존재.
        var dead = tickers.Data.First(x => x.OpenOrders == 0 && x.LastTradeAt == null);
        Assert.Null(dead.BestBid);
        Assert.Null(dead.BestAsk);
        Assert.Null(dead.LastPrice);
        Assert.True(dead.VendorBid > 0 && dead.VendorAsk > dead.VendorBid); // 벤더 스프레드(참고가)
    }

    // fun#8: 리더보드 — 최다 캡은 잔액 내림차순, 최다 탈출은 EXTRACTED 세션 수. 공유 상태라 절대 순위
    // 대신 구조를 검증한다(순자산 내림차순 + 잔액·아이템을 크게 준 플레이어가 1위, 순자산 > 잔액).
    [Fact]
    public async Task Leaderboard_ranks_top_net_worth_descending()
    {
        var admin = await _f.AuthedAs(Charlie);
        var bravo = await _f.AuthedAs(Bravo);

        // Bravo 잔액을 압도적으로 올린다 → 순자산 1위.
        (await admin.PostAsJsonAsync("/api/admin/wallet/adjust",
            new AdminAdjustWalletRequest(Bravo, 9_000_000, "leaderboard test"), Json)).EnsureSuccessStatusCode();
        var balance = await Balance(bravo);
        // 가치 있는 스택도 지급 → 순자산이 잔액보다 커진다(아이템 가치 포함 검증).
        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, 95, 50), Json)).EnsureSuccessStatusCode();

        var lb = await Api<LeaderboardDto>(await admin.GetAsync("/api/leaderboard"));
        Assert.True(lb.Success);
        Assert.NotEmpty(lb.Data!.TopNetWorth);

        // 순자산 내림차순 정렬(인접 비교).
        var nw = lb.Data.TopNetWorth;
        for (var i = 1; i < nw.Count; i++)
            Assert.True(nw[i - 1].Value >= nw[i].Value, "TopNetWorth not sorted descending");

        // Bravo가 1위이고, 순자산은 지갑 잔액 + 보유 아이템 가치라 잔액보다 크다.
        Assert.Equal(Bravo, nw[0].PlayerId);
        Assert.NotEmpty(nw[0].DisplayName);
        Assert.True(nw[0].Value > balance, "net worth should exceed wallet balance (includes item value)");

        // 탈출 순위도 내림차순(값이 있으면).
        var ex = lb.Data.TopExtractions;
        for (var i = 1; i < ex.Count; i++)
            Assert.True(ex[i - 1].Value >= ex[i].Value, "TopExtractions not sorted descending");
    }
}

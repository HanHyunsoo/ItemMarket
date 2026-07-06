using System.Net;
using System.Net.Http.Json;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
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
}

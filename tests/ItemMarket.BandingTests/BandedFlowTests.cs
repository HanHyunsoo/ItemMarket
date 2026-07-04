using System.Net.Http.Json;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Wallet;
using Xunit;
using static ItemMarket.BandingTests.BandedMarketFixture;

namespace ItemMarket.BandingTests;

/// <summary>
/// 가격 밴드 샤딩 ON(Market:PriceBandSize=10) 엔드투엔드 통합테스트. 실제 API+Orleans+Postgres.
/// 밴드 = unitPrice / 10. 각 테스트는 서로 다른 (깨끗한) 스택형 템플릿을 써서 호가창을 격리한다.
///
/// 핵심 확인:
///  1) 같은 밴드 안에서는 정상 매칭된다.
///  2) 밴드를 넘어서는 매칭되지 않는다 — 낮은 밴드의 싼 매도를 높은 밴드의 매수가 잡지 못한다
///     (완화된 시맨틱: 밴드-격리 매칭. 병렬성을 위해 가격 개선 교차를 포기한 의도된 트레이드오프).
///  3) 코디네이터 스냅샷이 여러 밴드를 올바르게 병합한다(bids 내림차순 / asks 오름차순).
///  4) 밴딩 하에서도 병뚜껑/아이템 보존 불변식이 유지된다.
/// </summary>
[Collection("banded")]
public class BandedFlowTests(BandedMarketFixture f)
{
    private readonly BandedMarketFixture _f = f;

    private static async Task<int> StackQty(HttpClient c, int templateId)
    {
        var inv = (await Api<InventoryDto>(await c.GetAsync("/api/inventory"))).Data!;
        return inv.Stacks.FirstOrDefault(s => s.TemplateId == templateId)?.Quantity ?? 0;
    }

    private async Task Grant(HttpClient admin, Guid to, int templateId, int qty)
        => (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(to, templateId, qty), Json)).EnsureSuccessStatusCode();

    // 1) 같은 밴드 안에서는 매칭된다. 매도 @12(밴드1) ← 매수 @15(밴드1) → 메이커가 @12 체결.
    [Fact]
    public async Task Intra_band_orders_match()
    {
        const int t = 40; // 깨끗한 스택형(수술 키트)
        var admin = await _f.AuthedAs(Charlie);
        var seller = await _f.AuthedAs(Alpha);
        var buyer = await _f.AuthedAs(Bravo);
        await Grant(admin, Alpha, t, 10);

        var sell = await Api<PlaceOrderResult>(await seller.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Sell, t, 12, 5), Json)); // 밴드 1
        Assert.True(sell.Success);
        Assert.Empty(sell.Data!.Fills);

        var buy = await Api<PlaceOrderResult>(await buyer.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Buy, t, 15, 5), Json)); // 밴드 1
        Assert.True(buy.Success);

        var trade = Assert.Single(buy.Data!.Fills);
        Assert.Equal(5, trade.Quantity);
        Assert.Equal(12, trade.UnitPrice); // 메이커(매도) 가격에 체결
        Assert.Equal(OrderStatus.Filled, buy.Data!.Order.Status);
        Assert.Equal(5, await StackQty(buyer, t));
    }

    // 2) 밴드를 넘어서는 매칭되지 않는다(완화된 시맨틱). 낮은 밴드의 싼 매도를 높은 밴드의 매수가
    //    잡지 못함을 확정한다. 이어서 같은 밴드 매수는 매칭됨을 보여 "경계 때문"임을 증명한다.
    [Fact]
    public async Task Buy_above_lower_band_ask_does_not_cross()
    {
        const int t = 41; // 깨끗한 스택형(모르핀)
        var admin = await _f.AuthedAs(Charlie);
        var seller = await _f.AuthedAs(Alpha);
        var buyer = await _f.AuthedAs(Bravo);
        await Grant(admin, Alpha, t, 10);

        // 매도 @8 → 밴드 0.
        var sell = await Api<PlaceOrderResult>(await seller.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Sell, t, 8, 5), Json));
        Assert.True(sell.Success);
        Assert.Empty(sell.Data!.Fills);

        // 매수 @15 → 밴드 1. 전역 가격-시간 우선이라면 15 >= 8 이므로 체결되겠지만,
        // 밴드-격리이므로 체결되지 않고 밴드 1에 잔존해야 한다.
        var crossBuy = await Api<PlaceOrderResult>(await buyer.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Buy, t, 15, 5), Json));
        Assert.True(crossBuy.Success);
        Assert.Empty(crossBuy.Data!.Fills);                          // 밴드 넘어 매칭 없음
        Assert.Equal(OrderStatus.Open, crossBuy.Data!.Order.Status);
        Assert.Equal(5, crossBuy.Data!.Order.RemainingQuantity);

        // 낮은 밴드의 매도 @8은 여전히 호가창에 남아 있어야 한다(코디네이터 병합 스냅샷으로 확인).
        var book1 = await Api<OrderBookSnapshotDto>(await buyer.GetAsync($"/api/market/{t}/book"));
        var ask8 = Assert.Single(book1.Data!.Asks, a => a.UnitPrice == 8);
        Assert.Equal(5, ask8.Quantity);

        // 같은 밴드(0)의 매수 @9는 매도 @8과 정상 체결된다 → 막힌 원인은 밴드 경계뿐임을 증명.
        var sameBandBuy = await Api<PlaceOrderResult>(await buyer.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Buy, t, 9, 3), Json)); // 밴드 0
        Assert.True(sameBandBuy.Success);
        var trade = Assert.Single(sameBandBuy.Data!.Fills);
        Assert.Equal(3, trade.Quantity);
        Assert.Equal(8, trade.UnitPrice);
    }

    // 3) 코디네이터 스냅샷이 여러 밴드를 병합한다. 3개 밴드에 매도/매수를 흩뿌리고 GET book 확인.
    [Fact]
    public async Task Snapshot_merges_across_bands()
    {
        const int t = 42; // 깨끗한 스택형(아드레날린)
        var admin = await _f.AuthedAs(Charlie);
        var seller = await _f.AuthedAs(Alpha);
        var buyer = await _f.AuthedAs(Bravo);
        await Grant(admin, Alpha, t, 100);

        // 매도(ask)를 밴드 0/1/2에 배치(서로 교차하지 않게 매수는 아직 없음).
        foreach (var price in new[] { 8, 12, 25 }) // 밴드 0, 1, 2
        {
            (await seller.PostAsJsonAsync("/api/orders",
                new PlaceOrderRequest(OrderSide.Sell, t, price, 5), Json)).EnsureSuccessStatusCode();
        }

        // 매수(bid)를 각 밴드의 매도보다 낮게 배치 → 매칭 없이 잔존.
        foreach (var price in new[] { 5, 11, 20 }) // 밴드 0, 1, 2
        {
            var b = await Api<PlaceOrderResult>(await buyer.PostAsJsonAsync("/api/orders",
                new PlaceOrderRequest(OrderSide.Buy, t, price, 2), Json));
            Assert.True(b.Success);
            Assert.Empty(b.Data!.Fills);
        }

        var book = (await Api<OrderBookSnapshotDto>(await buyer.GetAsync($"/api/market/{t}/book"))).Data!;

        // asks 오름차순으로 세 밴드 모두 병합.
        Assert.Equal(new long[] { 8, 12, 25 }, book.Asks.Select(a => a.UnitPrice).ToArray());
        Assert.All(book.Asks, a => Assert.Equal(5, a.Quantity));

        // bids 내림차순으로 세 밴드 모두 병합.
        Assert.Equal(new long[] { 20, 11, 5 }, book.Bids.Select(b => b.UnitPrice).ToArray());
        Assert.All(book.Bids, b => Assert.Equal(2, b.Quantity));
    }

    // 4) 밴딩 하에서도 병뚜껑/아이템 보존 + 주문 상태 정합이 유지된다(밴드 간 매칭·정산 후 SQL 검증).
    [Fact]
    public async Task Conservation_holds_under_banding()
    {
        const int t = 43; // 깨끗한 스택형(소독 티슈)
        var admin = await _f.AuthedAs(Charlie);
        var seller = await _f.AuthedAs(Alpha);
        var buyer = await _f.AuthedAs(Bravo);
        await Grant(admin, Alpha, t, 100);

        // 보존 기준선(grant 이후). 병뚜껑: 지갑+에스크로+소각수수료 총합은 거래로 변하지 않는다.
        const string capsSql =
            "SELECT (SELECT COALESCE(SUM(balance),0) FROM wallet) " +
            "+ (SELECT COALESCE(SUM(escrow_caps),0) FROM market_order) " +
            "+ (SELECT COALESCE(SUM(fee_amount),0) FROM trade)";
        var caps0 = await _f.ScalarAsync(capsSql);
        var items0 = await _f.ScalarAsync(ItemSql(t));

        // 여러 밴드에 걸쳐 매도/매수(일부는 체결, 일부는 밴드 경계로 잔존).
        (await seller.PostAsJsonAsync("/api/orders",
            new PlaceOrderRequest(OrderSide.Sell, t, 12, 6), Json)).EnsureSuccessStatusCode(); // 밴드 1
        (await seller.PostAsJsonAsync("/api/orders",
            new PlaceOrderRequest(OrderSide.Sell, t, 24, 4), Json)).EnsureSuccessStatusCode(); // 밴드 2

        (await buyer.PostAsJsonAsync("/api/orders",
            new PlaceOrderRequest(OrderSide.Buy, t, 15, 6), Json)).EnsureSuccessStatusCode();   // 밴드 1 → 체결
        (await buyer.PostAsJsonAsync("/api/orders",
            new PlaceOrderRequest(OrderSide.Buy, t, 25, 3), Json)).EnsureSuccessStatusCode();   // 밴드 2 → 부분 체결
        (await buyer.PostAsJsonAsync("/api/orders",
            new PlaceOrderRequest(OrderSide.Buy, t, 35, 2), Json)).EnsureSuccessStatusCode();   // 밴드 3 → 잔존(교차 없음)

        // 보존 확인.
        var caps1 = await _f.ScalarAsync(capsSql);
        var items1 = await _f.ScalarAsync(ItemSql(t));
        Assert.Equal(caps0, caps1);   // 병뚜껑 보존(1캡도 발행/유실 없음)
        Assert.Equal(items0, items1); // 아이템 보존(복제/증발 없음)

        // 주문 상태 정합(있어선 안 되는 조합이 0).
        Assert.Equal(0, await _f.ScalarAsync(
            $"SELECT count(*) FROM market_order WHERE template_id = {t} AND (" +
            "remaining_quantity > quantity " +
            "OR (status = 'OPEN' AND remaining_quantity = 0) " +
            "OR (status = 'FILLED' AND remaining_quantity <> 0) " +
            "OR (status = 'CANCELLED' AND escrow_caps <> 0))"));
    }

    private static string ItemSql(int t) =>
        $"SELECT (SELECT COALESCE(SUM(quantity),0) FROM inventory_stack WHERE template_id = {t}) + " +
        $"(SELECT COALESCE(SUM(remaining_quantity),0) FROM market_order " +
        $" WHERE template_id = {t} AND side = 'SELL' AND status IN ('OPEN','PARTIALLY_FILLED'))";
}

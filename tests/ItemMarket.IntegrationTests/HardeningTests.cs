using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Trades;
using ItemMarket.Contracts.Wallet;
using Xunit;
using static ItemMarket.IntegrationTests.MarketAppFixture;
using ErrorCode = ItemMarket.Contracts.Common.ErrorCode; // Orleans.ErrorCode와 모호성 방지

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 백엔드 하드닝(quality bundle) 통합테스트: 취소↔정산 락 순서 일관성(A1) +
/// 주문 등록 멱등성(A2). 각 테스트는 아직 쓰지 않은 템플릿으로 호가창을 격리한다.
/// </summary>
[Collection("market")]
public class HardeningTests(MarketAppFixture f)
{
    private readonly MarketAppFixture _f = f;

    private static async Task<long> Balance(HttpClient c)
        => (await Api<WalletDto>(await c.GetAsync("/api/wallet"))).Data!.Balance;

    private static async Task<int> StackQty(HttpClient c, int templateId)
    {
        var inv = (await Api<InventoryDto>(await c.GetAsync("/api/inventory"))).Data!;
        return inv.Stacks.FirstOrDefault(s => s.TemplateId == templateId)?.Quantity ?? 0;
    }

    // 멱등 키를 붙여(또는 없이) 주문을 POST한다.
    private static Task<HttpResponseMessage> PostOrder(HttpClient c, PlaceOrderRequest req, string? idemKey = null)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(req, options: Json)
        };
        if (idemKey is not null) msg.Headers.Add("Idempotency-Key", idemKey);
        return c.SendAsync(msg);
    }

    // ==================================================================== A1

    // 재정렬된 취소 경로가 여전히 에스크로를 정확히 환불한다(기본 동작 보존).
    [Fact]
    public async Task Reordered_cancel_still_refunds_escrow()
    {
        var buyer = await _f.AuthedAs(Alpha);
        var before = await Balance(buyer);

        // 매도자 없는 템플릿 5에 매수 → 에스크로 잠김.
        var buy = await Api<PlaceOrderResult>(await PostOrder(buyer,
            new PlaceOrderRequest(OrderSide.Buy, 5, 40, 3)));
        Assert.True(buy.Success);
        Assert.Equal(before - 120, await Balance(buyer)); // 40×3 잠김

        var del = await buyer.DeleteAsync($"/api/orders/{buy.Data!.Order.Id}");
        del.EnsureSuccessStatusCode();
        Assert.Equal(before, await Balance(buyer)); // 전액 환불
    }

    // 취소(템플릿 t1)와 정산(템플릿 t2)을 동시에 몰아쳐 데드락(40P01) 없이
    // 불변식이 유지됨을 확인한다. 두 경로 모두 같은 매수자 지갑을 건드리므로
    // 과거 반대 락 순서(order→wallet)라면 교착할 수 있었다.
    [Fact]
    public async Task Concurrent_cancels_and_settles_hold_invariants_no_deadlock_500()
    {
        var admin = await _f.AuthedAs(Charlie);
        var buyer = await _f.AuthedAs(Alpha);
        var seller = await _f.AuthedAs(Bravo);

        const int t1 = 6;   // 취소 경로용(매도자 없음 → 매수는 잔존 후 취소)
        const int t2 = 7;   // 정산 경로용(매도자 잔존 → 매수 즉시 체결)
        const int rounds = 15;

        // 매도자에게 t2 재고 지급 + 잔존 매도 rounds건.
        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, t2, rounds), Json)).EnsureSuccessStatusCode();
        for (var i = 0; i < rounds; i++)
        {
            (await PostOrder(seller, new PlaceOrderRequest(OrderSide.Sell, t2, 4, 1)))
                .EnsureSuccessStatusCode();
        }

        var b0 = await Balance(buyer);
        var statuses = new ConcurrentBag<HttpStatusCode>();

        // 동시에: t1에 매수→즉시 취소(취소 경로) + t2에 매수(정산 경로).
        var tasks = new List<Task>();
        for (var i = 0; i < rounds; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var r = await PostOrder(buyer, new PlaceOrderRequest(OrderSide.Buy, t1, 3, 1));
                statuses.Add(r.StatusCode);
                var placed = await Api<PlaceOrderResult>(r);
                if (placed.Success)
                {
                    var del = await buyer.DeleteAsync($"/api/orders/{placed.Data!.Order.Id}");
                    statuses.Add(del.StatusCode);
                }
            }));
            tasks.Add(Task.Run(async () =>
            {
                var r = await PostOrder(buyer, new PlaceOrderRequest(OrderSide.Buy, t2, 4, 1));
                statuses.Add(r.StatusCode);
            }));
        }
        await Task.WhenAll(tasks);

        // 데드락은 처리되지 않은 예외 → 500으로 샌다. 하나도 없어야 한다.
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, statuses);

        // 보존식: 매수자 순지출 = t2 체결 총액. t1 매수는 전부 취소되어 순효과 0.
        var t2Trades = (await Api<PagedResult<TradeDto>>(
            await buyer.GetAsync($"/api/market/{t2}/trades?page=1&size=100"))).Data!;
        var spend = t2Trades.Items.Where(t => t.BuyerId == Alpha).Sum(t => (long)t.UnitPrice * t.Quantity);
        Assert.Equal(spend, b0 - await Balance(buyer));
        Assert.Equal(rounds, await StackQty(buyer, t2)); // 모든 t2 매수 체결 → 15개 수령
    }

    // ==================================================================== A2

    // 같은 Idempotency-Key로 두 번 → 주문 1건, DB 행 1개, 동일 응답.
    [Fact]
    public async Task Same_idempotency_key_places_exactly_one_order()
    {
        var buyer = await _f.AuthedAs(Alpha);
        var key = $"idem-{Guid.NewGuid()}";
        const int template = 8; // 매도자 없음 → 잔존

        var first = await Api<PlaceOrderResult>(await PostOrder(buyer,
            new PlaceOrderRequest(OrderSide.Buy, template, 10, 1), key));
        var second = await Api<PlaceOrderResult>(await PostOrder(buyer,
            new PlaceOrderRequest(OrderSide.Buy, template, 10, 1), key));

        Assert.True(first.Success);
        Assert.True(second.Success);
        // 동일 주문 id → 재실행되지 않고 저장된 응답을 반환.
        Assert.Equal(first.Data!.Order.Id, second.Data!.Order.Id);

        // 이 템플릿의 내 주문은 정확히 1건.
        var mine = (await Api<List<OrderDto>>(await buyer.GetAsync("/api/orders"))).Data!;
        Assert.Single(mine, o => o.ItemTemplateId == template);
    }

    // 서로 다른 키 → 서로 다른 주문 2건.
    [Fact]
    public async Task Different_idempotency_keys_place_two_orders()
    {
        var buyer = await _f.AuthedAs(Alpha);
        const int template = 9;

        var a = await Api<PlaceOrderResult>(await PostOrder(buyer,
            new PlaceOrderRequest(OrderSide.Buy, template, 10, 1), $"idem-{Guid.NewGuid()}"));
        var b = await Api<PlaceOrderResult>(await PostOrder(buyer,
            new PlaceOrderRequest(OrderSide.Buy, template, 10, 1), $"idem-{Guid.NewGuid()}"));

        Assert.True(a.Success && b.Success);
        Assert.NotEqual(a.Data!.Order.Id, b.Data!.Order.Id);

        var mine = (await Api<List<OrderDto>>(await buyer.GetAsync("/api/orders"))).Data!;
        Assert.Equal(2, mine.Count(o => o.ItemTemplateId == template));
    }

    // 실패한 원본은 슬롯을 비워 같은 키로 재시도가 가능해야 한다.
    [Fact]
    public async Task Failed_original_releases_key_for_retry()
    {
        var buyer = await _f.AuthedAs(Alpha);
        var key = $"idem-{Guid.NewGuid()}";
        const int template = 10;

        // 1) 잔액 초과 매수 → 실패(InsufficientFunds). 슬롯은 해제되어야 한다.
        var fail = await Api<PlaceOrderResult>(await PostOrder(buyer,
            new PlaceOrderRequest(OrderSide.Buy, template, 1000, 1000000), key)); // 10억 CAP
        Assert.False(fail.Success);
        Assert.Equal(ErrorCode.InsufficientFunds, fail.Error!.Code);

        // 2) 같은 키로 유효한 주문 재시도 → 이번엔 성공(슬롯이 비워졌으므로).
        var ok = await Api<PlaceOrderResult>(await PostOrder(buyer,
            new PlaceOrderRequest(OrderSide.Buy, template, 5, 1), key));
        Assert.True(ok.Success);
    }
}

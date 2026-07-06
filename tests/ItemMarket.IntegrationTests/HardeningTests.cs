using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Trades;
using ItemMarket.Contracts.Wallet;
using ItemMarket.Grains.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ItemMarket.IntegrationTests.MarketAppFixture;

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 백엔드 하드닝(quality bundle) 통합테스트: 취소↔정산 락 순서 일관성(A1).
/// 주문 등록 멱등성(A2)은 Redis로 이관되어 IdempotencyRedisTests로 분리했다.
/// 각 테스트는 아직 쓰지 않은 템플릿으로 호가창을 격리한다.
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

    // ==================================================================== M2

    // 멱등성(M2): Redis 미구성(Null 저장소=이 fixture)에서 Idempotency-Key가 오면 조용히 무시하지
    // 않고 503 IdempotencyUnavailable로 명시적으로 거부한다. 헤더 없는 일반 주문은 영향받지 않는다.
    [Fact]
    public async Task Idempotency_key_is_rejected_when_store_is_not_durable()
    {
        var buyer = await _f.AuthedAs(Alpha);

        // 헤더 있음 → 503 거부.
        var withKey = await PostOrder(buyer, new PlaceOrderRequest(OrderSide.Buy, 8, 10, 1), idemKey: "demo-key-1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, withKey.StatusCode);
        Assert.Equal(ItemMarket.Contracts.Common.ErrorCode.IdempotencyUnavailable, (await Api<PlaceOrderResult>(withKey)).Error!.Code);

        // 헤더 없음 → 평소대로 등록(멱등 경로를 타지 않음).
        var noKey = await PostOrder(buyer, new PlaceOrderRequest(OrderSide.Buy, 8, 10, 1));
        Assert.True((await Api<PlaceOrderResult>(noKey)).Success);
    }

    // L9a: OrderExistsAsync는 커밋-후-예외 창에서 보상 전 멱등 재조정에 쓰인다 —
    // 영속된 주문은 true, 존재하지 않는 id는 false. (이중환불 방지 가드의 판별 근거)
    [Fact]
    public async Task Order_exists_probe_reflects_persistence()
    {
        var repo = _f.Services.GetRequiredService<MarketRepository>();
        var buyer = await _f.AuthedAs(Alpha);

        // 매도자 없는 템플릿 → 매수 잔존(주문 영속).
        var placed = await Api<PlaceOrderResult>(await PostOrder(buyer, new PlaceOrderRequest(OrderSide.Buy, 9, 30, 1)));
        Assert.True(placed.Success);

        Assert.True(await repo.OrderExistsAsync(placed.Data!.Order.Id));
        Assert.False(await repo.OrderExistsAsync(Guid.NewGuid()));
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

    // A2(주문 등록 멱등성)는 Redis로 이관되어 IdempotencyRedisTests(redis-market 컬렉션)로 옮겼다.
}

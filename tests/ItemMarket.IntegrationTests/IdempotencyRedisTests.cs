using System.Net;
using System.Net.Http.Json;
using ItemMarket.Api.Infrastructure;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Orders;
using StackExchange.Redis;
using Xunit;
using static ItemMarket.IntegrationTests.RedisMarketAppFixture;
using ErrorCode = ItemMarket.Contracts.Common.ErrorCode; // Orleans.ErrorCode와 모호성 방지

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 주문 등록 멱등성(A2) 통합테스트 — Redis 백엔드(RedisIdempotencyStore).
/// 같은 키 재시도 → 주문 1건 + 동일 응답, 다른 키 → 2건, 처리중 원본 → 409.
/// 전용 픽스처(Postgres + Redis)에서 실행해 무관한 테스트를 빠르게 유지한다.
/// </summary>
[Collection("redis-market")]
public class IdempotencyRedisTests(RedisMarketAppFixture f)
{
    private readonly RedisMarketAppFixture _f = f;

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

    // 같은 Idempotency-Key로 두 번 → 주문 1건, 동일 응답.
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

    // 실패한 원본은 슬롯을 비워(DEL) 같은 키로 재시도가 가능해야 한다.
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

    // 원본이 아직 처리중(INFLIGHT 마커만 존재)인 슬롯으로 재요청 → 409 IdempotencyInProgress.
    // 실제 인플라이트 경합은 비결정적이라, Redis에 마커를 직접 심어 결정적으로 재현한다.
    [Fact]
    public async Task In_progress_original_returns_409()
    {
        var buyer = await _f.AuthedAs(Alpha);
        var key = $"idem-{Guid.NewGuid()}";
        const int template = 11;

        // 원본이 슬롯을 청구하고 아직 처리중인 상태를 모사(INFLIGHT 마커).
        await using var redis = await ConnectionMultiplexer.ConnectAsync(_f.RedisConnectionString);
        await redis.GetDatabase().StringSetAsync(
            RedisIdempotencyStore.RedisKeyFor(Alpha, key), RedisIdempotencyStore.InflightMarker);

        var res = await PostOrder(buyer, new PlaceOrderRequest(OrderSide.Buy, template, 10, 1), key);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await Api<PlaceOrderResult>(res);
        Assert.False(body.Success);
        Assert.Equal(ErrorCode.IdempotencyInProgress, body.Error!.Code);

        // 처리중 응답은 원본을 재실행하지 않으므로 주문이 생성되지 않아야 한다.
        var mine = (await Api<List<OrderDto>>(await buyer.GetAsync("/api/orders"))).Data!;
        Assert.DoesNotContain(mine, o => o.ItemTemplateId == template);
    }
}

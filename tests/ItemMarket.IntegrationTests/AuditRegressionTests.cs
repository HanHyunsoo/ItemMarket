using System.Net;
using System.Net.Http.Json;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Wallet;
using Xunit;
using static ItemMarket.IntegrationTests.MarketAppFixture;
using ErrorCode = ItemMarket.Contracts.Common.ErrorCode; // Orleans.ErrorCode와 모호성 방지

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 백엔드 감사(audit)에서 수정한 버그들의 회귀 테스트.
/// 각 테스트는 아직 쓰지 않은 템플릿 ID를 사용해 호가창을 격리한다.
/// </summary>
[Collection("market")]
public class AuditRegressionTests(MarketAppFixture f)
{
    private readonly MarketAppFixture _f = f;

    private static async Task<long> Balance(HttpClient c)
        => (await Api<WalletDto>(await c.GetAsync("/api/wallet"))).Data!.Balance;

    private static async Task<int> StackQty(HttpClient c, int templateId)
    {
        var inv = (await Api<InventoryDto>(await c.GetAsync("/api/inventory"))).Data!;
        return inv.Stacks.FirstOrDefault(s => s.TemplateId == templateId)?.Quantity ?? 0;
    }

    // [Critical] UnitPrice×Quantity가 long을 오버플로하면 음수 에스크로가 되어
    // 지갑에 병뚜껑이 생성(free caps)되던 취약점 → 상한 검증으로 거부.
    [Fact]
    public async Task Overflowing_price_times_quantity_is_rejected_and_wallet_untouched()
    {
        var buyer = await _f.AuthedAs(Alpha);
        var before = await Balance(buyer);

        // long.MaxValue/2 × 4 → 기존 코드에서는 음수로 래핑되어 에스크로 성공(잔액 증가)
        var res = await buyer.PostAsJsonAsync("/api/orders",
            new PlaceOrderRequest(OrderSide.Buy, 2, long.MaxValue / 2, 4), Json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await Api<PlaceOrderResult>(res);
        Assert.False(body.Success);
        Assert.Equal(ErrorCode.ValidationError, body.Error!.Code);

        // 음수 수량도 거부
        var neg = await buyer.PostAsJsonAsync("/api/orders",
            new PlaceOrderRequest(OrderSide.Buy, 2, 10, -5), Json);
        Assert.Equal(HttpStatusCode.BadRequest, neg.StatusCode);

        Assert.Equal(before, await Balance(buyer)); // 지갑 무변동
    }

    // [High] 자기 주문과의 체결(자전거래) 금지: 본인 매도와 교차하는 본인 매수는
    // 체결 없이 호가창에 잔존해야 한다.
    [Fact]
    public async Task Self_trade_is_skipped_not_matched()
    {
        var admin = await _f.AuthedAs(Charlie);
        var bravo = await _f.AuthedAs(Bravo);

        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, 96, 50), Json)).EnsureSuccessStatusCode();

        var sell = await Api<PlaceOrderResult>(await bravo.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Sell, 96, 7, 10), Json));
        Assert.True(sell.Success);

        var buy = await Api<PlaceOrderResult>(await bravo.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Buy, 96, 7, 10), Json));
        Assert.True(buy.Success);
        Assert.Empty(buy.Data!.Fills);                       // 자기 매도와 체결 안 됨
        Assert.Equal(OrderStatus.Open, buy.Data!.Order.Status);

        var book = await Api<OrderBookSnapshotDto>(await bravo.GetAsync("/api/market/96/book"));
        Assert.Contains(book.Data!.Asks, a => a.UnitPrice == 7 && a.Quantity == 10);
        Assert.Contains(book.Data!.Bids, b => b.UnitPrice == 7 && b.Quantity == 10);

        // 단, 다른 플레이어와는 정상 체결된다(자기 주문만 건너뜀).
        var alpha = await _f.AuthedAs(Alpha);
        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Alpha, 96, 10), Json)).EnsureSuccessStatusCode();
        var alphaSell = await Api<PlaceOrderResult>(await alpha.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Sell, 96, 7, 10), Json));
        Assert.True(alphaSell.Success);
        Assert.Equal(10, alphaSell.Data!.Fills.Sum(t => t.Quantity)); // Bravo의 매수와 체결
    }

    // [Critical 계열] 부분 체결 후 취소: 가격 개선 차익 환불 + 잔여 에스크로 환불이
    // 정확해야 한다(escrow_caps 드리프트 없음). 최종 지출 = 체결가 × 체결 수량.
    [Fact]
    public async Task Cancel_after_partial_fill_refunds_exact_remaining_escrow()
    {
        var admin = await _f.AuthedAs(Charlie);
        var seller = await _f.AuthedAs(Alpha);
        var buyer = await _f.AuthedAs(Bravo);

        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Alpha, 97, 5), Json)).EnsureSuccessStatusCode();

        var b0 = await Balance(buyer);
        var qty0 = await StackQty(buyer, 97);

        // 매도 5 @10 대기 → 매수 10 @12: 5개 @10 체결(차익 2×5 환불), 5개 잔존(에스크로 12×5=60)
        (await seller.PostAsJsonAsync("/api/orders",
            new PlaceOrderRequest(OrderSide.Sell, 97, 10, 5), Json)).EnsureSuccessStatusCode();
        var buy = await Api<PlaceOrderResult>(await buyer.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Buy, 97, 12, 10), Json));
        Assert.True(buy.Success);
        Assert.Equal(5, buy.Data!.Fills.Sum(t => t.Quantity));
        Assert.Equal(10, buy.Data!.Fills[0].UnitPrice);           // 메이커 가격에 체결
        Assert.Equal(b0 - 120 + 10, await Balance(buyer));        // 에스크로 120, 차익 10 환불

        // 잔여분 취소 → 남은 에스크로 60 전액 환불. 순지출 = 50(=10×5).
        var del = await buyer.DeleteAsync($"/api/orders/{buy.Data!.Order.Id}");
        del.EnsureSuccessStatusCode();
        Assert.Equal(b0 - 50, await Balance(buyer));
        Assert.Equal(qty0 + 5, await StackQty(buyer, 97));
    }

    // [High] 어드민 지급 검증: 음수 수량/스택형-유니크 불일치/없는 플레이어가
    // 원시 500(DB CHECK/FK 위반) 대신 도메인 오류 봉투로 반환되어야 한다.
    [Fact]
    public async Task Admin_grant_validation_returns_domain_errors_not_500()
    {
        var admin = await _f.AuthedAs(Charlie);

        var negQty = await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Alpha, 1, -10), Json);
        Assert.Equal(HttpStatusCode.BadRequest, negQty.StatusCode);
        Assert.Equal(ErrorCode.ValidationError, (await Api<InventoryDto>(negQty)).Error!.Code);

        var uniqueAsStack = await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Alpha, 53, 1), Json); // 53 = 식칼(유니크)
        Assert.Equal(HttpStatusCode.BadRequest, uniqueAsStack.StatusCode);
        Assert.Equal(ErrorCode.StackableMismatch, (await Api<InventoryDto>(uniqueAsStack)).Error!.Code);

        var stackAsInstance = await admin.PostAsJsonAsync("/api/admin/grant/instance",
            new AdminGrantInstanceRequest(Alpha, 1, 10, null), Json); // 1 = 통조림(스택형)
        Assert.Equal(HttpStatusCode.BadRequest, stackAsInstance.StatusCode);
        Assert.Equal(ErrorCode.StackableMismatch, (await Api<ItemInstanceDto>(stackAsInstance)).Error!.Code);

        var ghost = await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Guid.NewGuid(), 1, 1), Json);
        Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);
        Assert.Equal(ErrorCode.PlayerNotFound, (await Api<InventoryDto>(ghost)).Error!.Code);
    }

    // 잔액 초과 매수는 InsufficientFunds 코드로 실패(grain 경계를 넘어 코드 보존).
    [Fact]
    public async Task Buy_beyond_balance_fails_with_insufficient_funds()
    {
        var buyer = await _f.AuthedAs(Alpha);
        var before = await Balance(buyer);

        var res = await buyer.PostAsJsonAsync("/api/orders",
            new PlaceOrderRequest(OrderSide.Buy, 3, 1000, 1000000), Json); // 10억 CAP
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(ErrorCode.InsufficientFunds, (await Api<PlaceOrderResult>(res)).Error!.Code);
        Assert.Equal(before, await Balance(buyer));
    }
}

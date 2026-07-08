using System.Net.Http.Json;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Wallet;
using ItemMarket.Grains.Data;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;
using static ItemMarket.IntegrationTests.MarketAppFixture;
using ErrorCode = ItemMarket.Contracts.Common.ErrorCode; // Orleans.ErrorCode와 모호성 방지

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 크래시 복구 증명 — "인메모리 호가창은 재구성 가능한 투영이고 DB가 최종 진실"이라는 핵심 신뢰성
/// 주장을 말이 아니라 테스트로 못박는다. OrderBookGrain을 강제 비활성화(활성화 수집)해 인메모리
/// 상태를 날린 뒤, 재활성화가 DB에서 호가창을 재수화하고 매칭이 이어지는지 검증한다.
/// (그레인 활성화/비활성화가 프로덕션 코드 변경 없이 IManagementGrain으로 재현된다.)
/// </summary>
[Collection("market")]
public class CrashRecoveryTests(MarketAppFixture f)
{
    private readonly MarketAppFixture _f = f;
    // 다른 테스트가 쓰지 않는 stackable 종목(생수)으로 호가창을 격리한다.
    private const int Tid = 15;

    private static async Task<long> Balance(HttpClient c)
        => (await Api<WalletDto>(await c.GetAsync("/api/wallet"))).Data!.Balance;

    /// <summary>유휴 활성화를 전부 수집(비활성화) — 프로세스 크래시/유휴 비활성화를 근사.</summary>
    private async Task ForceDeactivateAll()
    {
        var mgmt = _f.Services.GetRequiredService<IGrainFactory>().GetGrain<IManagementGrain>(0);
        await mgmt.ForceActivationCollection(TimeSpan.Zero);
    }

    [Fact]
    public async Task OrderBook_rehydrates_from_db_after_deactivation_and_keeps_matching()
    {
        var admin = await _f.AuthedAs(Charlie);
        var seller = await _f.AuthedAs(Bravo);
        var buyer = await _f.AuthedAs(Alpha);

        // 1) 실제 대기 매도(API로 정상 에스크로): @60 ×3 → market_order에 OPEN으로 영속.
        (await admin.PostAsJsonAsync("/api/admin/grant/stack",
            new AdminGrantStackRequest(Bravo, Tid, 3), Json)).EnsureSuccessStatusCode();
        (await seller.PostAsJsonAsync("/api/orders", new PlaceOrderRequest(OrderSide.Sell, Tid, 60, 3), Json))
            .EnsureSuccessStatusCode();

        // 2) 강제 비활성화 — OrderBookGrain의 인메모리 호가창을 날린다(크래시/유휴 근사).
        await ForceDeactivateAll();

        // 3) 비활성화 상태에서 DB에만 직접 매도 주문(@999)을 삽입한다. 어떤 살아있는 그레인도
        //    본 적이 없는 행이다 — 오직 DB에서 재수화해야만 호가창에 나타난다.
        var markerId = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(_f.ConnString))
        {
            await db.OpenAsync();
            await db.ExecuteAsync(
                @"INSERT INTO market_order
                    (id, player_id, side, template_id, unit_price, quantity, remaining_quantity, status)
                  VALUES (@markerId, @pid, 'SELL', @tid, 999, 2, 2, 'OPEN')",
                new { markerId, pid = Bravo, tid = Tid });
        }

        // 4) GET book → 재활성화가 DB에서 호가창을 재수화한다. DB에만 있던 @999가 보이면 재수화 증명.
        //    (그레인이 죽지 않고 살아있었다면 @999는 인메모리에 없어 스냅샷에 안 나온다.)
        var book = await Api<OrderBookSnapshotDto>(await buyer.GetAsync($"/api/market/{Tid}/book"));
        Assert.True(book.Success);
        Assert.Contains(book.Data!.Asks, a => a.UnitPrice == 60);   // 원래 대기 매도(에스크로됨)
        Assert.Contains(book.Data!.Asks, a => a.UnitPrice == 999);  // DB-only 마커 → DB에서 재수화됨

        // 5) 재수화된 호가창에서 매칭이 그대로 이어진다: 교차 매수가 최우선(@60)에 정확히 체결.
        var bBefore = await Balance(buyer);
        var sBefore = await Balance(seller);
        var buy = await Api<PlaceOrderResult>(await buyer.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest(OrderSide.Buy, Tid, 60, 3), Json));
        Assert.True(buy.Success);
        Assert.Equal(3, buy.Data!.Fills.Sum(x => x.Quantity));      // 최우선 매도 전량 체결
        Assert.Equal(60, buy.Data.Fills[0].UnitPrice);              // 메이커가(재수화된 잔주문)로 체결

        // 6) 정합성: 비활성화·재수화를 거쳐도 돈 보존 — 매수 -180, 매도 +(180 - 5% 수수료).
        const long cost = 60L * 3;
        const long fee = cost * 500 / 10000; // 기본 fee_bps 500(5%)
        Assert.Equal(bBefore - cost, await Balance(buyer));
        Assert.Equal(sBefore + (cost - fee), await Balance(seller));
    }

    // M1: 그레인이 던진 DomainException이 실로 경계를 넘을 때 Code가 보존돼야 API가 도메인 코드→적절한
    // HTTP를 매핑한다. [GenerateSerializer] 코덱을 Orleans Serializer로 직접 라운드트립해 증명한다
    // (멀티실로 클러스터를 띄우지 않고도 직렬화 계약을 고정). 이게 없으면 다중 실로에서 500으로 강등됐다.
    [Fact]
    public void DomainException_roundtrips_through_orleans_serializer_preserving_code()
    {
        var ser = _f.Services.GetRequiredService<Serializer>();
        var original = new DomainException(ErrorCode.InsufficientFunds, "잔액 부족");

        var bytes = ser.SerializeToArray(original);
        var back = ser.Deserialize<DomainException>(bytes);

        Assert.Equal(ErrorCode.InsufficientFunds, back.Code); // 실로 경계 넘어도 도메인 코드 보존
        Assert.Equal("잔액 부족", back.Message);
    }
}

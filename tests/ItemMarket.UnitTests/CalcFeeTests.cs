using ItemMarket.Grains.Data;
using Xunit;

namespace ItemMarket.UnitTests;

/// <summary>
/// MarketRepository.CalcFee 순수 수수료 계산. fee = gross(execPrice×quantity) × bps / 10000.
/// 중간 곱은 Int128로 수행해 long 오버플로를 차단하고, 정수 나눗셈은 버림(truncate)한다.
/// </summary>
public class CalcFeeTests
{
    [Fact]
    public void Zero_price_yields_zero_fee()
        => Assert.Equal(0, MarketRepository.CalcFee(0, 5, 500));

    [Fact]
    public void Zero_bps_yields_zero_fee()
        => Assert.Equal(0, MarketRepository.CalcFee(1000, 10, 0));

    [Theory]
    [InlineData(10, 10, 500, 5)]      // gross 100 × 5% = 5 (통합테스트와 동일)
    [InlineData(100, 1, 500, 5)]      // gross 100 × 5% = 5
    [InlineData(1000, 3, 500, 150)]   // gross 3000 × 5% = 150
    public void Computes_bps_of_gross(long price, int qty, int bps, long expected)
        => Assert.Equal(expected, MarketRepository.CalcFee(price, qty, bps));

    [Theory]
    [InlineData(1, 1, 500, 0)]        // 0.05 → 버림 0
    [InlineData(19, 1, 500, 0)]       // 0.95 → 버림 0
    [InlineData(20, 1, 500, 1)]       // 1.00 → 1
    [InlineData(39, 1, 500, 1)]       // 1.95 → 버림 1
    public void Truncates_fractional_fee(long price, int qty, int bps, long expected)
        => Assert.Equal(expected, MarketRepository.CalcFee(price, qty, bps));

    [Fact]
    public void Near_max_notional_no_overflow()
    {
        // gross = MaxNotional(10^15) = execPrice 10^9 × qty 10^6. 5% → 5×10^13.
        var fee = MarketRepository.CalcFee(1_000_000_000L, 1_000_000, 500);
        Assert.Equal(50_000_000_000_000L, fee);
    }

    [Fact]
    public void Extreme_intermediate_product_does_not_overflow_long()
    {
        // gross = 10^18(execPrice 10^12 × qty 10^6). bps 10000이면 중간곱 10^22로
        // long(≈9.2×10^18)을 넘지만, Int128 계산이라 안전. 결과 = gross(=10^18).
        var fee = MarketRepository.CalcFee(1_000_000_000_000L, 1_000_000, 10000);
        Assert.Equal(1_000_000_000_000_000_000L, fee);
    }

    [Fact]
    public void Fee_never_exceeds_gross_for_valid_bps()
    {
        // bps < 10000 이면 수수료는 항상 gross 이하(설계 불변식).
        const long price = 999_999_999L;
        const int qty = 1000;
        long gross = price * qty;
        var fee = MarketRepository.CalcFee(price, qty, 9999);
        Assert.True(fee <= gross);
        Assert.Equal(gross * 9999 / 10000, fee);
    }
}

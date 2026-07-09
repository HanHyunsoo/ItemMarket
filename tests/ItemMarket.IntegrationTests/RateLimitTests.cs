using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ItemMarket.Api;
using ItemMarket.Contracts.Auth;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Orders;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;
using static ItemMarket.IntegrationTests.MarketAppFixture;
using ErrorCode = ItemMarket.Contracts.Common.ErrorCode; // Orleans.ErrorCode와 모호성 방지

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 레이트 리미팅(A3) 전용 픽스처: 낮은 한도(3건/분)로 앱을 띄워 429 거부를 결정적으로
/// 재현한다. 공유 픽스처(넉넉한 기본값)와 격리하기 위해 별도 컨테이너/호스트를 쓴다.
/// </summary>
public sealed class RateLimitAppFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("item_market")
        .WithUsername("market")
        .WithPassword("market")
        .Build();

    private WebApplicationFactory<Program> _app = default!;

    public const int Permit = 3;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        var ddl = await File.ReadAllTextAsync(RepoFile("db/ddl.sql"));
        var result = await _pg.ExecScriptAsync(ddl);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ddl.sql 적용 실패: {result.Stderr}");

        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.GetConnectionString());
            b.UseSetting("Auth:Secret", "integration-test-secret-0123456789-abcdefghij");
            b.UseSetting("Orleans:ClusteringMode", "localhost");
            b.UseSetting("Orleans:SiloPort", "11198");
            b.UseSetting("Orleans:GatewayPort", "30198");
            // 낮은 한도로 429를 결정적으로 재현.
            b.UseSetting("RateLimiting:Orders:PermitLimit", Permit.ToString());
            b.UseSetting("RateLimiting:Orders:WindowSeconds", "60");
        });

        using var warm = _app.CreateClient();
        (await warm.GetAsync("/health")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _pg.DisposeAsync();
    }

    public async Task<HttpClient> AuthedAs(Guid playerId)
    {
        using var c = _app.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(playerId), Json);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<TokenResponse>>(Json);
        var authed = _app.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Data!.AccessToken);
        return authed;
    }

    private static string RepoFile(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ItemMarket.sln")))
            d = d.Parent;
        if (d is null) throw new InvalidOperationException("repo root(ItemMarket.sln)를 찾지 못함");
        return Path.Combine(d.FullName, rel);
    }
}

public class RateLimitTests(RateLimitAppFixture f) : IClassFixture<RateLimitAppFixture>
{
    private readonly RateLimitAppFixture _f = f;

    // 한도(3건/분)를 초과하면 4번째 주문은 429 + 표준 봉투(RateLimited).
    [Fact]
    public async Task Exceeding_limit_returns_429_with_envelope()
    {
        var buyer = await _f.AuthedAs(Alpha);
        // 매도자 없는 템플릿 → 매수는 잔존(주문 자체는 성공). 한도만 소진한다.
        var req = new PlaceOrderRequest(OrderSide.Buy, 11, 1, 1);

        // 처음 Permit건은 429가 아니어야 한다.
        for (var i = 0; i < RateLimitAppFixture.Permit; i++)
        {
            var ok = await buyer.PostAsJsonAsync("/api/orders", req, Json);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, ok.StatusCode);
        }

        // 한도 초과 → 429 + ApiResponse 실패 봉투.
        var rejected = await buyer.PostAsJsonAsync("/api/orders", req, Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var body = await Api<PlaceOrderResult>(rejected);
        Assert.False(body.Success);
        Assert.Equal(ErrorCode.RateLimited, body.Error!.Code);
    }
}

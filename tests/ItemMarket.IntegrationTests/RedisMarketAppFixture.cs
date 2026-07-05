using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ItemMarket.Api;
using ItemMarket.Contracts.Auth;
using ItemMarket.Contracts.Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 멱등성(Redis) 전용 픽스처: Postgres + Redis 컨테이너를 함께 띄우고 API에
/// Redis:ConnectionString 을 주입해 RedisIdempotencyStore 경로를 활성화한다.
/// 공유 MarketAppFixture(Postgres만)와 분리해 무관한 테스트를 빠르게 유지한다.
/// </summary>
public sealed class RedisMarketAppFixture : IAsyncLifetime
{
    // 시드된 개발 플레이어(ddl.sql)
    public static readonly Guid Alpha = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Bravo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Charlie = Guid.Parse("33333333-3333-3333-3333-333333333333"); // admin

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("item_market")
        .WithUsername("market")
        .WithPassword("market")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7")
        .Build();

    private WebApplicationFactory<Program> _app = default!;

    /// <summary>테스트가 멱등 슬롯을 직접 조작할 수 있도록 노출한 Redis 연결 문자열.</summary>
    public string RedisConnectionString => _redis.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pg.StartAsync(), _redis.StartAsync());

        // 스키마 + 아이템 마스터 102종 + 시드 적용 (컨테이너 내부 psql)
        var ddl = await File.ReadAllTextAsync(RepoFile("db/ddl.sql"));
        var result = await _pg.ExecScriptAsync(ddl);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ddl.sql 적용 실패: {result.Stderr}");

        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.GetConnectionString());
            b.UseSetting("Redis:ConnectionString", _redis.GetConnectionString());
            b.UseSetting("Auth:Secret", "integration-test-secret-0123456789-abcdefghij");
            b.UseSetting("Orleans:ClusteringMode", "localhost");
            b.UseSetting("Orleans:SiloPort", "11198");
            b.UseSetting("Orleans:GatewayPort", "30198");
        });

        // 호스트 강제 기동(Orleans 실로 워밍업)
        using var warm = _app.CreateClient();
        (await warm.GetAsync("/health")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await Task.WhenAll(_pg.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    // ---- 헬퍼 -------------------------------------------------------------

    public HttpClient Anon() => _app.CreateClient();

    public async Task<string> Login(Guid playerId)
    {
        using var c = _app.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(playerId), Json);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<TokenResponse>>(Json);
        return body!.Data!.AccessToken;
    }

    public async Task<HttpClient> AuthedAs(Guid playerId)
    {
        var token = await Login(playerId);
        var c = _app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static async Task<ApiResponse<T>> Api<T>(HttpResponseMessage r)
        => (await r.Content.ReadFromJsonAsync<ApiResponse<T>>(Json))!;

    private static string RepoFile(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Solution1.sln")))
            d = d.Parent;
        if (d is null) throw new InvalidOperationException("repo root(Solution1.sln)를 찾지 못함");
        return Path.Combine(d.FullName, rel);
    }
}

[CollectionDefinition("redis-market")]
public sealed class RedisMarketCollection : ICollectionFixture<RedisMarketAppFixture>;

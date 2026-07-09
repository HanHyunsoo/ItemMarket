using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ItemMarket.Api;
using ItemMarket.Contracts.Auth;
using ItemMarket.Contracts.Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ItemMarket.BandingTests;

/// <summary>
/// 밴딩 ON 통합테스트 픽스처: 일회용 Postgres + 인프로세스 API(Orleans 실로)를
/// <c>Market:PriceBandSize=BandSize</c>로 기동한다. 즉 OrderBookGrain이 코디네이터가 되고
/// 밴드별 OrderBandGrain으로 라우팅된다.
///
/// 별도 테스트 <b>프로젝트</b>(= 별도 프로세스)인 이유: 코디네이터 리엔트런시는
/// <c>OrderBookGrain.AllowInterleaving</c> 정적 플래그로 기동 시 켜진다. 프로세스가 나뉘어야
/// 밴딩 OFF(기존 통합테스트)의 정적 상태와 섞이지 않는다. Orleans 실로 포트도 겹치지 않도록
/// 기존 통합테스트(11199/30199)와 다른 값을 쓴다.
/// </summary>
public sealed class BandedMarketFixture : IAsyncLifetime
{
    /// <summary>이 픽스처의 가격 밴드 크기. 밴드 = unitPrice / BandSize.</summary>
    public const int BandSize = 10;

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

    private WebApplicationFactory<Program> _app = default!;
    private string _conn = default!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _conn = _pg.GetConnectionString();

        var ddl = await File.ReadAllTextAsync(RepoFile("db/ddl.sql"));
        var result = await _pg.ExecScriptAsync(ddl);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ddl.sql 적용 실패: {result.Stderr}");

        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _conn);
            b.UseSetting("Auth:Secret", "banding-test-secret-0123456789-abcdefghij");
            b.UseSetting("Orleans:ClusteringMode", "localhost");
            b.UseSetting("Orleans:SiloPort", "11198");
            b.UseSetting("Orleans:GatewayPort", "30198");
            // 이 픽스처의 핵심: 가격 밴드 샤딩 켜기.
            b.UseSetting("Market:PriceBandSize", BandSize.ToString());
        });

        using var warm = _app.CreateClient();
        (await warm.GetAsync("/health")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _pg.DisposeAsync();
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

    /// <summary>불변식 검증용: 컨테이너 DB에 직접 스칼라 SQL을 실행한다.</summary>
    public async Task<long> ScalarAsync(string sql)
    {
        await using var db = new NpgsqlConnection(_conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
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

[CollectionDefinition("banded")]
public sealed class BandedCollection : ICollectionFixture<BandedMarketFixture>;

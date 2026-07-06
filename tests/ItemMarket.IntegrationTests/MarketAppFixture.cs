using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ItemMarket.Api;
using ItemMarket.Contracts.Auth;
using ItemMarket.Contracts.Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ItemMarket.IntegrationTests;

/// <summary>
/// 통합테스트 픽스처: 일회용 Postgres 컨테이너를 띄워 db/ddl.sql로 시드하고,
/// 실제 API + Orleans 실로를 인프로세스로 호스팅한다(WebApplicationFactory).
/// 목킹 없이 진짜 매칭엔진·정산·인증 경로를 통과시킨다.
/// </summary>
public sealed class MarketAppFixture : IAsyncLifetime
{
    // 시드된 개발 플레이어(ddl.sql)
    public static readonly Guid Alpha = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Bravo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Charlie = Guid.Parse("33333333-3333-3333-3333-333333333333"); // admin
    // 레이드(익스트랙션) 전용 시드 플레이어 — 시작 인벤 비어있음(테스트 격리용).
    public static readonly Guid Delta = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid Echo = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid Foxtrot = Guid.Parse("66666666-6666-6666-6666-666666666666");
    // 장비(equipment)/중첩 컨테이너 전용 시드 플레이어 — 시작 인벤 비어있음(테스트 격리용).
    public static readonly Guid Golf = Guid.Parse("77777777-7777-7777-7777-777777777777");
    public static readonly Guid Hotel = Guid.Parse("88888888-8888-8888-8888-888888888888");

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

    /// <summary>일회용 Postgres 연결 문자열(테스트에서 item_ledger 등 직접 검증용).</summary>
    public string ConnString => _pg.GetConnectionString();

    /// <summary>호스트 DI(리포지토리 등 서버 내부 서비스를 직접 호출해 검증할 때).</summary>
    public IServiceProvider Services => _app.Services;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();

        // 스키마 + 아이템 마스터 102종 + 시드 적용 (컨테이너 내부 psql)
        var ddl = await File.ReadAllTextAsync(RepoFile("db/ddl.sql"));
        var result = await _pg.ExecScriptAsync(ddl);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ddl.sql 적용 실패: {result.Stderr}");

        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.GetConnectionString());
            b.UseSetting("Auth:Secret", "integration-test-secret-0123456789-abcdefghij");
            b.UseSetting("Orleans:ClusteringMode", "localhost");
            b.UseSetting("Orleans:SiloPort", "11199");
            b.UseSetting("Orleans:GatewayPort", "30199");
        });

        // 호스트 강제 기동(Orleans 실로 워밍업)
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

    private static string RepoFile(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Solution1.sln")))
            d = d.Parent;
        if (d is null) throw new InvalidOperationException("repo root(Solution1.sln)를 찾지 못함");
        return Path.Combine(d.FullName, rel);
    }
}

[CollectionDefinition("market")]
public sealed class MarketCollection : ICollectionFixture<MarketAppFixture>;

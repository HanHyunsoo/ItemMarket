using System.Text.Json.Serialization;
using ItemMarket.Api.Endpoints;
using ItemMarket.Api.Hubs;
using ItemMarket.Api.Infrastructure;
using ItemMarket.Grains.Data;
using ItemMarket.Grains.Grains;

var builder = WebApplication.CreateBuilder(args);

var cfg = builder.Configuration;
var connString = cfg.GetConnectionString("Postgres")
                 ?? "Host=localhost;Port=5432;Database=item_market;Username=market;Password=market";

// HTTP 리슨 포트를 우리가 소유한 설정키(Http:Port)로 제어한다. appsettings의 "Urls"를
// 쓰면 ASPNETCORE_URLS 환경변수가 appsettings에 밀려 덮어써지지 못하므로, 한 머신에서
// 여러 인스턴스를 띄우려면 이 방식이 필요하다. env Http__Port 로 인스턴스별 오버라이드.
var httpPort = cfg.GetValue("Http:Port", 5080);
// 바인딩 호스트도 소유 키(Http:Host)로 제어. 기본은 localhost(로컬 데모/멀티 인스턴스와
// 동일 동작). 컨테이너에서는 Http__Host=0.0.0.0 으로 오버라이드해 컨테이너 밖에서 접근 가능.
var httpHost = cfg["Http:Host"] ?? "localhost";
builder.WebHost.UseUrls($"http://{httpHost}:{httpPort}");

// Orleans 실로 co-host (클러스터링 localhost/adonet 스왑) — Infrastructure/OrleansHosting.cs
builder.AddMarketOrleans(connString);

// 인증(JWT Bearer) + 인가(admin 롤) + 토큰 발급기 — Infrastructure/AuthSetup.cs
builder.AddMarketAuth();

// Swagger/OpenAPI(/swagger) — JWT Bearer "Authorize" 포함. Infrastructure/SwaggerSetup.cs.
// 서비스 등록은 무해(inert)하며 문서 생성은 첫 요청 시 lazy → 기존 테스트/데모 불변.
builder.AddMarketOpenApi();

// 레이트 리미팅(주문 등록, 플레이어별) — Infrastructure/RateLimiting.cs
builder.AddMarketRateLimiting();

// DI: 리포지토리(싱글턴, 소스오브트루스 = Postgres)
builder.Services.AddSingleton(new MarketRepository(connString));

// 매칭 엔진 옵션 — 가격 밴드 샤딩 스위치(Market:PriceBandSize, 기본 0=비활성).
// 0이면 OrderBookGrain이 기존과 동일하게 템플릿당 단일 호가창을 직접 매칭한다.
// >0이면 코디네이터가 밴드별 OrderBandGrain으로 라우팅하며, 코디네이터가 병목이 되지
// 않도록 리엔트런시를 켠다(프로세스당 한 번 설정; 자세한 배경은 OrderBookGrain 참조).
var priceBandSize = cfg.GetValue("Market:PriceBandSize", 0);
builder.Services.AddSingleton(new MarketOptions(priceBandSize));
OrderBookGrain.AllowInterleaving = priceBandSize > 0;

// JSON: 열거형 문자열 직렬화 + camelCase(기본)
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// SignalR(실시간 푸시) — 직렬화는 REST와 동일(enum 문자열 + camelCase). 발행기 DI.
// Redis:ConnectionString 이 설정되면 SignalR 백플레인(StackExchangeRedis)을 붙인다.
// 다중 인스턴스에서 REST를 처리한 인스턴스가 IHubContext 로 발행해도, 구독자가 붙은
// 인스턴스는 다를 수 있다. 백플레인이 인스턴스 간 브로드캐스트를 중계해 크로스-인스턴스
// 라이브 푸시가 성립한다(docs/realtime-contract.md). 비어있으면(기본) 인메모리 단일
// 인스턴스로 기존과 완전히 동일하게 동작 → 기존 테스트/데모 불변.
var signalR = builder.Services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});
var redisConn = cfg["Redis:ConnectionString"];
if (!string.IsNullOrWhiteSpace(redisConn))
    signalR.AddStackExchangeRedis(redisConn);

builder.Services.AddSingleton<IMarketNotifier, MarketNotifier>();

// CORS (Vite 프론트) — SignalR은 자격증명(access_token) 전송 시 AllowCredentials +
// 명시적 오리진이 필요하다(AllowAnyOrigin과 함께 못 쓴다).
var corsOrigin = cfg["Cors:AllowedOrigin"] ?? "http://localhost:5173";
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

ApiResults.Logger = app.Logger; // Exec 헬퍼의 예상외 예외 로깅용

// 인터랙티브 API 문서(/swagger). 인증/인가 파이프라인 앞에 둬 익명 접근 가능.
app.UseMarketOpenApi();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
// 레이트 리미터는 인증 뒤에 둬서 파티션 키가 sub 클레임(플레이어)을 볼 수 있게 한다.
app.UseRateLimiter();

// 엔드포인트 — docs/api-contract.md 섹션과 1:1 (Endpoints/*.cs)
app.MapAuthEndpoints();
app.MapMarketEndpoints();
app.MapWalletEndpoints();
app.MapInventoryEndpoints();
app.MapStashEndpoints();
app.MapOrderEndpoints();
app.MapAdminEndpoints();

// 실시간 허브 — docs/realtime-contract.md
app.MapHub<MarketHub>("/hubs/market");

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous().WithTags("System");

app.Run();

// 통합 테스트에서 참조할 수 있도록 partial 클래스 노출.
namespace ItemMarket.Api
{
    public partial class Program;
}

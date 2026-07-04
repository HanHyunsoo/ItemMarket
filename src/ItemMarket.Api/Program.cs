using System.Text.Json.Serialization;
using ItemMarket.Api.Endpoints;
using ItemMarket.Api.Infrastructure;
using ItemMarket.Grains.Data;

var builder = WebApplication.CreateBuilder(args);

var cfg = builder.Configuration;
var connString = cfg.GetConnectionString("Postgres")
                 ?? "Host=localhost;Port=5432;Database=item_market;Username=market;Password=market";

// HTTP 리슨 포트를 우리가 소유한 설정키(Http:Port)로 제어한다. appsettings의 "Urls"를
// 쓰면 ASPNETCORE_URLS 환경변수가 appsettings에 밀려 덮어써지지 못하므로, 한 머신에서
// 여러 인스턴스를 띄우려면 이 방식이 필요하다. env Http__Port 로 인스턴스별 오버라이드.
var httpPort = cfg.GetValue("Http:Port", 5080);
builder.WebHost.UseUrls($"http://localhost:{httpPort}");

// Orleans 실로 co-host (클러스터링 localhost/adonet 스왑) — Infrastructure/OrleansHosting.cs
builder.AddMarketOrleans(connString);

// 인증(JWT Bearer) + 인가(admin 롤) + 토큰 발급기 — Infrastructure/AuthSetup.cs
builder.AddMarketAuth();

// DI: 리포지토리(싱글턴, 소스오브트루스 = Postgres)
builder.Services.AddSingleton(new MarketRepository(connString));

// JSON: 열거형 문자열 직렬화 + camelCase(기본)
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// CORS (Vite 프론트)
var corsOrigin = cfg["Cors:AllowedOrigin"] ?? "http://localhost:5173";
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigin).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

ApiResults.Logger = app.Logger; // Exec 헬퍼의 예상외 예외 로깅용

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// 엔드포인트 — docs/api-contract.md 섹션과 1:1 (Endpoints/*.cs)
app.MapAuthEndpoints();
app.MapMarketEndpoints();
app.MapWalletEndpoints();
app.MapInventoryEndpoints();
app.MapStashEndpoints();
app.MapOrderEndpoints();
app.MapAdminEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

// 통합 테스트에서 참조할 수 있도록 partial 클래스 노출.
namespace ItemMarket.Api
{
    public partial class Program;
}

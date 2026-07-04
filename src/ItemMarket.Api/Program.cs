using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using ItemMarket.Api;
using ItemMarket.Contracts.Admin;
using ItemMarket.Contracts.Auth;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Orders;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Orleans.Serialization;

var builder = WebApplication.CreateBuilder(args);

var cfg = builder.Configuration;
var connString = cfg.GetConnectionString("Postgres")
                 ?? "Host=localhost;Port=5432;Database=item_market;Username=market;Password=market";

// ADO.NET 클러스터링(Npgsql) 프로바이더 등록 — adonet 모드에서 필요.
if (!DbProviderFactories.GetProviderInvariantNames().Contains("Npgsql"))
    DbProviderFactories.RegisterFactory("Npgsql", NpgsqlFactory.Instance);

// --------------------------------------------------------------------------
//  Orleans 실로 호스팅 (co-host). 클러스터링은 config 스왑으로 분기.
//   - localhost : 개발용 단일 실로.
//   - adonet    : Postgres 멤버십으로 다중 실로 클러스터 형성(Redis 미사용).
// --------------------------------------------------------------------------
// 클러스터링 스왑 스위치. Orleans 9는 "Orleans" 설정 섹션을 예약(프로바이더 자동 바인딩)하므로
// 예약 키 "Clustering" 대신 스칼라 "ClusteringMode"를 사용한다(Orleans는 이 키를 무시).
var clustering = cfg["Orleans:ClusteringMode"] ?? "localhost";
var clusterId = cfg["Orleans:ClusterId"] ?? "item-market";
var serviceId = cfg["Orleans:ServiceId"] ?? "item-market";
var siloPort = cfg.GetValue("Orleans:SiloPort", 11111);
var gatewayPort = cfg.GetValue("Orleans:GatewayPort", 30000);

// HTTP 리슨 포트를 우리가 소유한 설정키(Http:Port)로 제어한다. appsettings의 "Urls"를
// 쓰면 ASPNETCORE_URLS 환경변수가 appsettings에 밀려 덮어써지지 못하므로, 한 머신에서
// 여러 인스턴스를 띄우려면 이 방식이 필요하다. env Http__Port 로 인스턴스별 오버라이드.
var httpPort = cfg.GetValue("Http:Port", 5080);
builder.WebHost.UseUrls($"http://localhost:{httpPort}");

builder.Host.UseOrleans(silo =>
{
    if (string.Equals(clustering, "adonet", StringComparison.OrdinalIgnoreCase))
    {
        silo.Configure<Orleans.Configuration.ClusterOptions>(o =>
            {
                o.ClusterId = clusterId;
                o.ServiceId = serviceId;
            })
            .UseAdoNetClustering(o =>
            {
                o.Invariant = "Npgsql";
                o.ConnectionString = connString;
            })
            .ConfigureEndpoints(siloPort: siloPort, gatewayPort: gatewayPort);
    }
    else
    {
        silo.UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort);
    }
});

// --------------------------------------------------------------------------
//  Orleans 직렬화: 계약(Contracts) DTO는 공유 계약이라 [GenerateSerializer]를
//  붙일 수 없다. grain 경계를 넘는 ItemMarket.* 타입은 JSON 직렬화기로 처리한다
//  (인프로세스 copier 포함). 계약을 수정하지 않고 코덱 부재 문제를 해결.
// --------------------------------------------------------------------------
builder.Services.AddSerializer(sb => sb.AddJsonSerializer(
    isSupported: t => t.Namespace is not null && t.Namespace.StartsWith("ItemMarket")));

// --------------------------------------------------------------------------
//  DI: 리포지토리(싱글턴, 소스오브트루스 = Postgres)
// --------------------------------------------------------------------------
builder.Services.AddSingleton(new MarketRepository(connString));

// --------------------------------------------------------------------------
//  JSON: 열거형 문자열 직렬화 + camelCase(기본)
// --------------------------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// --------------------------------------------------------------------------
//  인증(JWT Bearer, HS256) + 인가(admin 롤)
// --------------------------------------------------------------------------
var secret = cfg["Auth:Secret"] ?? throw new InvalidOperationException("Auth:Secret 누락");
var issuer = cfg["Auth:Issuer"] ?? "item-market";
var audience = cfg["Auth:Audience"] ?? "item-market-client";
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // "sub"/"role" 클레임명을 그대로 유지
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = signingKey,
            NameClaimType = "name",
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("admin", p => p.RequireRole("admin"));

// --------------------------------------------------------------------------
//  CORS (Vite 프론트)
// --------------------------------------------------------------------------
var corsOrigin = cfg["Cors:AllowedOrigin"] ?? "http://localhost:5173";
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigin).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// ==========================================================================
//  헬퍼
// ==========================================================================
var adminPlayerId = cfg["Auth:AdminPlayerId"] ?? "33333333-3333-3333-3333-333333333333";
var expiresMinutes = cfg.GetValue("Auth:ExpiresMinutes", 480);

static Guid CurrentPlayer(ClaimsPrincipal user)
{
    var sub = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    return Guid.TryParse(sub, out var id) ? id : throw new DomainException(ErrorCode.Unauthorized, "토큰에 유효한 sub가 없습니다.");
}

// 도메인 예외를 ApiResponse 실패 봉투 + 적절한 HTTP 상태로 변환.
// 예상 밖 예외(NpgsqlException 등)도 봉투를 유지한 500으로 감싼다 —
// 그렇지 않으면 프론트가 기대하는 ApiResponse 계약이 깨진 원시 500이 샌다.
async Task<IResult> Exec<T>(Func<Task<T>> action)
{
    try
    {
        var data = await action();
        return Results.Ok(ApiResponse<T>.Ok(data));
    }
    catch (DomainException ex)
    {
        var status = ex.Code switch
        {
            ErrorCode.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorCode.Forbidden => StatusCodes.Status403Forbidden,
            ErrorCode.PlayerNotFound or ErrorCode.TemplateNotFound or ErrorCode.InstanceNotFound
                or ErrorCode.OrderNotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(ApiResponse<T>.Fail(ex.Code, ex.Message), statusCode: status);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "처리되지 않은 예외");
        return Results.Json(ApiResponse<T>.Fail(ErrorCode.Unknown, "서버 내부 오류가 발생했습니다."),
            statusCode: StatusCodes.Status500InternalServerError);
    }
}

// ==========================================================================
//  인증 엔드포인트
// ==========================================================================
app.MapPost("/api/auth/login", async (LoginRequest req, MarketRepository repo) => await Exec(async () =>
{
    var player = await repo.GetPlayerAsync(req.PlayerId)
        ?? throw new DomainException(ErrorCode.PlayerNotFound, "플레이어를 찾을 수 없습니다.");

    var isAdmin = string.Equals(player.Id.ToString(), adminPlayerId, StringComparison.OrdinalIgnoreCase);
    var claims = new List<Claim>
    {
        new("sub", player.Id.ToString()),
        new("name", player.DisplayName)
    };
    if (isAdmin) claims.Add(new Claim("role", "admin"));

    var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
    var expires = DateTime.UtcNow.AddMinutes(expiresMinutes);
    var token = new JwtSecurityToken(issuer, audience, claims, expires: expires, signingCredentials: creds);
    var jwt = new JwtSecurityTokenHandler().WriteToken(token);

    var roles = isAdmin ? new List<string> { "admin" } : [];
    return new TokenResponse(jwt, "Bearer", expiresMinutes * 60L, player.Id, player.DisplayName, roles);
})).AllowAnonymous();

// ==========================================================================
//  플레이어 엔드포인트
// ==========================================================================
var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/catalog", (MarketRepository repo) => Exec(async () => await repo.GetCatalogAsync()));

api.MapGet("/wallet", (ClaimsPrincipal u, IGrainFactory gf) =>
    Exec(() => gf.GetGrain<IWalletGrain>(CurrentPlayer(u)).Get()));

api.MapGet("/wallet/ledger", (ClaimsPrincipal u, IGrainFactory gf, int page = 1, int size = 20) =>
    Exec(() => gf.GetGrain<IWalletGrain>(CurrentPlayer(u)).GetLedger(page, size)));

api.MapGet("/inventory", (ClaimsPrincipal u, IGrainFactory gf) =>
    Exec(() => gf.GetGrain<IPlayerInventoryGrain>(CurrentPlayer(u)).Get()));

api.MapGet("/market/{templateId:int}/book", (int templateId, IGrainFactory gf) =>
    Exec(() => gf.GetGrain<IOrderBookGrain>(templateId).GetSnapshot()));

api.MapGet("/market/{templateId:int}/trades", (int templateId, MarketRepository repo, int page = 1, int size = 20) =>
    Exec(() => repo.GetTradesByTemplateAsync(templateId, Math.Max(1, page), Math.Clamp(size, 1, 200))));

api.MapPost("/orders", (PlaceOrderRequest req, ClaimsPrincipal u, IGrainFactory gf) =>
    Exec(() => gf.GetGrain<IOrderBookGrain>(req.ItemTemplateId).PlaceOrder(CurrentPlayer(u), req)));

api.MapGet("/orders", (ClaimsPrincipal u, MarketRepository repo) =>
    Exec(() => repo.GetOrdersByPlayerAsync(CurrentPlayer(u))));

api.MapGet("/orders/{id:guid}", (Guid id, ClaimsPrincipal u, MarketRepository repo) => Exec(async () =>
{
    var pid = CurrentPlayer(u);
    var order = await repo.GetOrderAsync(id)
        ?? throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다.");
    if (order.PlayerId != pid)
        throw new DomainException(ErrorCode.OrderNotOwned, "본인 주문이 아닙니다.");
    return order.ToDto();
}));

api.MapDelete("/orders/{id:guid}", (Guid id, ClaimsPrincipal u, IGrainFactory gf, MarketRepository repo) => Exec(async () =>
{
    var pid = CurrentPlayer(u);
    var order = await repo.GetOrderAsync(id)
        ?? throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다.");
    return await gf.GetGrain<IOrderBookGrain>(order.TemplateId).CancelOrder(pid, id, isAdmin: false);
}));

// ==========================================================================
//  운영(어드민) 엔드포인트 — admin 롤 필요(없으면 403)
// ==========================================================================
var admin = app.MapGroup("/api/admin").RequireAuthorization("admin");

admin.MapGet("/players/{id:guid}/wallet", (Guid id, IGrainFactory gf) =>
    Exec(() => gf.GetGrain<IWalletGrain>(id).Get()));

admin.MapPost("/wallet/adjust", (AdminAdjustWalletRequest req, IGrainFactory gf) =>
    Exec(() => gf.GetGrain<IWalletGrain>(req.PlayerId).AdminAdjust(req.Delta, req.Reason)));

admin.MapPost("/grant/stack", (AdminGrantStackRequest req, IGrainFactory gf) =>
    Exec(() => gf.GetGrain<IPlayerInventoryGrain>(req.PlayerId).AdminGrantStack(req.TemplateId, req.Quantity)));

admin.MapPost("/grant/instance", (AdminGrantInstanceRequest req, IGrainFactory gf) =>
    Exec(() => gf.GetGrain<IPlayerInventoryGrain>(req.PlayerId).AdminGrantInstance(req.TemplateId, req.Durability, req.Attachments)));

admin.MapPost("/orders/force-cancel", (AdminForceCancelOrderRequest req, IGrainFactory gf, MarketRepository repo) => Exec(async () =>
{
    var order = await repo.GetOrderAsync(req.OrderId)
        ?? throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다.");
    return await gf.GetGrain<IOrderBookGrain>(order.TemplateId).CancelOrder(order.PlayerId, req.OrderId, isAdmin: true);
}));

admin.MapGet("/orders", (MarketRepository repo, int? templateId, string? status, int page = 1, int size = 20) =>
    Exec(() =>
    {
        // Enum.TryParse는 "7" 같은 숫자 문자열도 (정의 안 된 값으로) 통과시키므로 IsDefined로 걸러낸다.
        OrderStatus? parsed = Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : null;
        return repo.GetOrdersAdminAsync(templateId, parsed, Math.Max(1, page), Math.Clamp(size, 1, 200));
    }));

admin.MapGet("/trades", (MarketRepository repo, int page = 1, int size = 20) =>
    Exec(() => repo.GetTradesAllAsync(Math.Max(1, page), Math.Clamp(size, 1, 200))));

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

// 통합 테스트에서 참조할 수 있도록 partial 클래스 노출.
namespace ItemMarket.Api
{
    public partial class Program;
}

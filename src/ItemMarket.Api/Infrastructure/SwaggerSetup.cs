using Microsoft.OpenApi;

namespace ItemMarket.Api.Infrastructure;

/// <summary>
/// Swagger/OpenAPI 구성. /swagger 에서 인터랙티브 문서를 제공하고, JWT Bearer 인증을
/// 문서화해 UI의 "Authorize" 버튼으로 토큰을 넣어 보호된 엔드포인트를 호출할 수 있게 한다.
/// 엔드포인트는 태그(Auth/Market/Wallet/Orders/Stash/Inventory/Admin)로 그룹핑된다.
///
/// 항상 켜도 무해(inert-safe)하다: 미들웨어를 붙이지 않으면 런타임 동작이 바뀌지 않으므로
/// 기존 통합 테스트에는 영향이 없다(테스트는 /swagger를 타지 않는다).
/// </summary>
public static class SwaggerSetup
{
    private const string BearerScheme = "Bearer";

    public static WebApplicationBuilder AddMarketOpenApi(this WebApplicationBuilder builder)
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Item Market API",
                Version = "v1",
                Description =
                    "아포칼립스 익스트랙션 슈터 테마의 아이템 거래소 API.\n\n" +
                    "모든 응답은 공통 봉투 `ApiResponse<T>`(`success`/`data`/`error`)로 감싼다. " +
                    "인증은 JWT Bearer: `POST /api/auth/login` 으로 토큰을 받아 우측 상단 **Authorize** 에 " +
                    "붙이면 보호된 엔드포인트를 호출할 수 있다. 액세스 토큰이 만료되면 " +
                    "`POST /api/auth/refresh` 로 갱신(로테이션)한다."
            });

            // JWT Bearer: "Authorize" 버튼이 동작하도록 보안 스킴 + 전역 요구사항 등록.
            o.AddSecurityDefinition(BearerScheme, new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "로그인으로 받은 액세스 토큰만 입력하세요('Bearer ' 접두사는 UI가 자동으로 붙입니다)."
            });
            o.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(BearerScheme, doc)] = []
            });

            o.SupportNonNullableReferenceTypes();
        });
        return builder;
    }

    public static WebApplication UseMarketOpenApi(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(o =>
        {
            o.SwaggerEndpoint("/swagger/v1/swagger.json", "Item Market API v1");
            o.DocumentTitle = "Item Market API";
        });
        return app;
    }
}

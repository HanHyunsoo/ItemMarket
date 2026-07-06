using System.Text.Json;
using Xunit;

namespace ItemMarket.IntegrationTests;

/// <summary>
/// OpenAPI 문서(M3): enum은 정수 인덱스가 아니라 문자열 + 허용값 목록으로 노출돼야 한다
/// (런타임 직렬화가 JsonStringEnumConverter라 계약이 문자열이므로). 생성된 클라이언트/문서가
/// 실제 wire 계약과 일치하도록 스키마 필터로 강제한 것을 회귀로 고정한다.
/// </summary>
[Collection("market")]
public class OpenApiTests(MarketAppFixture f)
{
    private readonly MarketAppFixture _f = f;

    [Fact]
    public async Task Openapi_exposes_enums_as_string_with_values()
    {
        var c = _f.Anon();
        var json = await c.GetStringAsync("/swagger/v1/swagger.json");
        using var doc = JsonDocument.Parse(json);

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // 요청 DTO에 쓰이는 enum들은 문자열 + 이름 목록으로 노출돼야 한다(정수 인덱스 아님).
        foreach (var (name, expected) in new[]
                 {
                     ("OrderSide", "Buy"),
                     ("GridContainer", "Stash"),
                     ("StashEntryKind", "Stack"),
                     ("EquipSlot", "Helmet"),
                 })
        {
            Assert.True(schemas.TryGetProperty(name, out var e), $"{name} 스키마가 없습니다.");
            Assert.Equal("string", e.GetProperty("type").GetString());
            var values = e.GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToList();
            Assert.Contains(expected, values);
            Assert.DoesNotContain(values, v => int.TryParse(v, out _));
        }
    }
}

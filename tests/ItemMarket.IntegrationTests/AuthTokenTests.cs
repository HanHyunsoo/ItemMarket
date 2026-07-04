using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ItemMarket.Contracts.Auth;
using ItemMarket.Contracts.Wallet;
using Xunit;
using static ItemMarket.IntegrationTests.MarketAppFixture;

namespace ItemMarket.IntegrationTests;

/// <summary>
/// JWT 리프레시 토큰 플로우 통합테스트: 로그인이 액세스+리프레시 쌍을 발급하고,
/// 리프레시가 동작하는 새 액세스 토큰을 반환하며(로테이션), 회전된 옛 토큰은 401로 거부되고,
/// 로그아웃이 토큰을 폐기하는지 검증한다.
/// </summary>
[Collection("market")]
public class AuthTokenTests(MarketAppFixture f)
{
    private readonly MarketAppFixture _f = f;

    private async Task<TokenResponse> LoginFull(Guid playerId)
    {
        using var c = _f.Anon();
        var res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(playerId), Json);
        res.EnsureSuccessStatusCode();
        var body = await Api<TokenResponse>(res);
        Assert.True(body.Success);
        return body.Data!;
    }

    private HttpClient Bearer(string accessToken)
    {
        var c = _f.Anon();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return c;
    }

    [Fact]
    public async Task Login_issues_access_and_refresh_pair()
    {
        var tok = await LoginFull(Alpha);
        Assert.False(string.IsNullOrWhiteSpace(tok.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tok.RefreshToken));
        Assert.Equal("Bearer", tok.TokenType);
        Assert.True(tok.AccessTokenExpiresIn > 0);
    }

    [Fact]
    public async Task Refresh_returns_new_working_access_token()
    {
        var tok = await LoginFull(Alpha);

        using var anon = _f.Anon();
        var res = await anon.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tok.RefreshToken), Json);
        res.EnsureSuccessStatusCode();
        var refreshed = (await Api<TokenResponse>(res)).Data!;

        // 로테이션: 새 리프레시 토큰은 옛것과 다르다.
        Assert.NotEqual(tok.RefreshToken, refreshed.RefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(refreshed.AccessToken));

        // 새 액세스 토큰으로 보호된 엔드포인트를 호출할 수 있다.
        using var authed = Bearer(refreshed.AccessToken);
        var wallet = await authed.GetAsync("/api/wallet");
        wallet.EnsureSuccessStatusCode();
        var w = await Api<WalletDto>(wallet);
        Assert.True(w.Success);
        Assert.Equal(Alpha, w.Data!.PlayerId);
    }

    [Fact]
    public async Task Rotated_old_refresh_token_is_rejected_401()
    {
        var tok = await LoginFull(Bravo);
        using var anon = _f.Anon();

        // 첫 리프레시가 토큰을 회전(옛것 폐기).
        var first = await anon.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tok.RefreshToken), Json);
        first.EnsureSuccessStatusCode();

        // 이미 폐기된 옛 토큰 재사용 → 401.
        var reuse = await anon.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tok.RefreshToken), Json);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_refresh_token()
    {
        var tok = await LoginFull(Charlie);
        using var anon = _f.Anon();

        var logout = await anon.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(tok.RefreshToken), Json);
        logout.EnsureSuccessStatusCode();

        // 폐기된 뒤에는 리프레시가 401.
        var afterLogout = await anon.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tok.RefreshToken), Json);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Unknown_refresh_token_is_rejected_401()
    {
        using var anon = _f.Anon();
        var res = await anon.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest("not-a-real-refresh-token"), Json);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}

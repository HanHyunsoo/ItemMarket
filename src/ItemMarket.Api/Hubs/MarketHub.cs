using ItemMarket.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ItemMarket.Api.Hubs;

/// <summary>
/// 실시간 허브(<c>/hubs/market</c>). 호가창/체결/지갑 변경을 서버 푸시로 전달한다.
/// 인증(JWT Bearer) 필수 — WebSocket은 헤더를 못 실으므로 <c>?access_token=</c> 쿼리로
/// 토큰을 받는다(AuthSetup의 OnMessageReceived 참고). 연결 시 토큰의 sub(playerId)로
/// <c>user:{playerId}</c> 그룹에 자동 가입한다.
/// </summary>
[Authorize]
public sealed class MarketHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var sub = Context.User?.FindFirst("sub")?.Value;
        if (Guid.TryParse(sub, out var playerId))
            await Groups.AddToGroupAsync(Context.ConnectionId, MarketGroups.User(playerId));

        await base.OnConnectedAsync();
    }

    /// <summary>해당 템플릿의 호가창/체결 그룹(<c>tmpl:{id}</c>)을 구독한다.</summary>
    public Task SubscribeTemplate(int templateId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, MarketGroups.Template(templateId));

    /// <summary>템플릿 구독을 해제한다.</summary>
    public Task UnsubscribeTemplate(int templateId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, MarketGroups.Template(templateId));
}

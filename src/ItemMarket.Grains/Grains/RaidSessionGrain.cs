using ItemMarket.Contracts.Raid;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;

namespace ItemMarket.Grains.Grains;

/// <summary>
/// 레이드 세션 grain(키 = playerId). WalletGrain/StashGrain과 같은 무상태(pass-through) 설계다.
/// 세션 상태를 인메모리에 캐시하지 않고 매 호출마다 Postgres(소스오브트루스)를 읽어 정산한다.
/// Orleans는 플레이어당 grain 활성화를 단일로 보장하고 기본 non-reentrant라, 같은 플레이어의
/// StartRaid/Extract/Die 요청은 자동으로 직렬화된다(이중 진입 없음). 각 전이는
/// MarketRepository의 단일 Postgres 트랜잭션으로 원자 정산된다(예외 시 전량 롤백).
/// </summary>
public sealed class RaidSessionGrain(MarketRepository repo) : Grain, IRaidSessionGrain
{
    private Guid PlayerId => this.GetPrimaryKey();

    public Task<RaidSessionDto?> Get() => repo.GetRaidSnapshotAsync(PlayerId);

    public Task<RaidSessionDto> StartRaid() => repo.StartRaidAsync(PlayerId);

    public Task<RaidSessionDto> AddLoot(AddLootRequest req) => repo.AddLootAsync(PlayerId, req);

    public Task<RaidSessionDto> Extract() => repo.ExtractAsync(PlayerId);

    public Task<RaidSessionDto> Die() => repo.DieAsync(PlayerId);
}

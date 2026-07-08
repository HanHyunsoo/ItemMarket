using Dapper;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Equipment;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Leaderboard;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Raid;
using ItemMarket.Contracts.Stash;
using ItemMarket.Contracts.Trades;
using ItemMarket.Contracts.Wallet;
using Npgsql;

namespace ItemMarket.Grains.Data;

// (partial) 익스트랙션 레이드 세션 상태머신 + 드롭테이블(존 티어)
public sealed partial class MarketRepository
{

    // ── 드롭테이블(존 티어) ────────────────────────────────────────────────
    // rarity 순서 고정. 존별 가중치[COMMON,UNCOMMON,RARE,EPIC,LEGENDARY] + loot당 사망확률 상승률.
    // 고위험 존일수록 좋은 등급이 잘 나오지만 사망확률이 빠르게 오른다(리스크/보상, #1 연동).
    private static readonly string[] Rarities = ["COMMON", "UNCOMMON", "RARE", "EPIC", "LEGENDARY"];
    // 존별: (rarity 가중치, loot당 사망확률 상승 bps, 기본 사망확률 floor bps).
    // BaseDeathBps는 출격 시점의 death_chance_bps 초깃값 — 루팅 0이어도 존 진입만으로 사망 위험이 생겨
    // "값비싼 장비를 걸고 나갈까"라는 반입 판돈이 실재하게 된다(반입 리스크 상시화).
    private static readonly Dictionary<RaidZone, (int[] Weights, int DeathIncBps, int BaseDeathBps)> ZoneConfig = new()
    {
        // Scav: 무료 최저 티어 — COMMON/UNCOMMON만(최저 드롭), 낮은 위험. EV가 낮아 정상 티어 대비 열등해
        // 남용 유인이 없다(수수료 0이라 진입 장벽만 낮춘 재기용 최저 티어).
        [RaidZone.Scav] = ([80, 20, 0, 0, 0], 600, 300),
        [RaidZone.Low] = ([55, 30, 12, 3, 0], 800, 300),
        [RaidZone.Med] = ([35, 32, 22, 9, 2], 1200, 600),
        // High: "짧고 굵게"(≈3루팅 최대 EV)가 Med의 "길게 쌓기"(≈4루팅)를 상회하는 고위험 고보상 존.
        // 위험 프로파일(floor 1000·inc 1500·수수료 600)은 그대로 두고 드롭 가중치만 COMMON→EPIC로 이동해
        // 보상만 키웠다(위험 프리미엄 확대: Med 대비 EV +19%→+36%로, 분산·수수료 대비 프리미엄이 얇던 문제 해소).
        // LEGENDARY(5%)는 유지해 추격 아이템의 희소성 보존. death@n*=55%·n*=3 불변.
        [RaidZone.High] = ([11, 30, 35, 19, 5], 1500, 1000),
    };

    /// <summary>존별 출격 수수료(캡 싱크). 매 출격마다 소각돼 저욕심 1-루팅 그라인딩을 억제하고
    /// 창고 확장(일회성) 이후에도 recurring sink를 남긴다. 고위험 존일수록 진입 장벽이 높다.</summary>
    private long EntryFee(RaidZone zone) => zone switch
    {
        RaidZone.Scav => 0,   // 무료 재기 티어
        RaidZone.Low => raidEntryFeeLow,
        RaidZone.High => raidEntryFeeHigh,
        _ => raidEntryFeeMed
    };

    /// <summary>존 메타(출격 화면용): 존별 수수료 + loot당 사망확률 상승률. 프론트가 배당을 표시한다.</summary>
    public IReadOnlyList<ZoneInfoDto> GetZones() =>
    [
        new(RaidZone.Scav, EntryFee(RaidZone.Scav), ZoneConfig[RaidZone.Scav].DeathIncBps, ZoneConfig[RaidZone.Scav].BaseDeathBps),
        new(RaidZone.Low, EntryFee(RaidZone.Low), ZoneConfig[RaidZone.Low].DeathIncBps, ZoneConfig[RaidZone.Low].BaseDeathBps),
        new(RaidZone.Med, EntryFee(RaidZone.Med), ZoneConfig[RaidZone.Med].DeathIncBps, ZoneConfig[RaidZone.Med].BaseDeathBps),
        new(RaidZone.High, EntryFee(RaidZone.High), ZoneConfig[RaidZone.High].DeathIncBps, ZoneConfig[RaidZone.High].BaseDeathBps),
    ];

    /// <summary>존 가중치로 rarity 하나를 뽑는다(Random.Shared). weight=0인 등급은 뽑히지 않는다.</summary>
    private static string RollRarity(int[] weights)
    {
        int total = weights.Sum();
        int r = Random.Shared.Next(total);
        for (int i = 0; i < weights.Length; i++)
        {
            if (r < weights[i]) return Rarities[i];
            r -= weights[i];
        }
        return Rarities[0];
    }

    private static RaidZone ParseZone(string z) => z switch
    {
        "Scav" => RaidZone.Scav,
        "Low" => RaidZone.Low,
        "High" => RaidZone.High,
        _ => RaidZone.Med
    };

    // ======================================================================
    //  익스트랙션 레이드 — 서비스 계층 상태기계 + 원자적 정산(단일 Postgres 트랜잭션).
    //  at-risk(위험) = 스태시 밖 전부: 장착 장비(HELMET/ARMOR/WEAPON/BACKPACK/RIG) +
    //    장착된 백팩/리그의 중첩 그리드 내용물 + 주머니(POCKETS). STASH는 절대 무관(항상 안전).
    //  매도 에스크로와 동일한 "자산 잠금" 이동을 재사용한다:
    //    StartRaid = 위 대상을 위험(at-risk)으로 잠금(inventory_stack 차감 / instance owner=NULL /
    //                장착 슬롯 해제),
    //    Extract   = 반입+획득 전량을 소유로 복귀(스택 가산 / instance owner 복원) 후 원위치(장착
    //                슬롯/컨테이너 내부/주머니)로 정확히 복원, 획득분은 반입 공간에 first-fit,
    //    Die       = 위험 전량 소실(스택 미복귀 / instance tombstone). 스태시(안전)는 무관.
    //  모든 이동은 item_ledger(append-only)에 기록한다(wallet_ledger 패턴 차용).
    // ======================================================================

    /// <summary>현재 ACTIVE 레이드 세션 스냅샷. 활성 세션이 없으면 null(계약: null = 진행 중 레이드 없음).
    /// 해결된(EXTRACTED/DIED) 세션은 반환하지 않는다 — 결과 화면은 extract/die 응답으로 표시한다.</summary>
    /// <summary>플레이어에게 진행 중(ACTIVE) 레이드 세션이 있는가. 레이드 중에는 스태시/장비 변이를
    /// 잠가야 한다 — 반입 아이템은 필드에 나가 있고, 변이를 허용하면 Extract 원위치 복원이 새 배치와
    /// 고유 제약(슬롯/셀)에서 충돌해 정산이 깨진다(A-1).</summary>
    public async Task<bool> HasActiveRaidAsync(Guid playerId)
    {
        await using var db = Open();
        return await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM raid_session WHERE player_id = @playerId AND status = 'ACTIVE')",
            new { playerId });
    }

    /// <summary>플레이어 단위 advisory 트랜잭션 락. Orleans 단일 활성화는 같은 grain 안에서만 직렬화하므로,
    /// grain 경계를 넘는 변이(레이드 시작(RaidSessionGrain) ↔ 스태시/장비 변이(StashGrain))의 TOCTOU를 막으려면
    /// DB 레벨 공통 락이 필요하다(F-1). xact 락이라 트랜잭션 종료 시 자동 해제된다.</summary>
    private static Task LockPlayerAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid playerId)
        => db.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
            new { key = playerId.ToString() }, tx);

    /// <summary>트랜잭션 내부에서 ACTIVE 레이드를 재확인해 변이를 거부한다(RaidActive). LockPlayerAsync와 함께
    /// 쓰면 레이드 시작과 스태시/장비 변이가 DB에서 직렬화돼, grain 경계를 넘는 동시 호출의 TOCTOU가 닫힌다.</summary>
    private static async Task ThrowIfActiveRaidAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid playerId)
    {
        var active = await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM raid_session WHERE player_id = @playerId AND status = 'ACTIVE')",
            new { playerId }, tx);
        if (active)
            throw new DomainException(ErrorCode.RaidActive,
                "레이드 진행 중에는 스태시·장비를 변경할 수 없습니다. 먼저 탈출하거나 사망 처리하세요.");
    }

    public async Task<RaidSessionDto?> GetRaidSnapshotAsync(Guid playerId)
    {
        await using var db = Open();
        var s = await db.QuerySingleOrDefaultAsync(
            @"SELECT id, player_id, status
              FROM raid_session WHERE player_id = @playerId AND status = 'ACTIVE'
              LIMIT 1",
            new { playerId });
        if (s is null) return null;
        return await LoadRaidDtoAsync(db, null, (Guid)s.id, (Guid)s.player_id,
            Enums.ToRaidStatus((string)s.status));
    }

    /// <summary>
    /// StartRaid(한 트랜잭션): ACTIVE 세션이 없어야 하며, 스태시 밖 전부(장착 장비 + 장착된
    /// 백팩/리그 내용물 + 주머니)를 위험 스냅샷으로 옮기고 인벤토리 가용성에서 제거(에스크로)한 뒤
    /// 그 배치를 비운다. STASH는 절대 건드리지 않는다(항상 안전). RAID_BROUGHT 기록.
    /// 스태시 밖이 전부 비어 있으면(장비도 없고 주머니도 비고 컨테이너도 없으면) RaidNothingToDeploy로
    /// 거부한다 — 반대로 장착 장비만 있어도(주머니가 비어도) 출격은 항상 성공해야 한다.
    /// </summary>
    public async Task<RaidSessionDto> StartRaidAsync(Guid playerId, RaidZone zone)
    {
        // JsonStringEnumConverter는 기본적으로 범위 밖 정수({"zone":99})도 바인딩하므로 값을 검증한다(F-2).
        if (!Enum.IsDefined(zone))
            throw new DomainException(ErrorCode.ValidationError, "알 수 없는 출격 존입니다(Low/Med/High).");

        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            // 0) 스태시/장비 변이(Equip/Unequip/MoveStack)와 DB 레벨 직렬화 — grain 경계 TOCTOU 차단(F-1).
            //    이 락을 잡은 뒤 at-risk를 걷어가므로, 동시에 들어온 변이는 락을 기다렸다가 ACTIVE를 보고 거부된다.
            await LockPlayerAsync(db, tx, playerId);

            // 1) ACTIVE 중복 방지(부분 유니크 인덱스가 최종 강판; 명확한 에러 위해 선검사).
            var existing = await db.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM raid_session WHERE player_id = @playerId AND status = 'ACTIVE'",
                new { playerId }, tx);
            if (existing is not null)
                throw new DomainException(ErrorCode.RaidActive, "이미 진행 중인 레이드가 있습니다.");

            // 1.5) 출격 수수료 차감(캡 싱크). 잔액 부족이면 출격 거부(롤백 — at-risk를 걷지 않는다).
            var fee = EntryFee(zone);
            if (fee > 0)
            {
                var balance = await db.ExecuteScalarAsync<long>(
                    "SELECT balance FROM wallet WHERE player_id = @playerId FOR UPDATE", new { playerId }, tx);
                if (balance < fee)
                    throw new DomainException(ErrorCode.InsufficientFunds,
                        $"출격 수수료 {fee} 캡이 필요합니다(보유 {balance}).");
                var after = balance - fee;
                await db.ExecuteAsync("UPDATE wallet SET balance = @after WHERE player_id = @playerId",
                    new { after, playerId }, tx);
                await InsertLedgerAsync(db, tx, playerId, -fee, after, WalletLedgerReason.RaidEntryFee, null);
            }

            // 2) 위험(at-risk) 대상 수집 = 스태시 밖 전부: 장착 슬롯(전부) + 장착된 백팩/리그의
            //    중첩 그리드 내용 + 주머니(POCKETS). 장착 인스턴스(+백팩/리그 자체)와 그 안의 내용물,
            //    주머니 아이템이 모두 위험이다. STASH는 이 수집 대상에 전혀 포함되지 않는다(항상 안전).
            var equipment = (await db.QueryAsync(
                @"SELECT pe.slot, pe.instance_id, ii.template_id
                  FROM player_equipment pe JOIN item_instance ii ON ii.id = pe.instance_id
                  WHERE pe.player_id = @playerId ORDER BY pe.slot",
                new { playerId }, tx)).ToList();
            var equippedIds = equipment.Select(e => (Guid)e.instance_id).ToArray();

            // 주머니 배치 + 장착된 백팩/리그의 중첩 배치. x,y까지 읽어 원위치 스냅샷에 쓴다.
            var placements = (await db.QueryAsync(
                @"SELECT container, kind, template_id, instance_id, container_instance_id, x, y, quantity
                  FROM stash_placement
                  WHERE player_id = @playerId
                    AND (container = 'POCKETS'
                         OR (container = 'CONTAINER' AND container_instance_id = ANY(@equippedIds)))",
                new { playerId, equippedIds }, tx)).ToList();

            // 스택(주머니+중첩): template 오름차순 락 순서. 각 물리 컨테이너별 칸(다중 스택 가능). 원위치(x,y) 포함.
            var stackItems = placements.Where(p => (string)p.kind == "STACK")
                .Select(p => (Container: (string)p.container, Cid: (Guid?)p.container_instance_id,
                              TemplateId: (int)p.template_id, Qty: (int)p.quantity,
                              X: (int)p.x, Y: (int)p.y))
                .OrderBy(x => x.TemplateId).ThenBy(x => x.Container).ThenBy(x => x.Y).ThenBy(x => x.X).ToList();

            // 인스턴스: 중첩 그리드 내용(배치=CONTAINER 원위치) + 장착 슬롯(백팩/리그 본체 포함, EQUIP 원위치).
            var instanceItems = placements.Where(p => (string)p.kind == "INSTANCE")
                .Select(p => (TemplateId: (int)p.template_id, InstanceId: (Guid)p.instance_id,
                              OriginContainer: (string)p.container, OriginCid: (Guid?)p.container_instance_id,
                              OriginSlot: (string?)null, OriginX: (int?)(int)p.x, OriginY: (int?)(int)p.y))
                .Concat(equipment.Select(e => (TemplateId: (int)e.template_id, InstanceId: (Guid)e.instance_id,
                              OriginContainer: "EQUIP", OriginCid: (Guid?)null,
                              OriginSlot: (string?)(string)e.slot, OriginX: (int?)null, OriginY: (int?)null)))
                .OrderBy(x => x.InstanceId).ToList();

            // 스태시 밖이 전부 비어 있으면(장비도 없고 주머니/컨테이너도 없으면) 반입할 것이 없다.
            // 장착 장비만 있어도(주머니가 비어 있어도) instanceItems가 equipment로 채워지므로 여기를 통과한다
            // ("장비를 착용했는데 출격이 안됨" 버그 수정 — 장비 유무만으로 반드시 출격 가능해야 한다).
            if (stackItems.Count == 0 && instanceItems.Count == 0)
                throw new DomainException(ErrorCode.RaidNothingToDeploy,
                    "반입할 아이템이 없습니다. 장비를 착용하거나 주머니에 물건을 채운 뒤 출격하세요.");

            // 3) 세션 생성. 출격 마감(deadline)을 now() + 제한시간으로 설정 — 초과 후 extract/loot 시
            //    탈출 실패=사망으로 정산한다(lazy expiry). death_chance_bps는 존 기본 floor에서 시작해
            //    (반입 리스크 상시화 — 0루팅 즉시 탈출도 무위험이 아님) loot마다 상승한다.
            var sessionId = Guid.NewGuid();
            await db.ExecuteAsync(
                @"INSERT INTO raid_session(id, player_id, status, deadline_at, zone, death_chance_bps)
                  VALUES (@sessionId, @playerId, 'ACTIVE', now() + make_interval(secs => @dur), @zone, @baseDeath)",
                new { sessionId, playerId, dur = raidDurationSeconds, zone = zone.ToString(),
                      baseDeath = ZoneConfig[zone].BaseDeathBps }, tx);

            // 4) 스택 반입: inventory_stack 차감(= 매도 에스크로 이동) + 스냅샷 + 원장 + 해당 배치 제거.
            foreach (var s in stackItems)
            {
                int templateId = s.TemplateId;
                int qty = s.Qty;
                var have = await db.ExecuteScalarAsync<int?>(
                    "SELECT quantity FROM inventory_stack WHERE player_id = @playerId AND template_id = @templateId FOR UPDATE",
                    new { playerId, templateId }, tx);
                if (have is null || have < qty)
                    throw new DomainException(ErrorCode.InsufficientQuantity, "반입 스택 수량이 인벤토리 보유량을 초과합니다.");
                await db.ExecuteAsync(
                    "UPDATE inventory_stack SET quantity = quantity - @qty WHERE player_id = @playerId AND template_id = @templateId",
                    new { qty, playerId, templateId }, tx);
                await db.ExecuteAsync(
                    @"INSERT INTO raid_session_item
                        (session_id, kind, template_id, instance_id, quantity, source,
                         origin_container, origin_container_instance_id, origin_slot, origin_x, origin_y)
                      VALUES (@sessionId, 'STACK', @templateId, NULL, @qty, 'BROUGHT',
                              @originContainer, @originCid, NULL, @originX, @originY)",
                    new { sessionId, templateId, qty, originContainer = s.Container, originCid = s.Cid, originX = s.X, originY = s.Y }, tx);
                await InsertItemLedgerAsync(db, tx, playerId, StashEntryKind.Stack, templateId, null, -qty, ItemLedgerReason.RaidBrought, sessionId);
                await db.ExecuteAsync(
                    @"DELETE FROM stash_placement WHERE player_id = @playerId AND container = @container AND kind = 'STACK'
                      AND template_id = @templateId AND container_instance_id IS NOT DISTINCT FROM @cid",
                    new { playerId, container = s.Container, templateId, cid = s.Cid }, tx);
            }

            // 5) 유니크 반입(장착 + 중첩 내용): owner=NULL + 스냅샷 + 원장 + 배치/장착 제거.
            foreach (var it in instanceItems)
            {
                int templateId = it.TemplateId;
                Guid instanceId = it.InstanceId;
                var owner = await db.ExecuteScalarAsync<Guid?>(
                    "SELECT owner_player_id FROM item_instance WHERE id = @instanceId FOR UPDATE",
                    new { instanceId }, tx);
                if (owner != playerId)
                    throw new DomainException(ErrorCode.InstanceNotOwned, "위험 유니크 아이템의 소유권이 유효하지 않습니다.");
                await db.ExecuteAsync(
                    "UPDATE item_instance SET owner_player_id = NULL WHERE id = @instanceId",
                    new { instanceId }, tx);
                await db.ExecuteAsync(
                    @"INSERT INTO raid_session_item
                        (session_id, kind, template_id, instance_id, quantity, source,
                         origin_container, origin_container_instance_id, origin_slot, origin_x, origin_y)
                      VALUES (@sessionId, 'INSTANCE', @templateId, @instanceId, 1, 'BROUGHT',
                              @originContainer, @originCid, @originSlot, @originX, @originY)",
                    new
                    {
                        sessionId,
                        templateId,
                        instanceId,
                        originContainer = it.OriginContainer,
                        originCid = it.OriginCid,
                        originSlot = it.OriginSlot,
                        originX = it.OriginX,
                        originY = it.OriginY
                    }, tx);
                await InsertItemLedgerAsync(db, tx, playerId, StashEntryKind.Instance, templateId, instanceId, -1, ItemLedgerReason.RaidBrought, sessionId);
                if (it.OriginSlot is not null)
                    await db.ExecuteAsync(
                        "DELETE FROM player_equipment WHERE player_id = @playerId AND slot = @slot",
                        new { playerId, slot = it.OriginSlot }, tx);
                else
                    await db.ExecuteAsync(
                        "DELETE FROM stash_placement WHERE player_id = @playerId AND kind = 'INSTANCE' AND instance_id = @instanceId",
                        new { playerId, instanceId }, tx);
            }

            var dto = await LoadRaidDtoAsync(db, tx, sessionId, playerId, RaidStatus.Active);
            await tx.CommitAsync();
            return dto;
        }
        catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 부분 유니크 인덱스 위반(드문 동시 StartRaid 레이스) → 명확한 도메인 에러.
            await tx.RollbackAsync();
            throw new DomainException(ErrorCode.RaidActive, "이미 진행 중인 레이드가 있습니다.");
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Scavenge(한 트랜잭션, 서버 드롭테이블): 세션 존(zone)의 rarity 가중치로 무엇을·얼마나 드롭할지
    /// 서버가 결정해 ACTIVE 세션에 LOOTED 위험 아이템으로 추가한다(클라이언트는 아이템·수량을 못 정함 →
    /// 무한 인플레 차단). 스택은 스냅샷만(소유 인벤은 Extract 시 가산), 유니크는 item_instance를
    /// owner=NULL·origin=RAID로 즉시 생성한다. loot마다 존별 사망확률을 올린다("한 상자 더"의 대가).
    /// 마감(deadline)을 넘겼으면 탈출 실패=사망으로 정산하고 Dropped=null을 반환한다(lazy expiry).
    /// </summary>
    public async Task<LootResultDto> ScavengeAsync(Guid playerId)
    {
        var timing = await GetActiveRaidTimingAsync(playerId);
        if (timing is { Expired: true })
            return new LootResultDto(null, await ResolveRaidAsync(playerId, extracted: false));

        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            var sess = await db.QuerySingleOrDefaultAsync(
                "SELECT id, zone FROM raid_session WHERE player_id = @playerId AND status = 'ACTIVE' FOR UPDATE",
                new { playerId }, tx);
            if (sess is null)
                throw new DomainException(ErrorCode.RaidNotFound, "진행 중인 레이드가 없습니다.");
            Guid sessionId = (Guid)sess.id;
            var (weights, deathInc, _) = ZoneConfig[ParseZone((string)sess.zone)];

            // 존 가중치로 rarity 롤 → 그 rarity의 템플릿 중 랜덤 1종(무한생성 대신 서버 권위 드롭).
            var rarity = RollRarity(weights);
            var tpl = await db.QuerySingleAsync(
                @"SELECT id, stackable, max_durability, max_stack FROM item_template
                  WHERE rarity = @rarity ORDER BY random() LIMIT 1",
                new { rarity }, tx);
            int templateId = (int)tpl.id;
            bool stackable = (bool)tpl.stackable;
            int maxStack = (int)tpl.max_stack;

            RaidSessionItemDto dropped;
            if (stackable)
            {
                // 수량 = 1..max_stack(한 스택 상한 이내 — 서버가 보장하므로 상한 초과가 원천 불가).
                int qty = 1 + Random.Shared.Next(maxStack);
                await db.ExecuteAsync(
                    @"INSERT INTO raid_session_item(session_id, kind, template_id, instance_id, quantity, source)
                      VALUES (@sessionId, 'STACK', @templateId, NULL, @qty, 'LOOTED')",
                    new { sessionId, templateId, qty }, tx);
                dropped = new RaidSessionItemDto(StashEntryKind.Stack, templateId, null, qty, RaidItemSource.Looted);
            }
            else
            {
                var instanceId = Guid.NewGuid();
                var durability = (int?)tpl.max_durability;
                // 위험 상태의 유니크(owner=NULL) 즉시 생성 — origin=RAID로 프로버넌스 표시.
                await db.ExecuteAsync(
                    @"INSERT INTO item_instance(id, template_id, owner_player_id, durability, attachments, origin)
                      VALUES (@instanceId, @templateId, NULL, @durability, '[]'::jsonb, 'RAID')",
                    new { instanceId, templateId, durability }, tx);
                await db.ExecuteAsync(
                    @"INSERT INTO raid_session_item(session_id, kind, template_id, instance_id, quantity, source)
                      VALUES (@sessionId, 'INSTANCE', @templateId, @instanceId, 1, 'LOOTED')",
                    new { sessionId, templateId, instanceId }, tx);
                dropped = new RaidSessionItemDto(StashEntryKind.Instance, templateId, instanceId, 1, RaidItemSource.Looted);
            }

            // loot 성공 — 존별 사망확률 상승(extract에서 이 확률로 사망 롤).
            await db.ExecuteAsync(
                "UPDATE raid_session SET death_chance_bps = death_chance_bps + @inc WHERE id = @sessionId",
                new { inc = deathInc, sessionId }, tx);

            var session = await LoadRaidDtoAsync(db, tx, sessionId, playerId, RaidStatus.Active);
            await tx.CommitAsync();
            return new LootResultDto(dropped, session);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>ACTIVE 세션의 만료 여부(deadline 초과)와 누적 사망확률(bps)을 읽는다. 없으면 null.
    /// 만료 판정은 DB now() 기준으로 해 앱-DB 시계차를 피한다.</summary>
    private async Task<(bool Expired, int ChanceBps)?> GetActiveRaidTimingAsync(Guid playerId)
    {
        await using var db = Open();
        var r = await db.QuerySingleOrDefaultAsync(
            @"SELECT (deadline_at IS NOT NULL AND deadline_at < now()) AS expired, death_chance_bps
              FROM raid_session WHERE player_id = @playerId AND status = 'ACTIVE'",
            new { playerId });
        if (r is null) return null;
        return ((bool)r.expired, (int)r.death_chance_bps);
    }

    /// <summary>
    /// Extract 시도: 마감(deadline)을 넘겼으면 탈출 실패=사망으로 정산한다. 아니면 누적 사망확률로
    /// 롤 — 성공하면 EXTRACTED(반입+획득 전량 소유·원위치 복원), 실패하면 DIED(전량 소실).
    /// chance=0이면 항상 생존, chance≥100%면 항상 사망(경계는 결정론적). "한 상자 더 vs 지금 탈출" 도박.
    /// 생존 시 RAID_EXTRACT(반입)/RAID_LOOT(획득) 기록.
    /// </summary>
    public async Task<RaidSessionDto> ExtractAsync(Guid playerId)
    {
        var t = await GetActiveRaidTimingAsync(playerId);
        if (t is null) return await ResolveRaidAsync(playerId, extracted: true); // 없으면 ResolveRaid가 RaidNotFound
        bool died = t.Value.Expired || Random.Shared.NextDouble() < t.Value.ChanceBps / 10000.0;
        return await ResolveRaidAsync(playerId, extracted: !died);
    }

    /// <summary>
    /// Die(한 트랜잭션): ACTIVE→DIED. 위험 아이템 전량 소실(스택 미복귀 / 유니크 tombstone: owner=NULL,
    /// origin=RAID_LOST). 스태시(안전)는 무관. 소실은 item_ledger에 별도 기록하지 않는다(M4 대칭화 —
    /// 반입분은 RaidBrought에서 이미 debit, 전리품은 credit이 없음). 손실 감사는 raid_session(DIED)+raid_session_item.
    /// </summary>
    public async Task<RaidSessionDto> DieAsync(Guid playerId)
        => await ResolveRaidAsync(playerId, extracted: false);

    private async Task<RaidSessionDto> ResolveRaidAsync(Guid playerId, bool extracted)
    {
        await using var db = Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            var session = await db.QuerySingleOrDefaultAsync(
                @"SELECT id FROM raid_session
                  WHERE player_id = @playerId AND status = 'ACTIVE' FOR UPDATE",
                new { playerId }, tx);
            if (session is null)
                throw new DomainException(ErrorCode.RaidNotFound, "진행 중인 레이드가 없습니다.");
            Guid sessionId = (Guid)session.id;

            var items = (await db.QueryAsync(
                @"SELECT kind, template_id, instance_id, quantity, source,
                         origin_container, origin_container_instance_id, origin_slot, origin_x, origin_y
                  FROM raid_session_item WHERE session_id = @sessionId ORDER BY id",
                new { sessionId }, tx)).ToList();

            foreach (var it in items)
            {
                var kind = Enums.ToStashKind((string)it.kind);
                var source = Enums.ToRaidSource((string)it.source);
                int templateId = (int)it.template_id;
                Guid? instanceId = (Guid?)it.instance_id;
                int qty = (int)it.quantity;

                if (extracted)
                {
                    var reason = source == RaidItemSource.Looted ? ItemLedgerReason.RaidLoot : ItemLedgerReason.RaidExtract;
                    if (kind == StashEntryKind.Stack)
                    {
                        await UpsertStackAsync(db, tx, playerId, templateId, qty);
                        await InsertItemLedgerAsync(db, tx, playerId, kind, templateId, null, qty, reason, sessionId);
                    }
                    else
                    {
                        // 반입/획득 유니크 모두 owner를 플레이어로 복원(획득분은 이미 origin=RAID).
                        await db.ExecuteAsync(
                            "UPDATE item_instance SET owner_player_id = @playerId WHERE id = @instanceId",
                            new { playerId, instanceId }, tx);
                        await InsertItemLedgerAsync(db, tx, playerId, kind, templateId, instanceId, 1, reason, sessionId);
                    }
                }
                else // died: 소실.
                {
                    // item_ledger는 소유량 프로버넌스 로그다 — sum(delta_qty) == 실제 소유량이 불변식.
                    // 사망 시 소유량 변화는 0이므로 RaidLoss를 남기지 않는다(M4, 대칭화):
                    //   - 반입분: 출격 RaidBrought(-)에서 이미 debit됨. 미복귀=영구 손실이 그 debit으로 이미 표현됨
                    //     → 재차감하면 이중차감(유령 음수).
                    //   - 전리품(LOOTED): 인벤에 들어온 적 없어 사전 credit이 없음 → RaidLoss(-)만 남기면 유령 음수.
                    // "무엇을 잃었나"의 감사는 raid_session(status=DIED)+raid_session_item이 보유한다.
                    if (kind == StashEntryKind.Instance)
                    {
                        // tombstone: owner=NULL 유지 + origin=RAID_LOST(FK 안전한 소각 — 삭제 대신 표식).
                        await db.ExecuteAsync(
                            "UPDATE item_instance SET owner_player_id = NULL, origin = 'RAID_LOST' WHERE id = @instanceId",
                            new { instanceId }, tx);
                    }
                    // 반입 스택은 StartRaid에서 이미 인벤 차감됨(미복귀 = 소실) — 추가 물리/원장 처리 없음.
                }
            }

            // Extract(생존): 소유 복원에 더해 원위치로 복원한다(스태시 자동 덤프가 아님).
            //   BROUGHT은 스냅샷한 정확한 위치(주머니 칸/장착 슬롯/백팩·리그 내부)로,
            //   LOOTED은 반입 공간(백팩·리그 중첩 → 주머니 → STASH 오버플로 순)에 first-fit으로.
            if (extracted)
            {
                var stashRows = await db.ExecuteScalarAsync<int>(
                    "SELECT stash_rows FROM player WHERE id = @playerId", new { playerId }, tx);
                await RestoreExtractedPlacementsAsync(db, tx, playerId, items, stashRows);
            }

            var newStatus = extracted ? RaidStatus.Extracted : RaidStatus.Died;
            await db.ExecuteAsync(
                "UPDATE raid_session SET status = @status, resolved_at = now() WHERE id = @sessionId",
                new { status = newStatus.ToDb(), sessionId }, tx);

            var dto = await LoadRaidDtoAsync(db, tx, sessionId, playerId, newStatus);
            await tx.CommitAsync();
            return dto;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>정합화용 배치 스크래치(메모리 점유 계산). Quantity는 스택 합산으로 갱신 가능.</summary>
    private sealed class PlacementScratch(
        string container, Guid? cid, string kind, int templateId, Guid? instanceId, int x, int y, int quantity)
    {
        public string Container { get; } = container;
        public Guid? Cid { get; } = cid;
        public string Kind { get; } = kind;
        public int TemplateId { get; } = templateId;
        public Guid? InstanceId { get; } = instanceId;
        public int X { get; } = x;
        public int Y { get; } = y;
        public int Quantity { get; set; } = quantity;
    }

    /// <summary>
    /// Extract 원위치 복원(같은 트랜잭션). 소유는 이미 복원된 상태이고 여기서 물리 위치를 복원한다.
    ///   1) BROUGHT: StartRaid에서 스냅샷한 origin으로 정확히 복원 — EQUIP은 player_equipment,
    ///      POCKETS/CONTAINER는 stash_placement의 원래 칸(스태시 자동 덤프가 아님).
    ///   2) LOOTED: 원위치가 없으므로 반입 공간에 first-fit 배치한다 —
    ///      <b>장착된 백팩/리그의 중첩 그리드(슬롯 순) → POCKETS(4×1) → STASH(12×stashRows) 오버플로</b> 순.
    ///      어디에도 안 들어가면 미배치로 남고(소유 유지), 다음 GET /api/stash에서 STASH로 정합화된다.
    /// </summary>
    private static async Task RestoreExtractedPlacementsAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, Guid playerId, List<dynamic> items, int stashRows)
    {
        // 1) BROUGHT 원위치 복원.
        foreach (var it in items)
        {
            if (Enums.ToRaidSource((string)it.source) != RaidItemSource.Brought) continue;
            var originContainer = (string?)it.origin_container;
            if (originContainer is null) continue; // 방어(BROUGHT은 항상 origin 보유)
            var kind = Enums.ToStashKind((string)it.kind);
            int templateId = (int)it.template_id;
            Guid? instanceId = (Guid?)it.instance_id;

            if (originContainer == "EQUIP")
            {
                await db.ExecuteAsync(
                    "INSERT INTO player_equipment(player_id, slot, instance_id) VALUES (@playerId, @slot, @instanceId)",
                    new { playerId, slot = (string?)it.origin_slot, instanceId }, tx);
                continue;
            }

            Guid? cid = (Guid?)it.origin_container_instance_id;
            int x = (int)it.origin_x, y = (int)it.origin_y;
            if (kind == StashEntryKind.Stack)
                await db.ExecuteAsync(
                    @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
                      VALUES (@playerId, @container, 'STACK', @templateId, NULL, @cid, @x, @y, @qty)",
                    new { playerId, container = originContainer, templateId, cid, x, y, qty = (int)it.quantity }, tx);
            else
                await db.ExecuteAsync(
                    @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
                      VALUES (@playerId, @container, 'INSTANCE', @templateId, @instanceId, @cid, @x, @y, 1)",
                    new { playerId, container = originContainer, templateId, instanceId, cid, x, y }, tx);
        }

        // 2) LOOTED 반입 공간 배치.
        var looted = items.Where(it => Enums.ToRaidSource((string)it.source) == RaidItemSource.Looted).ToList();
        if (looted.Count == 0) return;

        // 카탈로그 footprint + max_stack + 중첩 컨테이너 크기.
        var templates = (await db.QueryAsync(
            "SELECT id, grid_w, grid_h, is_container, container_w, container_h, max_stack FROM item_template", null, tx)).ToList();
        var footprints = templates.ToDictionary(t => (int)t.id, t => ((int)t.grid_w, (int)t.grid_h));
        var maxStacks = templates.ToDictionary(t => (int)t.id, t => (int)t.max_stack);
        var containerDims = templates.Where(t => (bool)t.is_container)
            .ToDictionary(t => (int)t.id, t => ((int)t.container_w, (int)t.container_h));

        // 장착된 백팩/리그(중첩 컨테이너) — 슬롯 순. 배치 우선순위의 앞쪽.
        var equipped = (await db.QueryAsync(
            @"SELECT pe.slot, pe.instance_id, ii.template_id
              FROM player_equipment pe JOIN item_instance ii ON ii.id = pe.instance_id
              WHERE pe.player_id = @playerId ORDER BY pe.slot",
            new { playerId }, tx)).ToList();

        // 배치 대상 컨테이너 우선순위: 중첩(백팩/리그, 슬롯 순) → POCKETS(4×1) → STASH(오버플로).
        var targets = new List<(string Container, Guid? Cid, int W, int H)>();
        foreach (var e in equipped)
            if (containerDims.TryGetValue((int)e.template_id, out var dims))
                targets.Add(("CONTAINER", (Guid)e.instance_id, dims.Item1, dims.Item2));
        targets.Add(("POCKETS", null, StashGeometry.PocketsWidth, StashGeometry.PocketsHeight));
        targets.Add(("STASH", null, StashGeometry.StashWidth, stashRows));

        // 현재 배치(방금 복원한 BROUGHT + 남아있던 안전 배치)를 메모리로 로드해 점유 계산에 쓴다.
        var placements = (await db.QueryAsync(
            @"SELECT container, kind, template_id, instance_id, container_instance_id, x, y, quantity
              FROM stash_placement WHERE player_id = @playerId",
            new { playerId }, tx))
            .Select(p => new PlacementScratch(
                (string)p.container, (Guid?)p.container_instance_id, (string)p.kind,
                (int)p.template_id, (Guid?)p.instance_id, (int)p.x, (int)p.y, (int)p.quantity))
            .ToList();

        static bool SameTarget(PlacementScratch p, (string Container, Guid? Cid, int W, int H) t)
            => p.Container == t.Container && p.Cid == t.Cid;

        foreach (var it in looted)
        {
            var kind = Enums.ToStashKind((string)it.kind);
            int templateId = (int)it.template_id;
            Guid? instanceId = (Guid?)it.instance_id;
            int qty = (int)it.quantity;
            var (w, h) = kind == StashEntryKind.Stack ? (1, 1) : footprints.GetValueOrDefault(templateId, (1, 1));

            if (kind == StashEntryKind.Instance)
            {
                // 유니크는 항상 통째로 한 칸에.
                if (!TryPlaceInAnyTarget(w, h, out var fx, out var fy, out var chosen)) continue;
                await db.ExecuteAsync(
                    @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
                      VALUES (@playerId, @container, 'INSTANCE', @templateId, @instanceId, @cid, @x, @y, 1)",
                    new { playerId, container = chosen.Container, templateId, instanceId, cid = chosen.Cid, x = fx, y = fy }, tx);
                placements.Add(new PlacementScratch(chosen.Container, chosen.Cid, "INSTANCE", templateId, instanceId, fx, fy, 1));
                continue;
            }

            // 스택은 max_stack 단위로 쪼개 배치한다(다중 스택 — 한 칸이 상한을 넘지 않게).
            var maxStack = maxStacks.GetValueOrDefault(templateId, 1);
            var remaining = qty;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, maxStack);
                var placedChunk = false;

                foreach (var t in targets)
                {
                    // 같은 물리 컨테이너에 동일 템플릿 칸이 있으면 여유분만큼 병합(초과분은 다음 target으로).
                    var merge = placements.FirstOrDefault(p => p.Kind == "STACK" && p.TemplateId == templateId && SameTarget(p, t));
                    if (merge is not null)
                    {
                        var room = maxStack - merge.Quantity;
                        if (room <= 0) continue;
                        var add = Math.Min(room, chunk);
                        await db.ExecuteAsync(
                            @"UPDATE stash_placement SET quantity = quantity + @add
                              WHERE player_id = @playerId AND container = @container AND kind = 'STACK'
                                AND template_id = @templateId AND container_instance_id IS NOT DISTINCT FROM @cid
                                AND x = @x AND y = @y",
                            new { add, playerId, container = t.Container, templateId, cid = t.Cid, merge.X, merge.Y }, tx);
                        merge.Quantity += add;
                        remaining -= add;
                        placedChunk = true;
                        break;
                    }

                    var occupied = placements.Where(p => SameTarget(p, t)).Select(p =>
                    {
                        var (pw, ph) = p.Kind == "STACK" ? (1, 1) : footprints.GetValueOrDefault(p.TemplateId, (1, 1));
                        return new Rect(p.X, p.Y, pw, ph);
                    }).ToList();
                    var fit = StashGeometry.FirstFit(t.W, t.H, occupied, 1, 1);
                    if (fit is null) continue;
                    var (fx, fy) = fit.Value;

                    await db.ExecuteAsync(
                        @"INSERT INTO stash_placement(player_id, container, kind, template_id, instance_id, container_instance_id, x, y, quantity)
                          VALUES (@playerId, @container, 'STACK', @templateId, NULL, @cid, @x, @y, @chunk)",
                        new { playerId, container = t.Container, templateId, cid = t.Cid, x = fx, y = fy, chunk }, tx);
                    placements.Add(new PlacementScratch(t.Container, t.Cid, "STACK", templateId, null, fx, fy, chunk));
                    remaining -= chunk;
                    placedChunk = true;
                    break;
                }

                // 어느 target에도 이 칸을 놓을 자리가 없으면 남은 수량은 미배치로 남긴다
                // (소유는 이미 부여됨 — 다음 GET /api/stash의 정합화가 STASH에 자동 배치한다).
                if (!placedChunk) break;
            }

            bool TryPlaceInAnyTarget(int fw, int fh, out int fx, out int fy, out (string Container, Guid? Cid, int W, int H) chosen)
            {
                foreach (var t in targets)
                {
                    var occupied = placements.Where(p => SameTarget(p, t)).Select(p =>
                    {
                        var (pw, ph) = p.Kind == "STACK" ? (1, 1) : footprints.GetValueOrDefault(p.TemplateId, (1, 1));
                        return new Rect(p.X, p.Y, pw, ph);
                    }).ToList();
                    var fit = StashGeometry.FirstFit(t.W, t.H, occupied, fw, fh);
                    if (fit is null) continue;
                    (fx, fy) = fit.Value;
                    chosen = t;
                    return true;
                }
                fx = fy = 0;
                chosen = default;
                return false;
            }
        }
    }

    /// <summary>세션 + 위험 아이템 목록을 RaidSessionDto로 조립. started_at/resolved_at은
    /// 앱 시각을 넘겨받지 않고 DB에서 읽는다(단일 진실 소스) — AddLoot가 출격 시각 대신 loot
    /// 호출 시각을 반환하던 버그를 근본 제거하고, StartRaid/Resolve의 앱시각↔DB now() 불일치도 없앤다.</summary>
    private static async Task<RaidSessionDto> LoadRaidDtoAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, Guid sessionId, Guid playerId, RaidStatus status)
    {
        var s = await db.QuerySingleAsync(
            "SELECT started_at, resolved_at, deadline_at, death_chance_bps FROM raid_session WHERE id = @sessionId",
            new { sessionId }, tx);
        var items = (await db.QueryAsync(
            "SELECT kind, template_id, instance_id, quantity, source FROM raid_session_item WHERE session_id = @sessionId ORDER BY id",
            new { sessionId }, tx))
            .Select(r => new RaidSessionItemDto(
                Enums.ToStashKind((string)r.kind), (int)r.template_id, (Guid?)r.instance_id,
                (int)r.quantity, Enums.ToRaidSource((string)r.source))).ToList();
        // deadline_at·death_chance_bps는 ACTIVE 세션에서만 의미가 있다(해결된 세션은 null/무시).
        var deadlineAt = status == RaidStatus.Active ? (DateTimeOffset?)s.deadline_at : null;
        var deathChanceBps = status == RaidStatus.Active ? (int)s.death_chance_bps : 0;
        return new RaidSessionDto(sessionId, playerId, status,
            (DateTimeOffset)s.started_at, (DateTimeOffset?)s.resolved_at, items, deadlineAt, deathChanceBps);
    }

    /// <summary>
    /// 레이드 이력(해결된 세션: EXTRACTED/DIED)을 페이지네이션으로 조회. ACTIVE는 제외한다
    /// (진행 중 세션은 GET /api/raid로 본다). 각 세션의 아이템 스냅샷을 한 번의 쿼리로 묶어 붙인다(N+1 회피).
    /// </summary>
    public async Task<PagedResult<RaidHistoryEntryDto>> GetRaidHistoryAsync(Guid playerId, int page, int size)
    {
        await using var db = Open();
        var total = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM raid_session WHERE player_id = @playerId AND status IN ('EXTRACTED','DIED')",
            new { playerId });
        var sessions = (await db.QueryAsync(
            @"SELECT id, status, started_at, resolved_at
              FROM raid_session
              WHERE player_id = @playerId AND status IN ('EXTRACTED','DIED')
              ORDER BY started_at DESC, id DESC
              LIMIT @size OFFSET @offset",
            new { playerId, size, offset = (page - 1) * size })).ToList();

        var ids = sessions.Select(s => (Guid)s.id).ToArray();
        var itemsBySession = new Dictionary<Guid, List<RaidSessionItemDto>>();
        if (ids.Length > 0)
        {
            var itemRows = await db.QueryAsync(
                @"SELECT session_id, kind, template_id, instance_id, quantity, source
                  FROM raid_session_item WHERE session_id = ANY(@ids) ORDER BY id",
                new { ids });
            foreach (var r in itemRows)
            {
                var sid = (Guid)r.session_id;
                if (!itemsBySession.TryGetValue(sid, out var list))
                    itemsBySession[sid] = list = [];
                list.Add(new RaidSessionItemDto(
                    Enums.ToStashKind((string)r.kind), (int)r.template_id, (Guid?)r.instance_id,
                    (int)r.quantity, Enums.ToRaidSource((string)r.source)));
            }
        }

        var items = sessions.Select(s => new RaidHistoryEntryDto(
            (Guid)s.id, Enums.ToRaidStatus((string)s.status),
            (DateTimeOffset)s.started_at, (DateTimeOffset?)s.resolved_at,
            itemsBySession.GetValueOrDefault((Guid)s.id, []))).ToList();
        return new PagedResult<RaidHistoryEntryDto>(items, page, size, total);
    }
}

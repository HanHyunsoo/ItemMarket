# ONBOARDING — 이 코드베이스를 "오너십" 수준으로 파악하기

> **이 문서의 목적**: 이 저장소를 만든 사람이 세부를 완전히 체화하지 못한 상태에서, **3~4시간 안에
> 시니어 이직 면접의 꼬리질문을 코드 근거로 즉답**할 수 있게 만드는 **실습 주도** 가이드다.
>
> **읽지 말고 따라가라.** 각 구간은 "① 무엇을 배우나 → ② 직접 해보기(파일 열기·쿼리·테스트) →
> ③ 이게 답하는 면접 질문 → ④ 스스로 답해보기"로 되어 있다. 손으로 추적하고 눈으로 DB 행 변화를
> 봐야 남는다. 요약만 읽으면 30분 뒤 다 잊는다.
>
> **짝 문서**: 설계 근거·답변 스크립트는 [`docs/notes/interview-prep.md`](docs/notes/interview-prep.md),
> 시니어 예상 압박 질문+모범답안은 [`docs/notes/interview-hotseat.md`](docs/notes/interview-hotseat.md),
> 자체 감사(결함→수정→테스트)는 [`docs/backend-audit.md`](docs/backend-audit.md),
> 계약은 [`docs/api-contract.md`](docs/api-contract.md)·[`docs/realtime-contract.md`](docs/realtime-contract.md),
> 성능은 [`docs/perf-report.md`](docs/perf-report.md). 아키텍처 다이어그램은 [`README.md`](README.md).

## 사전 조건 / 스택 띄우기

- **.NET 10 SDK**(`~/.dotnet`), **Docker**, **Node 20+**, `jq`(시드용).
- 전체 스택(postgres+redis+api+web) 한 방:
  ```bash
  docker compose --profile app up -d --build
  #  web http://localhost:8081 · api http://localhost:8080 (/swagger)
  ```
  - **redis 6379 충돌**(다른 프로젝트가 점유) 시: `REDIS_PORT=6390 API_PORT=8080 WEB_PORT=8081 docker compose --profile app up -d --build`
  - **API가 redis보다 먼저 떠서 크래시**하면(로그에 `RedisConnectionException`): `docker compose --profile app restart api`
- DB 직접 보기: `docker exec -it item-market-db psql -U market -d item_market`
- 마켓 데이터 채우기(호가·체결 이력): `./scripts/seed-market.sh http://localhost:8080 && ./scripts/seed-trades.sh http://localhost:8080`
- 테스트(Docker만 있으면 Postgres 자동): `export PATH="$HOME/.dotnet:$PATH" && dotnet test` → **125개**(단위 41 + 통합 80 + 밴딩 4).
- 로그인은 **비밀번호 없음**: 우상단 인식표에서 플레이어 선택(웹) 또는 `POST /api/auth/login {"playerId":"…"}`. 데모 플레이어:
  `Survivor_Alpha`(11111111…), `Survivor_Bravo`(22222222…), `Trader_Charlie`(33333333…, **admin**),
  `Raider_Delta/Echo/Foxtrot`, `Gearhead_Golf/Hotel`. 시드 잔액: 대부분 10000, Charlie 50000 CAP.

---

## 0. 큰 그림 — 멘탈 모델 5개 (여기부터)

이 다섯 개만 확실히 잡으면 나머지는 파생이다.

1. **grain 단일 활성화 = 락 없는 직렬화.** Orleans는 grain 활성화를 클러스터 전체에서 **정확히 1개**로
   보장하고 기본 **non-reentrant**라 한 번에 한 요청만 처리한다. 그래서 `OrderBookGrain`(키=templateId)이
   그 종목의 매칭을 **단일 스레드처럼** 직렬화 → 이중 체결·경쟁이 락 코드 없이 사라진다.
2. **DB가 진실, 인메모리 호가창은 재구성 가능한 투영(projection).** 모든 자산 이동은 Postgres 트랜잭션으로
   커밋되고, 인메모리 `_book`은 활성화 시 DB에서 **재수화**되는 파생물. 실로가 죽어도 무손실.
3. **에스크로 + 단일 트랜잭션 정산.** 주문 시점에 자산을 잠그고(매수=캡, 매도=재고/인스턴스), 체결은
   `SettleFillAsync` **한 트랜잭션**에서 all-or-nothing. 부하 후 SQL 불변식으로 보존 증명.
4. **at-risk = 스태시 밖 자산을 "매도 에스크로"로 재사용해 잠금.** 레이드 출격은 새 메커니즘이 아니라
   장비·주머니·중첩 아이템을 매도 에스크로와 같은 방식(스택 차감/인스턴스 owner=NULL)으로 잠그는 것.
5. **아이템은 grain이 아니라 DB 행.** `ItemGrain`은 없다. grain은 **사람 단위**(Wallet/Inventory/Stash/Raid,
   키=playerId)와 **종목 단위**(OrderBook, 키=templateId)뿐. 아이템(탄약 수백만 개)을 grain으로 만들면
   활성화가 폭발하니 안 만든다. `item_instance`(유니크)·`inventory_stack`(스택)·`stash_placement`(그리드 위치) 행.

**grain 6종 (전부 얇음, `WalletGrain` 빼면 무상태 패스스루가 다수):**

| grain | 파일 | 키 | 역할 |
|---|---|---|---|
| `OrderBookGrain` | `Grains/OrderBookGrain.cs` | templateId | 매칭 엔진(또는 밴딩 ON 시 코디네이터) |
| `OrderBandGrain` | `Grains/OrderBandGrain.cs` | `"{tid}:{band}"` | (opt-in) 가격밴드 샤딩 |
| `WalletGrain` | `Grains/WalletGrain.cs` | playerId | 지갑(에스크로/환불/조정) — 상태 캐시 X |
| `PlayerInventoryGrain` | `Grains/PlayerInventoryGrain.cs` | playerId | 인벤(스택/인스턴스 에스크로) |
| `StashGrain` | `Grains/StashGrain.cs` | playerId | 그리드/장비 이동 직렬화 |
| `RaidSessionGrain` | `Grains/RaidSessionGrain.cs` | playerId | 레이드 세션 상태머신 |

> `OrderBookEngine`(`Grains/OrderBookEngine.cs`)은 **grain이 아니다** — 매칭·에스크로·정산·재수화 로직을
> 담은 내부 헬퍼 클래스. `OrderBookGrain`(밴딩 OFF)이 소유·호출한다. **동시성 스토리의 핵심 코드.**

**리포지토리는 partial 3분할** (같은 `MarketRepository` 클래스):
`Data/MarketRepository.cs`(코어: 지갑·에스크로·주문·`SettleFillAsync`·리더보드·티커·벤더·리프레시·체결),
`Data/MarketRepository.Stash.cs`(배치/이동/장비), `Data/MarketRepository.Raid.cs`(드롭테이블·세션·재수화).

**스스로 답해보기**: (a) 왜 아이템을 grain으로 안 만들었나? (b) "grain 단일 활성화가 정합성을 준다"는
말은 어디까지 맞나(힌트: 2번 구간에서 답이 갈린다)? (c) 인메모리 호가창이 DB와 어긋나면 어떻게 복구되나?

---

## 1. 띄우고 관찰 (감을 먼저 잡는다 · ~30분)

스택을 띄우고 **주문 하나가 DB를 어떻게 바꾸는지 눈으로** 본다.

**직접 해보기:**
1. 스택 기동 + 시드(위 사전조건). 웹 http://localhost:8081 에서 `Survivor_Alpha`로 로그인.
2. 마켓 카드 클릭 → 아이템 상세 → **호가창의 빨간 ASK 가격을 클릭**(→ 매수·그 가격으로 폼 자동 채움) → 주문.
3. psql에서 방금 무슨 일이 났는지 확인:
   ```sql
   -- 방금 체결(가장 최근)
   SELECT template_id, unit_price, quantity, fee_amount, buyer_id, seller_id
   FROM trade ORDER BY executed_at DESC LIMIT 3;
   -- 그 종목 미체결 주문(호가창의 진짜 소스)
   SELECT side, unit_price, remaining_quantity, status FROM market_order
   WHERE template_id = 95 AND status IN ('OPEN','PARTIALLY_FILLED') ORDER BY created_at;
   -- 매수자 지갑 원장(에스크로 잠금 → 체결 시 차익 환불)
   SELECT delta, balance_after, reason, ref_id FROM wallet_ledger
   WHERE player_id = '11111111-1111-1111-1111-111111111111' ORDER BY id DESC LIMIT 5;
   ```
   - **기대**: `trade`에 1행 추가, `market_order`의 매도 잔량 감소(또는 FILLED로 사라짐), `wallet_ledger`에
     `ORDER_ESCROW`(−) 후 체결 시 `ORDER_REFUND`(+차익). 판매자 쪽엔 `TRADE_PROCEEDS`(+)·`FEE`(−).
4. **① 배우는 것**: "호가창"은 UI 위젯이 아니라 `market_order` 테이블의 투영이고, 돈 흐름은 전부
   `wallet_ledger`(append-only)에 남는다. 이게 멘탈 모델 2·3의 실물.

**이게 답하는 면접 질문**: [hotseat A3](docs/notes/interview-hotseat.md#a3-인메모리-북이-투영이라며--commit-후반영-전-죽으면)(투영),
[interview-prep 3장](docs/notes/interview-prep.md)(돈 보존).

**스스로 답해보기**: 매수 상한가 250에 최우선 매도 210이 있으면, 체결가는 얼마고 매수자에게 뭐가 돌아가나?

---

## 2. 요청 하나 추적 — `POST /api/orders` (★ 핵심 · ~60분)

주문 하나가 지나는 길을 **파일·함수 순서로** 따라간다. 각 단계에서 해당 파일을 열어라.

| # | 어디 | 무엇을 하나 | ① 배우는 것 |
|---|---|---|---|
| 1 | `Api/Endpoints/OrderEndpoints.cs` `MapPost("/orders")` | `CurrentPlayer(u)`로 토큰의 sub에서 플레이어 식별 → `gf.GetGrain<IOrderBookGrain>(templateId).PlaceOrder(...)`. `Idempotency-Key` 있으면 `ExecIdempotent`로 감쌈. | 엔드포인트는 얇다. 클라가 보낸 id는 안 믿고 토큰만 신뢰. |
| 2 | `Grains/OrderBookGrain.cs` `PlaceOrder` | 밴딩 OFF면 `_engine.PlaceOrderAsync`로 위임(기본). ON이면 밴드 계산해 `OrderBandGrain`으로 라우팅. | grain은 단일 활성화라 이 지점이 종목별 직렬화 경계. |
| 3 | `Grains/OrderBookEngine.cs` `PlaceOrderAsync` — **검증** | 수량/단가 상한, **총액을 `Int128`로 계산**해 `MaxNotional` 초과 거부(오버플로=음수 에스크로=무한발행 차단). | 병뚜껑 무한발행 취약점을 막은 자리(STAR ①). |
| 4 | 같은 함수 — **에스크로** | 매수: `WalletGrain.TryEscrow(notional)`. 스택 매도: `PlayerInventoryGrain.TryEscrowStack`. 유니크 매도: `TryEscrowInstance`. 실패 시 `InsufficientFunds`/`InsufficientQuantity`. | 매칭 전에 **자산을 먼저 잠근다** — 이중판매/복제 불가. |
| 5 | 같은 함수 — **주문 영속화** | `repo.InsertOrderAsync(OPEN, 전량)`. **catch 블록 주목**: INSERT가 예외인데 실제 커밋됐을 수 있어(L9a) `OrderExists`로 멱등 확인 후에만 보상 환불(이중환불 방지). | 에스크로 커밋 ↔ INSERT는 **별개 트랜잭션** → 고아 에스크로 창(감사 disclosed). |
| 6 | `MatchAsync` | 반대편 호가를 **가격-시간 우선**으로 정렬, 교차하는 만큼 체결. **체결가=메이커 가격**(테이커는 차익). `maker.PlayerId==incoming.PlayerId`면 **자전거래 스킵**. 각 체결마다 `SettleFillAsync`. | 실제 매칭 알고리즘. 정산 성공 후에만 인메모리 반영(선반영 금지). |
| 7 | `MarketRepository.cs` `SettleFillAsync` | **단일 트랜잭션 정산**(→ 3구간에서 정독). | 돈·아이템 원자 이동. |
| 8 | 다시 `OrderEndpoints` | `PlaceOrder` 반환 후 `notifier.PublishOrderActivityAsync(GetSnapshot(), pid, fills)`. | 실시간 발행은 **트랜잭션 밖 best-effort**(→ hotseat 7장). |

**직접 해보기**: `OrderBookEngine.cs`를 열고 `PlaceOrderAsync` → `MatchAsync`를 한 번 통독하라. 특히 260~296줄
"커밋 성공 후에만 인메모리 반영" 주석과, 정산 실패 시 `deactivateOnIdle()` 후 rethrow(→ 다음 활성화 재수화)를 봐라.

**이게 답하는 면접 질문**: [hotseat A1](docs/notes/interview-hotseat.md)(DB 핫패스), [hotseat A2](docs/notes/interview-hotseat.md)(에스크로 vs 정산),
[hotseat 5번/A5](docs/notes/interview-hotseat.md)(고아 에스크로).

**스스로 답해보기**: (a) 왜 매칭 전에 에스크로부터 하나? (b) INSERT가 커밋됐는데 예외가 왔을 때 그냥
환불하면 뭐가 잘못되나? (c) 정산이 실패하면 인메모리 호가창은 어떻게 되나?

---

## 3. 정산 정독 — `SettleFillAsync` (~40분)

`MarketRepository.cs`의 `SettleFillAsync`를 한 구간씩. 여기가 "돈이 안 샌다"의 심장.

계산값(함수 상단):
- `gross = ExecPrice × Quantity` (판매 총액)
- `fee = CalcFee(ExecPrice, Quantity, FeeBps)` (Int128 안전, 소각)
- `improvement = (BuyLimitPrice − ExecPrice) × Quantity` (매수자 차익)
- `releasedEscrow = BuyLimitPrice × Quantity` (이 체결분에 잠겼던 매수 에스크로)

트랜잭션 순서:
0. **락 순서 선점** — 이 정산이 건드릴 매수·매도 지갑을 **player_id 오름차순으로 정렬해 한 행씩
   `SELECT 1 FROM wallet WHERE player_id=@pid FOR UPDATE`**로 잠근다. 서로 다른 종목 grain의 동시
   정산이 두 지갑을 **다른 순서로** 잠가 생기던 데드락(40P01)을 일관된 락 순서로 제거(STAR ②).
   (과거엔 `WHERE player_id = ANY(@ids) ORDER BY player_id FOR UPDATE` 한 문장이었으나, Postgres
   행 락이 정렬 노드 이전 스캔에서 잡혀 플랜 의존이라 → C# 정렬 + 개별 문장으로 하드닝.)
1. `trade` INSERT.
2. 판매자: `CreditWalletAsync(+gross, TRADE_PROCEEDS)` 후 `if fee>0: CreditWalletAsync(−fee, FEE)`.
3. 매수자: `if improvement>0: CreditWalletAsync(+improvement, ORDER_REFUND)`.
4. 아이템 이전: 스택은 `UpsertStackAsync`(수량 가산), 유니크는 `item_instance.owner_player_id` 갱신.
5·6. 매수·매도 주문 갱신 — **낙관적 가드** `WHERE remaining_quantity = @before AND status IN(OPEN,PARTIALLY_FILLED)`.
   `rows != 1`이면 `OrderAlreadyClosed`로 **롤백**. 드문 이중 활성화가 나도 **DB가 최종 심판**.

**손으로 검산 (보존식)** — 매수 상한가 250, 최우선 매도(메이커) 210, 수량 4, 수수료 5%:
```
gross          = 210 × 4 = 840   → 판매자 +840
fee            = 840 × 5% = 42   → 판매자 −42   (소각)
improvement    = (250−210) × 4 = 160 → 매수자 +160 환불
releasedEscrow = 250 × 4 = 1000
검증: gross(840, 판매자로) + improvement(160, 매수자 환불로) = 1000 = releasedEscrow  ✅ 캡 보존
     매수자 순지출 = 1000(에스크로) − 160(환불) = 840,  판매자 순수령 = 840 − 42 = 798,  소각 = 42
     840 = 798 + 42  ✅  (발행 없음, 수수료만 sink)
```

**직접 해보기**: `CreditWalletAsync`(코어 파일)를 찾아 읽어라 — "UPDATE balance RETURNING + 원장 기록"을
delta 부호로 통일한 헬퍼. 정산·환불·벤더가 공유. (최근 리팩토링으로 중복 제거됨.)

**이게 답하는 면접 질문**: [hotseat A2](docs/notes/interview-hotseat.md), [interview-prep STAR ②](docs/notes/interview-prep.md)(데드락),
[backend-audit 병뚜껑 무한발행](docs/backend-audit.md).

**스스로 답해보기**: (a) 낙관적 가드가 막는 시나리오를 하나 말해봐라. (b) 정수 절삭 수수료가 값을 새게
하지 않는 이유는? (c) 왜 두 지갑을 player_id 순서로 정렬해 잠그나?

---

## 4. 크래시 · 재수화 (~30분)

멘탈 모델 2를 코드·테스트로 확정한다.

**직접 해보기:**
1. `Grains/OrderBookGrain.cs` `OnActivateAsync` → `new OrderBookEngine(...)` → `RehydrateAsync(await repo.GetLiveOrdersAsync(TemplateId))`.
   즉 활성화 때마다 DB의 미체결 주문으로 인메모리 `_book`을 다시 쌓는다.
2. `tests/ItemMarket.IntegrationTests/CrashRecoveryTests.cs` 두 테스트를 읽어라:
   - `OrderBook_rehydrates_from_db_after_deactivation_and_keeps_matching`: 대기 매도 등록 →
     `IManagementGrain.ForceActivationCollection`으로 **강제 비활성화** → 비활성 상태에서 **DB에만 직접**
     매도(@999) INSERT → GET book이 재활성화하며 재수화 → **그 @999가 스냅샷에 나타남**(살아있었으면
     인메모리에 없어 안 보일 주문). = **재수화의 명백한 증거** + 교차 매수 체결 + 돈 보존.
   - `DomainException_roundtrips_through_orleans_serializer_preserving_code`: `DomainException`을 Orleans
     `Serializer`로 직접 라운드트립해 `Code`·`Message` 보존 검증(=M1 수정, 멀티실로에서 예외 Code 유실→500 차단).

**관련 갭(먼저 인정)**: 에스크로 커밋 ↔ 주문 INSERT가 별개 트랜잭션이라 하드 크래시 시 고아 에스크로 창이
남는다(보상으로 완화, 완전 해결은 동일 Tx/outbox + 리컨실 잡). [backend-audit](docs/backend-audit.md) 참고.

**이게 답하는 면접 질문**: [hotseat A3·A5](docs/notes/interview-hotseat.md), [interview-prep "서버가 죽으면?"](docs/notes/interview-prep.md).

**스스로 답해보기**: (a) COMMIT 직후·인메모리 반영 직전에 죽으면 그 체결은 유실되나? (b) 재수화가 실제로
됐다는 걸 그 테스트는 어떻게 "증명"하나(단순히 '책이 그대로 있네'가 아니라)?

---

## 5. 레이드 · 스태시 루프 (~40분)

거래소에 수요·공급을 만드는 경제 엔진 + 그리드. `MarketRepository.Raid.cs`, `MarketRepository.Stash.cs`, `Grains/StashGrain.cs`.

**레이드 (`RaidSessionGrain` 키=playerId → `MarketRepository.Raid.cs`):**
- 상태머신: `StartRaid → Scavenge(loot) → Extract / Die → ResolveRaidAsync(extracted)`. 존 `Scav/Low/Med/High`가
  드롭 rarity 가중치·loot당 사망확률·수수료를 결정. 플레이어당 ACTIVE 1개(`uq_raid_active` 부분 유니크 인덱스).
- **at-risk = 스태시 밖 전부**(장착 장비 + 주머니 + 백팩·리그 내용물). 출격은 이걸 **매도 에스크로와 같은
  방식**으로 잠근다(스택 차감 / 인스턴스 `owner=NULL`) + 원위치를 스냅샷. **탈출=제자리 복원 + 전리품 귀속**,
  **사망=at-risk 전량 소각, 스태시는 불가침**. 각 전이 단일 Postgres 트랜잭션.
- **교차 grain 락**: `StartRaid`와 스태시 이동은 **다른 grain 타입**(RaidSession vs Stash, 둘 다 키=playerId)이라
  Orleans 단일 활성화만으로 직렬화 안 됨 → `LockPlayerAsync` = `pg_advisory_xact_lock(hashtextextended(key,0))`
  + `ThrowIfActiveRaidAsync`로 TOCTOU를 닫음(F-1/F-1b). **"grain 경계 = 정합성 경계"가 깨지는 실제 사례.**

**스태시 그리드 (`StashGrain` 키=playerId):**
- 이동/배치마다 서버가 `EnsureNoOverlap`으로 **AABB 겹침**(`StashGeometry.Overlaps`) + 경계 검사 → 겹치면
  `PlacementInvalid`(400), 상태 변화 0. `StashGrain`은 무상태(데이터는 DB, 존재 이유는 플레이어별 변이 직렬화).
- DB 백스톱: `uq_stash_cell(player_id, container, COALESCE(cinst,센티넬), x, y)` — **좌상단 한 칸만** 보장.
  다중 셀 footprint의 내부 칸 겹침은 **앱 AABB만** 잡는다(그래서 앱 검사가 필수 방어).
- **알려진 갭(BUG1)**: `EnsureNoOverlap`이 **스택형을 (1,1)로 하드코딩** — 현재 시드 스택형이 전부 1×1이라
  무해하나, >1×1 스택형 추가 시 좌상단만 검사. 유니크는 실제 footprint 사용(정상).

**직접 해보기**: `StashGrain.cs`의 `EnsureNoOverlap`(240줄대)과 `StashGeometry.Overlaps`를 읽고, 4×4
아이템 안쪽 칸에 1×1을 놓을 때 AABB가 왜 잡는지 손으로 확인. `RaidTests.cs`에서 `Extract`가 원위치로
복원하는 테스트를 하나 읽어라.

**이게 답하는 면접 질문**: [hotseat 1장(그레인/동시성)·A2](docs/notes/interview-hotseat.md), [interview-prep 5장(게임 도메인)](docs/notes/interview-prep.md).

**스스로 답해보기**: (a) 출격 잠금이 왜 "새 메커니즘이 아니다"라고 말할 수 있나? (b) 스태시↔레이드
TOCTOU를 grain 단일 활성화가 못 막는 이유는? (c) 다중 셀 겹침은 DB가 못 잡는데 그럼 뭐가 잡나?

---

## 6. 테스트로 배우기 (~30분)

- **125개** = 단위 41(`CalcFeeTests`, `StashGeometryTests` — DB 없는 순수 로직) + 통합 80 + 밴딩 4.
- 통합 테스트는 **Testcontainers Postgres + `WebApplicationFactory` 인프로세스 실로**로 실제
  API→Orleans→DB를 목킹 없이 통과(`MarketAppFixture`). 로그인은 `AuthedAs(playerId)`.
- 주요 파일: `MarketFlowTests`(머니샷: 원자 정산·동시 매수·티커·리더보드), `CrashRecoveryTests`(재수화·M1),
  `RaidTests`, `StashTests`, `EquipmentTests`, `HardeningTests`, `RateLimitTests`, `AuthTokenTests`, `OpenApiTests`.

**직접 해보기(일부러 깨보기)** — 이해도 점검용:
1. `MarketFlowTests.Concurrent_buys_against_single_unit_never_double_sell`을 열어라(서로 다른 7명이 재고 1개에
   동시 매수 → 체결 정확히 1건). `SettleFillAsync`의 낙관적 가드 `WHERE remaining_quantity=@before`를
   `WHERE id=@id`로만 바꿔 가드를 없애면 이 테스트가 어떻게 깨지는지 **예상**해보고, 실제로 바꿔 돌려봐라
   (확인 후 되돌릴 것). → 가드가 "이중 체결 최종 방어"임을 체감.
2. `CalcFeeTests`를 보고 수수료 절삭·오버플로 케이스를 확인.

**이게 답하는 면접 질문**: [hotseat A1(동시성)·9장(테스트 격리)](docs/notes/interview-hotseat.md).

---

## 파악 완료 체크리스트 (이걸 다 즉답하면 오너십 OK)

- [ ] grain 6종과 각 키(playerId vs templateId)를 그리고, 왜 아이템은 grain이 아닌지 말할 수 있다.
- [ ] `POST /api/orders`의 전체 경로를 파일·함수 순서로 화이트보드에 그린다.
- [ ] `SettleFillAsync`의 6단계와 보존식(`gross + improvement = releasedEscrow`, 수수료만 소각)을 손으로 검산한다.
- [ ] "인메모리 호가창은 투영"을 재수화 테스트가 **어떻게 증명**하는지 설명한다(DB-only 주문 등장).
- [ ] 낙관적 가드와 지갑 락 순서(데드락 수정)가 각각 뭘 막는지 말한다.
- [ ] 정합성 경계가 grain이 아니라 **DB 락**임을 설명한다(에스크로 vs 정산 이중 경로 포함).
- [ ] at-risk 잠금이 매도 에스크로 재사용임을, 스태시↔레이드 TOCTOU를 advisory lock으로 닫은 이유를 말한다.
- [ ] 그리드 겹침을 앱 AABB(주)와 DB `uq_stash_cell`(좌상단 백스톱)이 어떻게 나눠 막는지, 스택 1×1 갭을 안다.
- [ ] **먼저 인정할 갭 7개**([hotseat 부록 A](docs/notes/interview-hotseat.md))를 근거와 함께 말한다.

## 4시간 학습 플랜 (제안)

| 시간 | 구간 | 산출물 |
|---|---|---|
| 0:00–0:30 | 0장 멘탈모델 + 1장 띄우고 관찰 | psql로 주문→행 변화 눈으로 확인 |
| 0:30–1:30 | 2장 요청 추적 (`OrderBookEngine` 통독) | 경로를 손으로 그림 |
| 1:30–2:10 | 3장 정산 정독 + 보존식 검산 | 숫자 대입 검산 완료 |
| 2:10–2:40 | 4장 재수화 + `CrashRecoveryTests` | "왜 무손실"을 말로 설명 |
| 2:40–3:20 | 5장 레이드·스태시 | at-risk·advisory lock·AABB 이해 |
| 3:20–3:50 | 6장 테스트 깨보기 | 가드 제거 → 테스트 실패 재현·복원 |
| 3:50–4:00 | 체크리스트 + [hotseat 5개 모범답안](docs/notes/interview-hotseat.md) 소리 내어 | 즉답 리허설 |

> **핵심 태도**: 이 프로젝트의 값은 "다 만들었다"가 아니라 **"뭘 왜 했고, 뭘 왜 안 했는지 안다 + 정확성을
> 측정·증명했다"**. 갭은 약점이 아니라 판단의 증거로 말한다.

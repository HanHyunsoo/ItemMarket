# 부하 테스트 & 성능 분석 — 매칭 엔진

> 대상: `OrderBookGrain`(아이템 템플릿당 단일 활성화) 매칭 엔진을 **실제 API → Orleans →
> PostgreSQL** 경로로 부하. 목표는 (1) 처리량/지연 실측, (2) **동시성 하에서의 정합성
> 불변식(invariant) 증명**, (3) 핫 grain 병목의 정량화와 완화 방향 제시.

부하 도구: [`tools/LoadTest`](../tools/LoadTest) (`loadtest` 콘솔 앱).

---

## 방법론 (methodology)

- **격리**: 라이브 데모(`:5080` API, `:5173` Vite, DB `item_market`)를 건드리지 않기 위해
  같은 Postgres 컨테이너(`item-market-db`)에 **별도 DB `item_market_load`** 를 만들고
  (`db/ddl.sql` + `db/orleans-clustering.sql` 적용), **전용 API 인스턴스**를 다른 포트로
  기동했다: `Http__Port=5090`, `Orleans__SiloPort=11121`, `Orleans__GatewayPort=30021`,
  `Orleans__ClusterId=item-market-load`. API는 **Release** 빌드.
- **시딩(Npgsql 직접)**: 합성 플레이어 200명 + 지갑(각 10억 CAP) + 테스트 템플릿마다
  대용량 재고(플레이어·템플릿당 100만 개) 지급. 매도가 항상 재고를 확보하도록. `COPY`
  바이너리 임포트로 <200 ms에 시딩.
- **구동(HTTP)**: `C`개의 동시 워커가 각자 플레이어 1명으로 **로그인(JWT) 1회** 후,
  중앙가(mid=1000) ±25 대역에서 **랜덤 BUY/SELL 지정가**를 반복 등록. 좁은 대역이라
  반대편(타 플레이어) 주문과 교차하여 **실제 체결이 발생**한다. 요청별 지연과 결과를 기록.
- **시나리오**:
  - `spread` — 주문을 **20개 템플릿**에 분산. 서로 다른 `OrderBookGrain`이 병렬 실행 → 스케일.
  - `hot` — 모든 주문을 **단일 템플릿**에 집중. 그 grain의 1건씩(turn-based) 처리에 바운드.
- 각 실행은 시작 전 도메인 상태를 초기화하고 재시딩하여 **깨끗한 baseline**에서 시작.
  종료 후 **SQL 집계로 불변식**을 검증한다.

### 하드웨어 / 환경

| 항목 | 값 |
|---|---|
| 머신 | Apple M2 Pro, 12코어(성능 8 + 효율 4), 16 GB RAM |
| OS | macOS 26.2 |
| 런타임 | .NET 10 (10.0.301), Orleans 9.2.1, Npgsql 9.0.5 |
| DB | PostgreSQL 16 (Docker, 동일 호스트) |
| 토폴로지 | 단일 실로 co-host + 로컬 Postgres (전부 한 노드) |
| 파라미터 | players=200, concurrency=64, duration=30s, mid=1000±25, qty 1–10, fee 5% |

---

## 결과 (measured)

아래 표는 **데드락 수정(§병목 분석 3) 적용 후** 실측이다. 수정 전 대비 before/after는 §3 참조.

| 지표 | `spread` (20 템플릿) | `hot` (1 템플릿) |
|---|---:|---:|
| 주문 처리량 (orders/s) | **1,411** | **350** |
| 체결 처리량 (trades/s) | **998** | **250** |
| 총 주문 (30s) | 42,409 | 10,603 |
| 총 체결 (30s) | 30,010 | 7,572 |
| 지연 p50 | 35.7 ms | 180.3 ms |
| 지연 p95 | 119.9 ms | 215.3 ms |
| 지연 p99 | 175.3 ms | 256.9 ms |
| 지연 max | 312.1 ms | 306.3 ms |
| 비즈니스 오류 | 0 | 1 (0.009%) |
| 전송(transport) 오류 | 0 | 0 |

**핵심**: 종목을 분산하면 처리량이 **약 4배**(1,411 vs 350 orders/s, 998 vs 250 trades/s).
서로 다른 템플릿의 호가창은 독립 grain이라 병렬로 흐르기 때문. 반대로 핫 종목은 단일
grain에 직렬화되어 처리량이 바운드된다.

**지연 프로파일**: 핫은 p50이 높다(180 ms — 모든 요청이 그 grain의 순번을 기다림). 스프레드는
p50이 낮고(36 ms) 꼬리(p99 175 ms)도 안정적이다 — 초기엔 스프레드 p99가 973 ms까지 튀었으나
교차-grain 지갑 락 순서화(§3)로 제거했다.

리틀의 법칙 교차검증(hot): 동시성 64 / p50 0.180s ≈ 355 req/s ≈ 실측 350 orders/s. 즉 핫에서
클라이언트는 사실상 단일 grain 앞에 큐잉되며, grain의 실효 서비스 시간은 주문당 ≈ 2.7 ms
(정산 트랜잭션 포함)다.

---

## 정합성 불변식 (correctness under load) — 헤드라인

부하 종료 후 `item_market_load`에 대해 SQL로 검증. **두 시나리오 모두 전 항목 PASS.**

| 불변식 | 정의 | 결과 |
|---|---|---|
| 음수 잔액 없음 | `count(wallet WHERE balance < 0)` == 0 | ✅ 0 |
| **병뚜껑 보존** | `Σ wallet.balance + Σ order.escrow_caps + Σ trade.fee_amount == 최초 발행량` | ✅ diff=0 |
| 아이템 보존 | 템플릿별 `Σ inventory + Σ 미체결 매도 잔량 == 최초 지급량` | ✅ 전 템플릿 일치 |
| 주문 상태 정합 | `rem>qty` / `OPEN∧rem=0` / `FILLED∧rem≠0` / 잘못된 `PARTIALLY_FILLED` / `CANCELLED∧escrow≠0` 모두 0 | ✅ 전부 0 |

예: spread 실행에서 최초 발행 200,000,000,000 CAP == 지갑 199,974,878,660 + 에스크로
21,364,375 + 소각 수수료 3,756,965 (diff = 0). 즉 24,714건의 동시 체결 속에서도 **1 CAP도
새로 발행되거나 유실되지 않았고**, 아이템도 복제/증발이 없었다.

돈 보존이 성립하는 근거는 **정산이 단일 Postgres 트랜잭션**이기 때문이다. 한 체결에서
{판매대금 지급, 수수료 소각, 매수 차익 환불, 에스크로 차감}의 순변화 합은 0이며(수수료는
소각 sink로 별도 집계), 어떤 예외에도 트랜잭션 전체가 롤백된다.

---

## 병목 분석 (bottleneck analysis)

### 1) 왜 핫 종목이 바운드되는가

Orleans는 grain 활성화를 **클러스터 전체에서 정확히 1개**로 보장하고, 기본 non-reentrant라
**한 번에 하나의 요청만** 처리한다. 이는 매칭의 정확성(중복 체결·이중 판매 차단)을 락 코드
없이 얻는 대가로, **단일 종목의 처리량 상한 = 그 grain 하나의 직렬 처리 속도**가 된다는 뜻이다.
주문당 서비스 시간은 대부분 **동기 정산 트랜잭션(SettleFill)** 이 지배한다(왕복 DB I/O).
따라서 아무리 클라이언트를 늘려도(concurrency 64) 핫 종목은 큐만 길어질 뿐 throughput은
평평해지고, p50 지연이 그 큐 대기시간으로 상승한다(실측 167 ms).

이것은 결함이 아니라 **설계상의 정확성-처리량 트레이드오프**다. 실제 게임 경제에서 "단일
인기 종목에 전 서버가 몰리는" 상황이 이 상한에 부딪힌다.

### 2) 완화: 인기 호가창을 **가격 밴드로 샤딩**

핫 grain을 깨는 표준 기법은 하나의 논리적 호가창을 **가격 구간(price band)별 여러 grain으로
분할**하는 것이다. 키를 `templateId`에서 `(templateId, priceBand)`로 확장하면:

- 서로 다른 밴드의 주문이 **병렬 매칭**되어 단일 grain 상한을 넘어선다(핫→준-스프레드화).
- 라우팅: 테이커 주문은 자신이 교차 가능한 밴드들에만 순서대로 방문(매수는 낮은 ask 밴드부터).
- 트레이드오프: 밴드 경계를 걸치는 주문의 라우팅/부분 이월 로직이 필요하고, 밴드별 최우선
  호가를 모으는 **상단 집계(top-of-book aggregator)** 가 추가된다. 즉 "락 없는 단일 직렬성"의
  단순함을 일부 내주고 병렬성을 얻는 교환.
- 스케일아웃과 결합: 밴드 grain들은 실로 추가 시 자연히 분산 배치된다(위치 투명성).

### 3) 실측에서 드러난 2차 병목 — 교차-grain DB 데드락 → **발견·수정·재측정**

**발견**: 초기 `spread`의 p99 꼬리(973 ms)와 500 에러들은 **grain 병렬성이 DB 계층에서
재충돌**한 결과였다. 서로 다른 템플릿의 grain이 병렬로 정산할 때, 같은 플레이어가 여러 종목을
거래하면 두 정산 트랜잭션이 **동일한 `wallet` 행을 서로 다른 순서로 잠가** Postgres 데드락(40P01)이
났다. 데드락 victim은 원자적으로 롤백되고 grain은 DB에서 재수화되어 **불변식은 유지**됐지만,
재수화 왕복이 p99 꼬리를 만들었다. (핫 시나리오는 grain이 하나라 이런 교차 경합이 없어 꼬리가
오히려 짧았다.)

**수정**: 정산 트랜잭션 시작에 이 트랜잭션이 건드릴 지갑 행을
`SELECT ... WHERE player_id = ANY(ids) ORDER BY player_id FOR UPDATE`로 **항상 playerId
오름차순으로 미리 잠갔다**(`MarketRepository.SettleFillAsync`). 매수자/판매자 역할과 무관하게 락
획득 순서가 전역적으로 일정해져 데드락 사이클이 성립하지 않는다.

**재측정 (같은 조건: players=200 · concurrency=64 · 30s, Release):**

| 지표 (spread) | 수정 전 | 수정 후 |
|---|---:|---:|
| 처리량 | 1,152 orders/s | **1,411 orders/s** |
| p95 | 133 ms | 120 ms |
| **p99** | **973 ms** | **175 ms** |
| max | (긴 꼬리) | 312 ms |
| 데드락(40P01)/500 | 다수 | **0** |
| 정합성 불변식 | ALL PASS | ALL PASS |

락 순서화 한 줄로 **p99를 5.5배 개선**(973→175 ms)하면서 정확성은 그대로 유지했다. 핫 시나리오는
단일 grain 상한이 병목이라 이 수정과 무관하게 ≈350 orders/s로 변화 없음(예상된 결과).

---

## 가격 밴드 샤딩 (price-band sharding) — 구현 & 재측정

§병목 분석 2)에서 방향만 제시했던 완화책을 **실제로 구현하고 핫 시나리오로 재측정**했다.
스위치는 설정 `Market:PriceBandSize`(기본 **0=비활성**). 0이면 `OrderBookGrain`이 종전과
동일하게 템플릿당 단일 호가창을 직접 매칭한다(**기존 동작 바이트 단위 보존** — 기존 통합
테스트 23 + 단위 33 전부 그대로 통과).

### 설계 (façade + 밴드 grain)

- `IOrderBookGrain`(키=templateId)은 유지하되 **코디네이터(façade)** 로 역할이 바뀐다.
  주문의 밴드 = `unitPrice / PriceBandSize`를 계산해 밴드별 `IOrderBandGrain`
  (키=`"{templateId}:{band}"`)로 라우팅한다.
- `OrderBandGrain`은 자기 밴드의 잔존 주문·**밴드 내부 매칭**·에스크로·정산·스냅샷을 소유한다.
  매칭/정산 로직은 기존 엔진을 그대로 추출한 `OrderBookEngine`을 **재사용**하며, 활성화 시
  자기 밴드에 속한 미체결 주문만 재수화한다. 따라서 매칭 후보가 밴드로 한정돼 밴드 간 교차가
  구조적으로 불가능하다.
- `GetSnapshot`은 미체결 밴드들로 팬아웃해 병합한다(bids 내림차순 / asks 오름차순). `CancelOrder`는
  주문 가격으로 소유 밴드를 찾아 라우팅. grain은 SignalR에 결합하지 않고 `PlaceOrderResult`/스냅샷
  모양이 동일하므로 **엔드포인트/실시간 발행기는 무수정**이다.
- **조건부 리엔트런시**: 코디네이터는 상태 없이 라우팅만 하므로 리엔트런트가 안전하며, 리엔트런트여야만
  여러 주문이 밴드 grain을 기다리는 동안 코디네이터가 새 병목이 되지 않는다(핵심). 반면 밴드 grain과
  OFF 모드는 매칭을 직접 하므로 논-리엔트런트여야 한다. `[MayInterleave]` 술어 + 기동 시 설정되는
  플래그로 분기한다: OFF면 논-리엔트런트(기존과 동일), ON이면 코디네이터만 리엔트런트.

### 핵심 통찰 — 왜 "완화"이지 "제거"가 아닌가 (inherent serialization)

**단일 종목의 엄격한 전역 가격-시간 우선은 원리적으로 단일 직렬화 지점을 요구한다.** 가장 싼 매도를
전 가격대에서 찾아 교차시키려면 한 곳이 그 종목의 전체 호가창을 봐야 하기 때문이다. 즉 *한 템플릿의
매칭을 코어 여러 개로 병렬화하면서 동시에 엄격한 교차-가격 매칭을 유지하는 것은 불가능*하다.
샤딩이 성립하는 유일한 길은 시맨틱을 **밴드-격리 매칭**으로 완화하는 것뿐이다 — 주문은 자기 밴드
안에서만 매칭되고, 밴드 경계를 넘는 가격 개선 교차는 포기한다. 이는 결함이 아니라 **병렬성과 맞바꾼
의도된 제품 트레이드오프**이며, 회귀 테스트로 명시적으로 고정했다(낮은 밴드의 싼 매도를 높은 밴드의
매수가 **잡지 못함**을 단언).

### 재측정 (hot, 단일 노드 · Release · players=200 · concurrency=64 · 30s)

핫 시나리오의 가격을 **넓은 대역에 분포**시켜(mid=1000 ± 500 → 500‥1500) 밴드가 실제로 나뉘게 했다.
`PriceBandSize=50` → 밴드 = 가격/50 → 약 **21개 밴드**. OFF/ON 모두 **동일한** 가격 분포로 측정.

| 지표 (hot) | **OFF** (`PriceBandSize=0`) | **ON** (`PriceBandSize=50`) |
|---|---:|---:|
| 주문 처리량 (orders/s) | **299** | **649** (≈ 2.2×) |
| 체결 처리량 (trades/s) | 214 | 453 (≈ 2.1×) |
| 지연 p50 | 207 ms | **95 ms** |
| 지연 p95 | 265 ms | 141 ms |
| 지연 p99 | 365 ms | **189 ms** |
| 지연 max | 442 ms | 274 ms |
| 비즈니스/전송 오류 | 0 / 0 | 0 / 0 |
| 정합성 불변식 | **ALL PASS** | **ALL PASS** |

확인 재실행에서도 ±2% 이내로 재현(OFF 306 / ON 645 orders/s). **단일 핫 grain 상한을 약 2.2배
돌파**했고, 큐 대기가 여러 밴드로 분산돼 p50이 절반 이하로 떨어졌다. 무엇보다 **밴딩 하에서도
병뚜껑 보존·아이템 보존·주문 상태 정합·음수 잔액 0이 그대로 유지**된다 — 정산은 여전히 밴드와
무관한 단일 Postgres 트랜잭션이고, 교차-grain 지갑 락 순서화(§3)가 밴드 grain 간 동시 정산에도
그대로 적용되기 때문이다.

> 참고: 여기의 OFF 299 orders/s는 헤드라인 핫(350)보다 낮다. 가격 대역을 ±25 → ±500으로 넓혀
> 단일 grain이 매칭마다 훑을 후보가 많아졌기 때문이며, OFF/ON을 **같은** 넓은 분포로 비교하기 위한
> 조건이다(공정 비교). 병렬 배수(≈2.2×)가 코어/단일 DB/코디네이터 홉 오버헤드에 눌려 이론상 21배보다
> 훨씬 작다는 점도 정직하게 남긴다 — 실효 상한은 다시 **단일 Postgres**로 옮겨간다.

### 언제 켜는가

- **켠다**: 소수의 **깊은 단일 핫 종목**에 부하가 집중되고, 밴드 경계를 넘는 가격 개선 교차를
  포기해도 무방한 시장(예: 넓은 가격대에 물량이 두껍게 쌓이는 인기 소모품). 이때 단일 grain 상한을
  넘는 처리량과 낮은 지연을 얻는다.
- **끈다(기본)**: 대부분의 종목이 얕거나 부하가 여러 종목에 퍼져 있을 때. `spread`가 이미 grain
  병렬성으로 스케일하며(≈4×), 밴드 격리의 시맨틱 손실 없이 엄격한 전역 가격-시간 우선을 유지한다.
- 스케일아웃과 결합: 밴드 grain은 실로 추가 시 위치 투명성으로 자연히 분산 배치된다.

---

## 캐비어트 (honest caveats)

- **단일 노드**: API·실로·Postgres가 모두 한 M2 Pro 위에 있다. 실제 배포에서는 실로/DB가
  분리되고, `adonet` 클러스터링으로 실로를 늘리면 서로 다른 종목의 grain이 물리적으로 분산되어
  스프레드 처리량이 더 오른다(단, 단일 DB가 새 상한이 됨).
- 클라이언트(부하 도구)도 같은 머신에서 CPU/커넥션을 경쟁하므로 절대 수치는 보수적으로 볼 것.
  상대 비교(spread vs hot, ≈4배)와 불변식 결과가 핵심 산출물이다.
- **Debug 아님 — Release**로 API를 빌드해 측정했다(정산 경로의 JIT 최적화 반영).
- 부하는 스택형(FOOD/MEDICAL) 템플릿만 사용(매도 재고 보장 목적). 유니크(무기) 경로는
  인스턴스 소유권 이전이라 부하 특성이 다르며 별도 측정 대상.
- 교차-grain 데드락(초기 관측)은 §3의 지갑 락 순서화로 제거됨(재측정 500=0). 정확성은 수정 전에도
  트랜잭션+롤백으로 안전했고, 수정은 p99 꼬리를 없애는 최적화였다.

---

## 재현 (reproduce)

```bash
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"

# 1) 격리 DB 생성 + 스키마
docker exec item-market-db psql -U market -d postgres -c "CREATE DATABASE item_market_load;"
docker exec -i item-market-db psql -U market -d item_market_load < db/ddl.sql
docker exec -i item-market-db psql -U market -d item_market_load < db/orleans-clustering.sql

# 2) 전용 API (다른 포트, Release, 백그라운드)
dotnet build src/ItemMarket.Api -c Release
Http__Port=5090 Orleans__SiloPort=11121 Orleans__GatewayPort=30021 \
Orleans__ClusterId=item-market-load \
ConnectionStrings__Postgres="Host=localhost;Port=5432;Database=item_market_load;Username=market;Password=market" \
Auth__Secret="load-test-secret-0123456789-abcdefghijklmnop" \
  dotnet run --project src/ItemMarket.Api -c Release --no-build &

# 3) 부하 (spread → hot)
PG="Host=localhost;Port=5432;Database=item_market_load;Username=market;Password=market"
dotnet run --project tools/LoadTest -c Release -- --api http://localhost:5090 --pg "$PG" \
  --players 200 --templates 20 --concurrency 64 --duration 30 --scenario spread
dotnet run --project tools/LoadTest -c Release -- --api http://localhost:5090 --pg "$PG" \
  --players 200 --templates 20 --concurrency 64 --duration 30 --scenario hot

# 3b) 가격 밴드 샤딩 off vs on (핫, 넓은 가격 대역으로 밴드가 나뉘게)
#     API를 Market__PriceBandSize 없이(=0) 한 번, =50 으로 한 번 기동해 각각 측정한다.
#     ON 예: 위 (2)의 기동 명령에 Market__PriceBandSize=50 을 추가.
for BAND in 0 50; do
  Http__Port=5090 Orleans__SiloPort=11121 Orleans__GatewayPort=30021 \
  Orleans__ClusterId=item-market-load Market__PriceBandSize=$BAND \
  ConnectionStrings__Postgres="$PG" \
  Auth__Secret="load-test-secret-0123456789-abcdefghijklmnop" \
    dotnet run --project src/ItemMarket.Api -c Release --no-build & APIPID=$!
  until curl -sf http://localhost:5090/health >/dev/null; do sleep 1; done
  dotnet run --project tools/LoadTest -c Release -- --api http://localhost:5090 --pg "$PG" \
    --players 200 --concurrency 64 --duration 30 --scenario hot --mid 1000 --band 500
  kill $APIPID; wait $APIPID 2>/dev/null
done

# 4) 정리: API 종료 + 부하 DB 삭제 (데모 item_market 은 그대로)
docker exec item-market-db psql -U market -d postgres -c "DROP DATABASE item_market_load;"
```

---

## 부록: 다중 인스턴스 실시간 (Redis SignalR 백플레인)

성능이 아닌 **아키텍처 실증**이지만 같은 격리 방법론(전용 DB/포트/ClusterId)을 쓰므로 함께 기록한다.

**주장**: 2 API 인스턴스 + Orleans adonet 클러스터링에서 특정 `OrderBookGrain` 은 단 하나의 실로에
살고 어느 인스턴스로 REST 가 들어와도 그 grain 으로 라우팅된다. 하지만 SignalR 클라이언트는 두
인스턴스 중 한쪽에만 붙는다. REST 를 처리한 인스턴스가 `IHubContext` 로 발행해도 구독자가 다른
인스턴스에 있으면 못 받는다 — **Redis 백플레인**이 인스턴스 간 중계를 해줘야 크로스-인스턴스 푸시가
성립한다.

**토폴로지**: `scripts/run-cluster.sh` — 인스턴스 A(`:5091`), B(`:5092`), 전용 DB
`item_market_cluster`(`db/ddl.sql`+`orleans-clustering.sql`), 전용 ClusterId `item-market-cluster`,
Redis `localhost:6379`. 라이브 데모(`:5080`/`item_market`)와 완전 분리. 멤버십 테이블에 **Active 실로
2개**(Status=3) 확인.

**절차**: 스로어웨이 `Microsoft.AspNetCore.SignalR.Client` 콘솔로 (1) A 의 hub 에 연결 +
`SubscribeTemplate(1)`, (2) B 의 REST 로 매칭 sell(Alpha)+buy(Bravo) 등록, (3) A 의 클라이언트 수신 관찰.

**실측 결과**:

| 구성 | B REST 체결(fills) | A 수신 `OrderBookUpdated` | A 수신 `TradeExecuted` | 결론 |
|---|---:|:---:|:---:|---|
| **Redis 백플레인 ON**  | 1 | ✅ YES | ✅ YES | 크로스-인스턴스 푸시 성립(B→A 중계) |
| **Redis 백플레인 OFF** | 1 | ❌ NO  | ❌ NO  | Orleans 라우팅으로 체결은 되나 A 는 미수신 |

두 케이스 모두 B 의 REST 는 정상 체결(fills=1) → Orleans 크로스-인스턴스 라우팅은 백플레인과 무관하게
동작한다는 것과, **실시간 푸시의 크로스-인스턴스 전달만이 Redis 백플레인의 산물**임을 분리해 증명한다.
스위치는 config-gated(`Redis:ConnectionString` 비면 인메모리 단일 인스턴스)라 기존 60개 테스트/데모/
성능 경로는 불변이다.

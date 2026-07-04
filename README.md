# Wasteland Exchange — 아이템 거래소

> 아포칼립스 익스트랙션 슈터 장르의 **서버 권위(server-authoritative) 아이템 거래소**.
> 주문서 매칭 엔진 · 에스크로 · 원자적 정산 · 운영(GM) 툴을 갖춘 게임 서비스 백엔드 포트폴리오입니다.

플레이어는 병뚜껑(CAP)을 재화로 아이템을 사고팝니다. 매수/매도 주문은 **가격-시간 우선순위**로
매칭되고, 체결 시 아이템·재화·수수료가 **하나의 트랜잭션으로 원자 정산**됩니다.
먹을거/힐템/탄약은 수량 기반 스택으로, 근접무기/총은 내구도·부착물을 가진 **유니크 인스턴스**로
거래됩니다(아포칼립스 세계관이라 총은 귀합니다).

```
Vue 3 (Vite + Element Plus)  ──REST/JWT──▶  ASP.NET Core Minimal API
                                                    │ co-host
                                            Microsoft Orleans 실로
                                     WalletGrain / InventoryGrain / OrderBookGrain(매칭엔진)
                                                    │ Dapper (단일 트랜잭션 정산)
                                              PostgreSQL (소스 오브 트루스)
```

---

## 기술 스택과 선택 이유

| 스택 | 선택 이유 |
|---|---|
| **C# / .NET + ASP.NET Core Minimal API** | 게임 서버 업계 표준 언어. Minimal API로 엔드포인트-그레인 배선을 얇게 유지 |
| **Microsoft Orleans (버추얼 액터)** | Halo 백엔드를 위해 만들어진 게임 서비스 검증 프레임워크. **grain은 클러스터 전체에서 활성화가 정확히 1개 + 턴 기반 단일스레드**라서, 거래소의 최대 난제인 "한 아이템에 동시 주문 N건"이 **락 코드 0줄**로 직렬화됨 |
| **MSA 대신 Orleans를 택한 이유** | 서비스 분리(브로커·디스커버리·배포 파이프라인)의 운영 비용 없이 분산 시스템의 핵심 문제(단일 활성화, 위치 투명성, 클러스터 멤버십)를 다룰 수 있음. 스케일아웃은 실로 추가로 해결 |
| **PostgreSQL + Dapper** | 돈과 아이템의 **소스 오브 트루스는 DB**. 정산처럼 여러 자산이 얽히는 변경은 SQL 트랜잭션이 가장 단순하고 검증 가능. Dapper로 SQL을 명시적으로 관리 |
| **Orleans Transactions 대신 DB 트랜잭션** | 검토 결과 정산의 쓰기 대상(주문·지갑·인벤·체결·원장)이 전부 한 DB에 있음 → 분산 트랜잭션이 필요 없는 구조. Orleans Tx는 트랜잭셔널 상태 프로바이더 요구 + 성능 오버헤드가 있어 **단일 Postgres 트랜잭션 + 낙관적 동시성 가드**를 선택 |
| **Orleans 클러스터링 = Postgres (ADO.NET)** | 다중 인스턴스 멤버십에 Redis/K8s를 추가하지 않고 이미 있는 Postgres 재사용. Redis는 "호가 실시간 푸시(SignalR 백플레인)·핫 캐시"가 필요해지는 시점의 확장 옵션으로 문서화 |
| **JWT (HS256) + 롤 기반 인가** | 무상태 인증. 플레이어 식별은 서명된 `sub` 클레임만 신뢰(클라이언트 헤더 스푸핑 불가). 운영 API는 `admin` 롤 필요(403) |
| **Vue 3 + TypeScript + Element Plus** | 운영 툴까지 포함한 프론트 요구를 빠르게 커버. C# DTO를 TS 타입으로 미러링해 계약을 양쪽에서 강제 |
| **Testcontainers + WebApplicationFactory** | 단위 테스트보다 **통합 테스트 우선**: 일회용 Postgres 컨테이너 + 인프로세스 실제 호스트로 매칭→정산→원장까지 목킹 없이 검증. `dotnet test` 한 방으로 재현 |
| **Docker Compose** | Postgres + 스키마/시드(ddl) 자동 초기화. `docker compose up -d` 한 방 |

---

## 핵심 설계

### 매칭 엔진 (`OrderBookGrain`, 아이템 템플릿당 1개)
- 가격-시간 우선, 부분 체결 지원. grain 단일스레드 덕에 매칭 자체는 경쟁 상태가 없음.
- **인메모리 호가창은 "재구성 가능한 투영(projection)"**: 모든 변경은 같은 트랜잭션으로 Postgres에
  write-through 되고, 활성화 시(`OnActivateAsync`) 미체결 주문을 DB에서 읽어 재구성(rehydrate).
  실로 장애·리밸런싱·유휴 비활성화 후에도 호가창이 유실되지 않음.
- 드문 이중 활성화 윈도우 대비, 정산 트랜잭션에 **낙관적 동시성 가드(잔량 검증)** → DB가 최종 심판.

### 에스크로와 원자 정산
- 매수 등록 = `단가×수량` 병뚜껑 잠금, 매도 등록 = 재고 차감(스택) 또는 인스턴스 소유권 분리(유니크).
  → **이중 판매·이중 지불·아이템 복제(dupe)를 주문 시점에 원천 차단.**
- 체결 = 단일 Postgres 트랜잭션: 판매 대금 지급(수수료 차감) + 수수료 소각(sink) + 아이템 이전
  + 차액 환불(상한가보다 싸게 체결 시) + 체결/원장 기록 + 주문 상태 갱신. 실패 시 전체 롤백.
- 지갑의 모든 변동은 append-only **원장(wallet_ledger)** 에 사유와 함께 기록 → 감사/이상거래 추적 기반.

### 스케일 아웃
- Orleans 단일 활성화 = 실로가 몇 대여도 특정 아이템의 호가창은 클러스터에서 정확히 한 곳.
  실로를 늘리면 서로 다른 아이템의 호가창이 분산되어 처리량이 늘고, 종목별 직렬성은 유지.
- `Orleans:ClusteringMode=adonet` 스왑으로 Postgres 멤버십 기반 다중 실로 구성(`scripts/run-cluster.sh`).

---

## 개발 중 만난 이슈와 해결

포트폴리오의 핵심이라 생각해 **찾은 문제 → 원인 → 해결 → 회귀 테스트**로 남겼습니다.
전체 감사 리포트: [`docs/backend-audit.md`](docs/backend-audit.md)

### 1. 병뚜껑 무한 발행 취약점 (Critical)
- **증상**: 단가·수량을 극단값으로 넣으면 `단가 × 수량`(long)이 오버플로로 **음수**가 됨.
  에스크로 검사 `잔액 < 금액`은 음수 금액을 통과시키고, `잔액 - (음수)`가 **지갑에 돈을 꽂아줌**.
  게임 경제를 즉사시키는 실제 익스플로잇 경로.
- **해결**: `Int128`로 상한 검증(단가 ≤ 1e12, 수량 ≤ 1e6, 총액 ≤ 1e15) 후 진입 차단. 수수료 계산도
  단일 함수의 Int128 산술로 통일. **회귀 테스트**: 극단값 주문이 `ValidationError`로 거부되고
  지갑이 1캡도 변하지 않음을 검증.

### 2. 에스크로 커밋 후 주문 INSERT 실패 시 자산 영구 증발 (Critical)
- **증상**: 에스크로(자산 잠금)와 주문 행 INSERT가 별개 트랜잭션이라, 사이에서 실패하면
  잠긴 자산을 되돌릴 주문 행 자체가 없음 → 취소도 환불도 불가.
- **해결**: INSERT 실패 시 **보상 트랜잭션**(캡 환불/재고 반환/인스턴스 소유권 복구) + 크리티컬 로깅.

### 3. 정산 실패가 호가창을 오염시키는 문제 (Critical)
- **증상**: 인메모리 잔량을 DB 커밋 **전에** 차감 → 정산 트랜잭션이 실패하면 인메모리와 DB가 어긋나고,
  낙관적 가드에 걸려 이후 모든 매칭이 연쇄 실패.
- **해결**: **커밋 성공 후에만** 인메모리 반영. 실패 시 `DeactivateOnIdle()` + 예외 재던짐으로
  grain을 버리고 다음 요청에서 DB로부터 재수화 — "인메모리는 언제든 버려도 되는 투영"이라는
  원칙을 코드로 강제.

### 4. 자전거래(wash trading) 허용 문제 (High)
- **증상**: 본인 매도 호가에 본인 매수가 체결 가능 → 수수료만 내며 체결량 조작(시세 조작 기초).
- **해결**: 매칭 루프에서 본인 주문은 건너뛰고 다음 호가와 매칭. 교차하는 본인 주문은 체결 없이
  호가창에 공존(API 계약에 명시). 회귀 테스트로 "본인과는 스킵, 타인과는 체결"을 검증.

### 5. 공유 계약(Contracts)과 Orleans 직렬화의 충돌
- **문제**: grain 경계를 넘는 DTO는 Orleans 직렬화가 필요하지만, Contracts 프로젝트는
  프론트와 공유하는 순수 계약이라 `[GenerateSerializer]` 같은 Orleans 의존성을 넣고 싶지 않았음.
- **해결**: 실로에 **네임스페이스 스코프 JSON 직렬화기**를 등록(`ItemMarket.*` 한정).
  계약은 순수하게 유지하면서 코덱 부재 문제 해결.

### 6. Orleans 9의 예약 설정 섹션과 충돌
- **문제**: `Orleans:Clustering` 키로 스왑 스위치를 만들었더니 Orleans 9가 `"Orleans"` 섹션을
  프로바이더 자동 바인딩용으로 예약하고 있어 기동 충돌.
- **해결**: 예약 키를 피해 `Orleans:ClusteringMode` 스칼라 키로 우회. 한 머신 다중 인스턴스를 위해
  HTTP 포트도 `Http:Port`(env `Http__Port`) 소유 키로 제어.

### 7. Element Plus 다크 테마가 통째로 미적용
- **문제**: 디자인 시스템을 다크로 짰는데 화면은 기본 파란-흰 테마. 원인은 Element Plus 다크 변수가
  `html.dark` **클래스**를 요구하는데 `data-theme="dark"` 속성만 넣었던 것.
- **해결**: `index.html`에 `class="dark"` 추가(로드베어링). CSS 변수 오버라이드로 전 컴포넌트 통일.

### 8. "동시 구매"는 테스트로 증명해야 한다
- 단일 재고(수량 1)에 **동시 매수 8건** → 정확히 1건만 체결되고 dupe·이중 지불이 없음을
  통합 테스트로 고정. 액터 모델의 직렬화 보장을 말이 아니라 테스트로 증명.

### 테스트 결과
```
통합 테스트 11/11 통과 (기존 6 + 감사 회귀 5)
— 카탈로그 · 원자 정산+수수료 소각 · 부분 체결 · 에스크로 환불
— 인증/인가(401/403) · 동시 매수 단일 체결 · 오버플로 거부 · 자전거래 스킵
— 부분 체결 후 취소의 정확한 환불액 · 에러코드 보존
```

---

## 실행 방법

```bash
# 0) 사전 조건: .NET 10 SDK, Docker, Node 20+, jq

# 1) DB (스키마 + 102종 아이템 마스터 + 시드 자동 적용)
docker compose up -d

# 2) 백엔드 (http://localhost:5080)
dotnet run --project src/ItemMarket.Api

# 3) 프론트 (http://localhost:5173)
cd web && npm install && npm run dev

# 4) 살아있는 마켓 만들기 (호가 + 체결 이력 더미)
./scripts/seed-market.sh    # 마켓메이커 호가: 전 스택형 3단 매도/2단 매수 + 유니크 매물
./scripts/seed-trades.sh    # 체결 이력: 전 스택형 종목당 2~4건 + 유니크 무기 체결

# 통합 테스트 (Docker만 있으면 됨 — 일회용 Postgres 자동 기동)
dotnet test tests/ItemMarket.IntegrationTests

# 다중 실로 (Postgres 클러스터링)
./scripts/run-cluster.sh
```

시드 플레이어(비밀번호 없는 데모 로그인): `Survivor_Alpha` · `Survivor_Bravo` · `Trader_Charlie`(admin — 운영 툴 접근)

---

## 프로젝트 구조

```
src/ItemMarket.Contracts/   # 프론트/백 공유 계약(DTO) — TS 타입으로 미러링
src/ItemMarket.Grains/      # Orleans grain (지갑/인벤/주문서 매칭엔진) + Dapper 리포지토리
src/ItemMarket.Api/         # Minimal API + 실로 co-host, JWT, 어드민 인가
web/                        # Vue 3 프론트 (마켓/인벤/지갑/주문/어드민) + 픽셀 스프라이트
db/ddl.sql                  # 스키마 + 아이템 마스터 102종 (docker 초기화 시 자동 적용)
db/orleans-clustering.sql   # Orleans ADO.NET 멤버십 테이블
tests/                      # 통합 테스트 (Testcontainers + WebApplicationFactory)
scripts/                    # 마켓/체결 시드, 다중 실로 기동
docs/api-contract.md        # REST 계약 (매칭 규칙·오류 코드 포함)
docs/backend-audit.md       # 자체 감사 리포트 (발견→수정→회귀 테스트)
tools/gen-sprites.mjs       # ASCII 픽셀맵 → SVG 스프라이트 생성기 (15종)
```

## 확장 로드맵

- **그리드 인벤토리**: N×M 스태시에 w×h 아이템 배치 — 서버 권위 배치 검증(경계·충돌)
- **다중 인스턴스 실시간(Redis 백플레인) ✅(구현·config-gated)**: `Redis:ConnectionString` 을 주면
  SignalR 에 `AddStackExchangeRedis` 백플레인을 붙인다(비어있으면 기존 인메모리 단일 인스턴스 그대로).
  2 인스턴스(Orleans adonet 클러스터링 + Redis 백플레인, `scripts/run-cluster.sh`)로 **크로스-인스턴스
  라이브 푸시를 실증**: 인스턴스 A(:5091)에 붙은 SignalR 구독자가, 인스턴스 B(:5092)의 REST 주문 체결로
  발생한 `OrderBookUpdated`+`TradeExecuted` 를 수신(B→A 중계). 백플레인을 끄면 동일 시나리오에서 REST 는
  여전히 체결되지만(fills=1) A 는 두 이벤트를 모두 미수신 → 백플레인이 결정적 요소임을 확인.
  설계·계약: [`docs/realtime-contract.md`](docs/realtime-contract.md)
- **운영 고도화**: rate limiting, 이상 거래 탐지(원장 기반 RMT 휴리스틱), 시세 차트
- **핫 grain 가격대 샤딩 ✅(구현·opt-in)**: 인기 종목 호가창을 `(templateId, priceBand)`로 분할하는
  코디네이터+밴드 grain을 구현(`Market:PriceBandSize`, 기본 0=비활성). 핫 시나리오에서 단일 grain
  상한을 **≈2.2× 돌파**(299→649 orders/s, p99 365→189 ms), 밴드-격리 매칭이라는 시맨틱 트레이드오프
  포함. 측정·설계: [`docs/perf-report.md`](docs/perf-report.md)

---

## 성능 (부하 테스트)

실제 **API → Orleans → PostgreSQL** 경로를 봇 클라이언트로 부하해 처리량·지연을 실측하고,
동시성 하에서 **돈/아이템 보존 불변식**을 SQL로 검증했다. 도구: [`tools/LoadTest`](tools/LoadTest),
전체 분석: [`docs/perf-report.md`](docs/perf-report.md).

측정(Apple M2 Pro, 단일 노드, Release, players=200 · concurrency=64 · 30s):

| 시나리오 | 처리량 | 체결 | p50 | p95 | p99 |
|---|---:|---:|---:|---:|---:|
| **spread** (20 종목 분산) | **1,411 orders/s** | 998 trades/s | 36 ms | 120 ms | 175 ms |
| **hot** (단일 종목 집중) | **350 orders/s** | 250 trades/s | 180 ms | 215 ms | 257 ms |

- **종목 분산 시 처리량 ≈ 4배** — 서로 다른 `OrderBookGrain`이 병렬로 흐른다. 핫 종목은
  단일 grain의 turn-based 직렬 처리에 바운드(정확성-처리량 트레이드오프).
- **불변식 전 항목 PASS**: 3만 건 동시 체결 속에서 병뚜껑 보존(발행량 == 지갑+에스크로+소각
  수수료, diff=0)·아이템 보존·주문 상태 정합·음수 잔액 0. 정산이 단일 Postgres 트랜잭션이라 가능.
- **부하테스트가 찾은 데드락 → 수정 → 재측정**: 초기 spread p99가 교차-grain 지갑 락 순서
  경합(Postgres 40P01)으로 973 ms까지 튀었다. 정산 트랜잭션에서 지갑 행을 playerId 순으로
  미리 잠그니 **p99 973 → 175 ms(5.5배)**, 데드락 0.
- **가격 밴드 샤딩(opt-in)으로 핫 grain 상한 돌파**: `Market:PriceBandSize`를 켜면 코디네이터가
  주문을 밴드별 grain으로 라우팅해 핫 종목이 **299 → 649 orders/s(≈2.2×)**, p99 **365 → 189 ms**로
  개선(불변식 전부 PASS). 대가로 밴드 경계를 넘는 가격 개선 교차를 포기하는 밴드-격리 시맨틱을
  택한다(엄격 전역 우선은 단일 직렬화 지점을 요구하므로 병렬성과 양자택일). 리포트 참조.

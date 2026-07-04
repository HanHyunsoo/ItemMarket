# 실시간 계약 (SignalR) — Front ↔ Back

호가창/체결/지갑을 폴링 대신 **서버 푸시**로 전달한다. 프론트/백이 이 이벤트 계약을 공유한다.

## 허브

- URL: **`/hubs/market`**
- 인증: **JWT Bearer**. WebSocket은 헤더를 못 실으므로 `?access_token=<jwt>` 쿼리로 전달하고,
  서버는 이 경로에 한해 쿼리의 access_token을 읽도록 JwtBearer 이벤트를 설정한다.
- JSON 직렬화는 REST와 동일: **enum = PascalCase 문자열**, 프로퍼티 = camelCase
  (`AddJsonProtocol`에 `JsonStringEnumConverter` + camelCase 적용).
- 연결 시 서버는 토큰의 `sub`(playerId)로 **유저 그룹**에 자동 가입시킨다(그룹명 `user:{playerId}`).

## Client → Server (Hub 메서드)

| 메서드 | 인자 | 설명 |
|---|---|---|
| `SubscribeTemplate` | `int templateId` | 해당 아이템의 호가창/체결 그룹(`tmpl:{id}`) 구독 |
| `UnsubscribeTemplate` | `int templateId` | 구독 해제 |

## Server → Client (이벤트)

| 이벤트 | 페이로드 | 대상 | 트리거 |
|---|---|---|---|
| `OrderBookUpdated` | `OrderBookSnapshotDto` | `tmpl:{id}` 그룹 | 해당 템플릿에 주문 등록/취소/체결로 호가창이 바뀔 때 |
| `TradeExecuted` | `TradeDto` | `tmpl:{id}` 그룹 | 해당 템플릿에서 체결이 발생할 때(체결 1건당 1회) |
| `WalletChanged` | (없음) | `user:{playerId}` | 그 플레이어의 지갑/인벤이 바뀜(체결의 매수·매도자, 주문 등록/취소자) → 클라가 지갑·인벤 재조회 |

## 발행 지점 (서버 구현 노트)

- grain을 SignalR에 결합하지 않는다. **엔드포인트 계층(POST /orders, DELETE /orders,
  admin force-cancel)** 에서 작업 완료 후 `IHubContext<MarketHub>`로 발행한다:
  - 주문/취소 후 `GetSnapshot()` 결과를 `OrderBookUpdated`로 그룹에 push.
  - `PlaceOrderResult.Fills`의 각 체결을 `TradeExecuted`로 push + 매수/매도자에게 `WalletChanged`.
- **단일 인스턴스(co-host)** 에서는 grain과 허브가 같은 프로세스라 `IHubContext` 직접 push로 충분.
- **다중 인스턴스**로 확장 시: SignalR 클라이언트는 두 인스턴스 중 "한쪽"에만 붙는다. REST 를
  처리한 인스턴스가 `IHubContext` 로 발행해도 구독자가 "다른" 인스턴스에 있으면 못 받는다.
  이때 **Redis 백플레인**이 인스턴스 간 브로드캐스트를 중계해 크로스-인스턴스 라이브 푸시를 성립시킨다.

## 다중 인스턴스 실시간 (Redis 백플레인) — ✅ 구현됨 (config-gated)

- `Redis:ConnectionString`(env `Redis__ConnectionString`)이 설정되면 `AddSignalR()` 에
  `AddStackExchangeRedis(conn)` 백플레인을 붙인다(`Program.cs`). **비어있으면(기본) 기존과 완전히
  동일한 인메모리 단일 인스턴스** 동작이라 기존 테스트/데모/성능 경로는 불변(비파괴적 스위치).
- 패키지: `Microsoft.AspNetCore.SignalR.StackExchangeRedis`. 인프라: `docker-compose.yml` 의
  `redis`(redis:7) 선택 서비스. 데모: `scripts/run-cluster.sh` 가 **2 인스턴스**(Orleans adonet
  클러스터링 + 전용 ClusterId `item-market-cluster` + 전용 DB `item_market_cluster` + Redis 백플레인)를
  띄운다(라이브 데모 `:5080`/`item_market` 와 완전 분리).
- **크로스-인스턴스 실증(실측)**: 클라이언트를 인스턴스 A(`:5091`)의 hub 에 붙여
  `SubscribeTemplate(1)` 한 뒤, 매칭되는 sell+buy 를 인스턴스 B(`:5092`)의 REST 로 등록.
  - **백플레인 ON**: A 의 클라이언트가 `OrderBookUpdated` + `TradeExecuted` **수신** → B→A 중계 성립.
  - **백플레인 OFF**: 동일 시나리오에서 B 의 REST 는 여전히 체결(fills=1, Orleans 라우팅은 무관)되지만
    A 의 클라이언트는 두 이벤트를 **모두 미수신** → 백플레인이 크로스-인스턴스 푸시의 결정적 요소임을 증명.

## 프론트 동작

- `@microsoft/signalr`로 연결(자동 재연결). 아이템 상세 진입 시 `SubscribeTemplate`,
  이탈 시 `UnsubscribeTemplate`.
- `OrderBookUpdated`/`TradeExecuted` 수신 시 폴링 없이 화면 갱신, `WalletChanged` 수신 시
  지갑/인벤 스토어 재조회. 연결 상태 인디케이터(“LIVE”) 표시.

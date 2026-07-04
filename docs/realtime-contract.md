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
- **다중 인스턴스**로 확장 시: 클라이언트가 붙은 API 인스턴스와 grain 소유 실로가 다를 수 있으므로
  **Redis 백플레인**(`AddStackExchangeRedis`)이 필요하다. (지금은 미도입, 확장 로드맵.)

## 프론트 동작

- `@microsoft/signalr`로 연결(자동 재연결). 아이템 상세 진입 시 `SubscribeTemplate`,
  이탈 시 `UnsubscribeTemplate`.
- `OrderBookUpdated`/`TradeExecuted` 수신 시 폴링 없이 화면 갱신, `WalletChanged` 수신 시
  지갑/인벤 스토어 재조회. 연결 상태 인디케이터(“LIVE”) 표시.

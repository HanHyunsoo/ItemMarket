# API 계약 (Front ↔ Back 프로토콜)

아포칼립스 익스트랙션 슈터 테마의 아이템 거래소. 모든 응답은 공통 봉투
`ApiResponse<T>`(`Success` / `Data` / `Error`)로 감싼다. 열거형은 문자열로 직렬화한다.

- 인증: **JWT (Bearer)**. `POST /api/auth/login` 으로 토큰 발급 → 이후 모든 요청에
  `Authorization: Bearer <token>`. 플레이어 식별은 토큰의 `sub`(playerId) 클레임을
  서버가 신뢰(헤더 스푸핑 불가). HS256 대칭키 서명.
- 인가: 어드민 엔드포인트(`/api/admin/*`)는 `admin` 롤 클레임 필요(없으면 403).
- 금액 단위: 병뚜껑(CAP), `long`(정수).
- 오류: `Error.Code`(열거형, `ErrorCode`)로 분기, `Error.Message`로 표시.
  인증 실패는 `Unauthorized`(401), 권한 부족은 `Forbidden`(403).
- 실시간: 호가창/체결/지갑 변경은 SignalR 허브 `/hubs/market`로 서버 푸시한다 — 계약은 [docs/realtime-contract.md](./realtime-contract.md) 참고.

## 인증 엔드포인트

| 메서드 | 경로 | 바디 | 응답 `Data` |
|---|---|---|---|
| POST | `/api/auth/login` | `LoginRequest` | `TokenResponse` (AccessToken/Roles 등) |

> 개발 스코프: 비밀번호 없이 시드 플레이어 ID로 로그인. 어드민 롤은 설정값
> `Auth:AdminPlayerId`(기본 `33333333-...-333333333333` Trader_Charlie)에 부여.

## 플레이어용 엔드포인트

| 메서드 | 경로 | 바디 | 응답 `Data` |
|---|---|---|---|
| GET | `/api/catalog` | - | `ItemTemplateDto[]` (아이템 마스터 102종) |
| GET | `/api/wallet` | - | `WalletDto` (현재 플레이어) |
| GET | `/api/wallet/ledger?page=&size=` | - | `PagedResult<WalletLedgerEntryDto>` |
| GET | `/api/inventory` | - | `InventoryDto` (스택 + 유니크 인스턴스) |
| GET | `/api/stash` | - | `StashDto` (그리드 배치 + 미배치, 현재 플레이어) |
| POST | `/api/stash/move` | `MoveStashItemRequest` | `StashDto` (이동 후 스냅샷) |
| GET | `/api/market/{templateId}/book` | - | `OrderBookSnapshotDto` (호가창) |
| GET | `/api/market/{templateId}/trades?page=&size=` | - | `PagedResult<TradeDto>` (체결 내역) |
| POST | `/api/orders` | `PlaceOrderRequest` | `PlaceOrderResult` (잔여 주문 + 즉시 체결분) |
| GET | `/api/orders` | - | `OrderDto[]` (내 주문) |
| GET | `/api/orders/{id}` | - | `OrderDto` |
| DELETE | `/api/orders/{id}` | - | `OrderDto` (취소, 에스크로 환불) |

## 운영(어드민) 엔드포인트

| 메서드 | 경로 | 바디 | 응답 `Data` |
|---|---|---|---|
| GET | `/api/admin/players/{id}/wallet` | - | `WalletDto` |
| POST | `/api/admin/wallet/adjust` | `AdminAdjustWalletRequest` | `WalletDto` |
| POST | `/api/admin/grant/stack` | `AdminGrantStackRequest` | `InventoryDto` |
| POST | `/api/admin/grant/instance` | `AdminGrantInstanceRequest` | `ItemInstanceDto` |
| POST | `/api/admin/orders/force-cancel` | `AdminForceCancelOrderRequest` | `OrderDto` |
| GET | `/api/admin/orders?templateId=&status=&page=&size=` | - | `PagedResult<OrderDto>` |
| GET | `/api/admin/trades?page=&size=` | - | `PagedResult<TradeDto>` |

## 주문/매칭 규칙

- **매수(BUY)**: `UnitPrice`는 상한가. 등록 시 `UnitPrice × Quantity` 병뚜껑을 에스크로(잠금).
  가장 싼 매도 호가부터 체결하며, 체결가가 상한가보다 낮으면 차액은 환불.
- **매도(SELL)**:
  - 스택형(FOOD/MEDICAL/AMMO): `InstanceId=null`, 수량만큼 인벤에서 차감(에스크로).
  - 유니크(MELEE/GUN): `InstanceId` 지정, `Quantity=1`. 해당 인스턴스를 에스크로.
- **가격-시간 우선(price-time priority)**: 같은 가격이면 먼저 등록된 주문 우선.
- **자전거래 금지**: 본인의 잔존 주문과는 체결되지 않는다(건너뛰고 다음 호가와 매칭).
  교차하는 본인 매수/매도는 체결 없이 호가창에 공존할 수 있다.
- **한도**: 단가 ≤ 1,000,000,000,000 CAP, 수량 ≤ 1,000,000, 주문 총액(단가×수량) ≤ 10^15 CAP.
  초과 시 `ValidationError`.
- **부분 체결**: 남은 물량은 호가창에 잔존(`PartiallyFilled`).
- **수수료**: 체결 시 판매 대금의 `fee_bps`(기본 5%)를 판매자 수령액에서 차감·소각(sink).

## 그리드 스태시 규칙

- **고정 그리드**: 플레이어당 **10칸(폭) × 12칸(높이)**. 좌상단 (0,0) 기준.
  크기는 `StashDto.GridW/GridH`로 반환.
- **footprint**: 아이템은 좌상단 `(x,y)`에서 템플릿의 `grid_w × grid_h` 칸을 차지한다.
  스택형(FOOD/MEDICAL/AMMO)은 (플레이어, 템플릿)당 **1×1** 한 칸, 유니크(MELEE/GUN)는
  인스턴스별로 템플릿 footprint(예: AK-47 4×2)를 차지한다.
- **자동 배치**: `GET /api/stash`는 소유 아이템 중 아직 배치되지 않은 것을 좌상단→오른쪽→아래
  순서로 스캔해 first-fit으로 자동 배치·영속화한다. 이미 배치된 아이템은 자리를 유지한다.
  그리드가 가득 차 들어갈 자리가 없는 항목은 `Unplaced`(대기 트레이)로 반환된다.
- **이동(`POST /api/stash/move`)**: 서버 권위 검증 — 소유권 + 경계(footprint가 그리드 안) +
  겹침(다른 배치와 충돌 금지, 이동 대상 자신은 제외). 위반 시 `PlacementInvalid`(400).
  플레이어당 grain 단일 활성화로 동시 이동이 직렬화된다(락 불필요).
- 대상 지정: 스택 이동은 `Kind=Stack`+`TemplateId`, 유니크 이동은 `Kind=Instance`+`InstanceId`.

## 오류 코드 (`ErrorCode`)

`ValidationError` · `PlayerNotFound` · `TemplateNotFound` · `InstanceNotFound` ·
`InstanceNotOwned` · `InsufficientFunds` · `InsufficientQuantity` · `OrderNotFound` ·
`OrderNotOwned` · `OrderAlreadyClosed` · `StackableMismatch` · `PlacementInvalid`

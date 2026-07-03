# API 계약 (Front ↔ Back 프로토콜)

아포칼립스 익스트랙션 슈터 테마의 아이템 거래소. 모든 응답은 공통 봉투
`ApiResponse<T>`(`Success` / `Data` / `Error`)로 감싼다. 열거형은 문자열로 직렬화한다.

- 인증: (1주 스코프) 생략. 플레이어 식별은 `X-Player-Id: <uuid>` 헤더.
- 금액 단위: 병뚜껑(CAP), `long`(정수).
- 오류: `Error.Code`(열거형, `ErrorCode`)로 분기, `Error.Message`로 표시.

## 플레이어용 엔드포인트

| 메서드 | 경로 | 바디 | 응답 `Data` |
|---|---|---|---|
| GET | `/api/catalog` | - | `ItemTemplateDto[]` (아이템 마스터 102종) |
| GET | `/api/wallet` | - | `WalletDto` (현재 플레이어) |
| GET | `/api/wallet/ledger?page=&size=` | - | `PagedResult<WalletLedgerEntryDto>` |
| GET | `/api/inventory` | - | `InventoryDto` (스택 + 유니크 인스턴스) |
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
- **부분 체결**: 남은 물량은 호가창에 잔존(`PartiallyFilled`).
- **수수료**: 체결 시 판매 대금의 `fee_bps`(기본 5%)를 판매자 수령액에서 차감·소각(sink).

## 오류 코드 (`ErrorCode`)

`ValidationError` · `PlayerNotFound` · `TemplateNotFound` · `InstanceNotFound` ·
`InstanceNotOwned` · `InsufficientFunds` · `InsufficientQuantity` · `OrderNotFound` ·
`OrderNotOwned` · `OrderAlreadyClosed` · `StackableMismatch`

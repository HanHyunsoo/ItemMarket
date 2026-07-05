# API 계약 (Front ↔ Back 프로토콜)

아포칼립스 익스트랙션 슈터 테마의 아이템 거래소. 모든 응답은 공통 봉투
`ApiResponse<T>`(`Success` / `Data` / `Error`)로 감싼다. 열거형은 문자열로 직렬화한다.

> **인터랙티브 문서(Swagger/OpenAPI)**: API를 띄운 뒤 [`/swagger`](http://localhost:5080/swagger)
> 에서 모든 엔드포인트를 그룹(Auth/Market/Wallet/Orders/Stash/Inventory/Admin)별로 보고 직접
> 호출할 수 있다. 우측 상단 **Authorize** 에 로그인으로 받은 액세스 토큰을 넣으면 JWT Bearer로
> 보호된 엔드포인트를 시험할 수 있다.

- 인증: **JWT (Bearer)**. `POST /api/auth/login` 으로 토큰 발급 → 이후 모든 요청에
  `Authorization: Bearer <token>`. 플레이어 식별은 토큰의 `sub`(playerId) 클레임을
  서버가 신뢰(헤더 스푸핑 불가). HS256 대칭키 서명.
- 인가: 어드민 엔드포인트(`/api/admin/*`)는 `admin` 롤 클레임 필요(없으면 403).
- 금액 단위: 병뚜껑(CAP), `long`(정수).
- 오류: `Error.Code`(열거형, `ErrorCode`)로 분기, `Error.Message`로 표시.
  인증 실패는 `Unauthorized`(401), 권한 부족은 `Forbidden`(403).
- 실시간: 호가창/체결/지갑 변경은 SignalR 허브 `/hubs/market`로 서버 푸시한다 — 계약은 [docs/realtime-contract.md](./realtime-contract.md) 참고.
- 멱등성: `POST /api/orders`는 선택 헤더 `Idempotency-Key`를 지원한다(아래 참고).
- 레이트 리밋: `POST /api/orders`는 플레이어별 한도 초과 시 `429`(`ApiResponse` 실패 봉투, `RateLimited`)를 반환한다.

## 인증 엔드포인트

| 메서드 | 경로 | 바디 | 응답 `Data` |
|---|---|---|---|
| POST | `/api/auth/login` | `LoginRequest` | `TokenResponse` (액세스+리프레시 쌍) |
| POST | `/api/auth/refresh` | `RefreshRequest` | `TokenResponse` (로테이션된 새 쌍) |
| POST | `/api/auth/logout` | `RefreshRequest` | `bool` (리프레시 토큰 폐기) |

> 개발 스코프: 비밀번호 없이 시드 플레이어 ID로 로그인. 어드민 롤은 설정값
> `Auth:AdminPlayerId`(기본 `33333333-...-333333333333` Trader_Charlie)에 부여.

### 토큰 수명과 리프레시(로테이션)

로그인은 **짧은 액세스 토큰**과 **긴 리프레시 토큰**을 함께 발급한다.

- **액세스 토큰(JWT, HS256)**: 기본 15분(`Auth:AccessTokenMinutes`). 모든 보호 요청의
  `Authorization: Bearer` 에 사용. `TokenResponse.accessTokenExpiresIn`(초)로 만료를 안내.
- **리프레시 토큰**: 기본 14일(`Auth:RefreshTokenDays`). CSPRNG 256비트 난수 원문을
  클라이언트만 보관하고, 서버 DB(`refresh_token`)에는 **SHA-256 해시만** 저장한다(원문 미저장).
- **갱신(`POST /api/auth/refresh`)**: 제시된 리프레시 토큰을 해시로 조회해 (존재·미폐기·미만료)
  검증한 뒤 **로테이션**한다 — 옛 토큰을 `revoked=true`로 폐기하고 **새 액세스+리프레시 쌍**을
  발급한다. 폐기는 `revoked=false`인 행만 원자적으로 업데이트해 동시 회전 레이스를 막는다.
- **로그아웃(`POST /api/auth/logout`)**: 제시된 리프레시 토큰을 폐기(멱등). 플레이어 전환 시에도
  이전 세션의 토큰을 폐기한다.
- **재사용 탐지**: 이미 폐기된(회전이 끝난) 리프레시 토큰이 다시 제시되면 탈취 정황으로 보고
  **해당 플레이어의 리프레시 토큰 체인 전체를 폐기**한 뒤 `Unauthorized`(401)를 반환한다.
  만료/없음/폐기된 토큰도 모두 401.
- **프론트엔드**: axios 응답 인터셉터가 401을 만나면 리프레시를 **한 번** 시도하고 원요청을 재시도한다
  (동시 401은 single-flight로 한 번만 회전). 리프레시 실패 시 세션을 비우고 재로그인을 요구한다.
  SignalR `accessTokenFactory`는 저장소의 최신 액세스 토큰을 읽어 (재)연결 시 갱신분을 사용한다.

## 플레이어용 엔드포인트

| 메서드 | 경로 | 바디 | 응답 `Data` |
|---|---|---|---|
| GET | `/api/catalog` | - | `ItemTemplateDto[]` (아이템 마스터 102종) |
| GET | `/api/wallet` | - | `WalletDto` (현재 플레이어) |
| GET | `/api/wallet/ledger?page=&size=` | - | `PagedResult<WalletLedgerEntryDto>` |
| GET | `/api/inventory` | - | `InventoryDto` (스택 + 유니크 인스턴스) |
| GET | `/api/stash` | - | `StashDto` (STASH 컨테이너, 하위호환) |
| GET | `/api/stash/{container}` | - | `StashDto` (지정 컨테이너: `stash`\|`loadout`, 대소문자 무시) |
| POST | `/api/stash/move` | `MoveStashItemRequest` | `StashDto` (이동 후 `ToContainer` 스냅샷) |
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

## 그리드 스태시 규칙 (컨테이너: STASH / LOADOUT)

- **컨테이너 2종**: 플레이어당 두 개의 그리드가 있다.
  - `STASH` = 안전 보관소 **10×12**. 소유 아이템의 기본 보관 위치.
  - `LOADOUT` = 레이드에 들고 나가는 칸 **6×8**. 비어서 시작하며 이동(반입/반출)으로만 채워진다.
  - 크기는 `StashDto.GridW/GridH`, 어느 컨테이너인지는 `StashDto.Container`(`Stash`\|`Loadout`).
  - 각 배치(`StashPlacementDto`)도 `Container`를 갖는다. 열거값은 PascalCase로 직렬화.
- **footprint**: 아이템은 좌상단 `(x,y)`에서 템플릿의 `grid_w × grid_h` 칸을 차지한다.
  스택형(FOOD/MEDICAL/AMMO)은 컨테이너당 **1×1** 한 칸(+그 컨테이너에 담긴 `Quantity`),
  유니크(MELEE/GUN)는 인스턴스별로 템플릿 footprint(예: AK-47 4×2)를 차지한다.
- **소유권 모델(중복 없음)**: 아이템(스택 수량의 일부 / 인스턴스)은 **정확히 한 컨테이너**에 놓인다.
  컨테이너 배치는 조직화(위치/반입 여부)일 뿐이고, 총 소유량의 진실은 인벤토리다 →
  `GET /api/inventory` 총량은 컨테이너 이동과 무관하게 항상 보존된다(반입/반출로 늘거나 줄지 않음).
- **자동 배치**: `GET /api/stash/{container}`는 아직 어디에도 배치되지 않은 소유 아이템을
  좌상단→오른쪽→아래 first-fit으로 **STASH에** 자동 배치·영속화한다(어느 컨테이너를 조회해도
  누락분은 STASH로 회수 → 유실 방지). LOADOUT은 자동 배치 대상이 아니다. 이미 배치된 아이템은
  자리를 유지하며, STASH가 가득 차 못 들어간 항목은 STASH 뷰의 `Unplaced`로 반환된다.
- **이동(`POST /api/stash/move`)**: `FromContainer`/`ToContainer`로 두 가지를 모두 처리한다.
  - **같은 컨테이너 재배치**(`From==To`): 위치만 갱신.
  - **컨테이너 간 이동**(`From!=To`, stash↔loadout = 반입/반출): 원본에서 빼고 대상에 넣는 것을
    **원자적으로** 수행한다.
  - 서버 권위 검증 — 소유권 + 경계(footprint가 대상 컨테이너 안) + 겹침(대상 컨테이너의 다른
    배치와 충돌 금지, 이동 대상 자신은 제외). 위반 시 `PlacementInvalid`(400).
  - 응답은 `ToContainer`의 이동 후 스냅샷. 플레이어당 grain 단일 활성화로 모든 컨테이너 조작이
    직렬화된다(컨테이너 간 이동 포함, 락 불필요).
- **대상 지정 & 수량**:
  - 스택 이동은 `Kind=Stack`+`TemplateId`, 유니크 이동은 `Kind=Instance`+`InstanceId`.
  - 스택의 **컨테이너 간 부분 이동**은 `Quantity`로 옮길 개수를 지정(미지정 시 원본 컨테이너의
    전체 수량). 대상 컨테이너에 같은 스택 칸이 이미 있으면 그 칸에 수량이 합산된다(위치 유지).
  - 유니크 인스턴스는 항상 통째로 이동하며 `Quantity`는 무시된다.
  - 필드(`FromContainer`/`ToContainer`/`Quantity`)는 모두 선택적이며 기본값은 STASH/STASH/전체라,
    기존 단일-스태시 이동 호출과 호환된다.

## 멱등성 (`POST /api/orders`)

재시도/중복 제출(네트워크 재전송, 더블클릭)로 주문이 두 번 등록되는 것을 막는다.

- 요청에 `Idempotency-Key: <임의 문자열>` 헤더를 붙인다(선택). 헤더가 없으면 기존 동작 그대로.
- 키는 **플레이어별**로 유일해야 한다. 슬롯은 `(player_id, key)`로 관리한다.
- 처리 흐름:
  1. 헤더가 있으면 `(player_id, key)` 슬롯을 원자적으로 선점(`INSERT ... ON CONFLICT DO NOTHING`).
  2. 선점 성공(원본): 주문을 등록하고 직렬화된 `ApiResponse<PlaceOrderResult>` JSON을 저장 후 반환.
  3. 선점 실패(중복): 저장된 응답을 **그대로** 반환한다(주문 재등록 없음).
  4. 원본이 아직 처리 중(응답 미저장)이면 `409` + `ApiResponse`(`IdempotencyInProgress`).
  5. 원본이 실패하면 슬롯을 비워 같은 키로 재시도 가능.

## 레이트 리밋 (`POST /api/orders`)

- **플레이어별** 고정창(fixed-window) 리미터. 파티션 키는 토큰의 `sub`(플레이어),
  익명은 원격 IP로 폴백.
- 한도 초과 시 `429 Too Many Requests` + 표준 `ApiResponse` 실패 봉투(`RateLimited`),
  `Retry-After` 헤더 포함.
- 설정(`appsettings` → `RateLimiting:Orders`): `PermitLimit`(기본 1000),
  `WindowSeconds`(기본 10), `QueueLimit`(기본 0). 데모/테스트가 throttle되지 않도록 넉넉한 기본값.

## 오류 코드 (`ErrorCode`)

`ValidationError` · `PlayerNotFound` · `TemplateNotFound` · `InstanceNotFound` ·
`InstanceNotOwned` · `InsufficientFunds` · `InsufficientQuantity` · `OrderNotFound` ·
`OrderNotOwned` · `OrderAlreadyClosed` · `StackableMismatch` · `PlacementInvalid` ·
`RateLimited`(429) · `IdempotencyInProgress`(409)

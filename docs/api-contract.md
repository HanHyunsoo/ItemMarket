# API 계약 (Front ↔ Back 프로토콜)

아포칼립스 익스트랙션 슈터 테마의 아이템 거래소. 모든 응답은 공통 봉투
`ApiResponse<T>`(`Success` / `Data` / `Error`)로 감싼다. 열거형은 문자열로 직렬화한다.

> **인터랙티브 문서(Swagger/OpenAPI)**: API를 띄운 뒤 [`/swagger`](http://localhost:5080/swagger)
> 에서 모든 엔드포인트를 그룹(Auth/Market/Wallet/Orders/Stash/Equipment/Inventory/Raid/Admin)별로 보고 직접
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
| GET | `/api/catalog` | - | `ItemTemplateDto[]` (아이템 마스터 149종(기본 102 + 장비·컨테이너 등 확장)) |
| GET | `/api/wallet` | - | `WalletDto` (현재 플레이어) |
| GET | `/api/wallet/ledger?page=&size=` | - | `PagedResult<WalletLedgerEntryDto>` |
| GET | `/api/inventory` | - | `InventoryDto` (스택 + 유니크 인스턴스) |
| GET | `/api/inventory/ledger?page=&size=` | - | `PagedResult<ItemLedgerEntryDto>` (아이템 이동 원장: RAID_*/ADMIN_GRANT) |
| GET | `/api/stash` | - | `StashDto` (STASH 컨테이너, 하위호환) |
| GET | `/api/stash/{container}` | - | `StashDto` (지정 컨테이너: `stash`\|`pockets`, 대소문자 무시. 중첩 `container`는 인스턴스 id가 필요해 거부(400) — 장비 `GET /api/equipment`로 조회) |
| POST | `/api/stash/move` | `MoveStashItemRequest` | `StashDto` (이동 후 `ToContainer` 스냅샷) |
| POST | `/api/stash/upgrade` | - | `StashUpgradeResultDto` (캡으로 스태시 +6행. 점증 가격 차감(캡 싱크). 부족 시 `InsufficientFunds`, 상한 500 초과 시 `ValidationError`) |
| GET | `/api/equipment` | - | `EquipmentDto` (장착 슬롯 + 장착된 백팩/리그의 중첩 그리드) |
| POST | `/api/equipment/equip` | `EquipRequest` | `EquipmentDto` (장착 후 스냅샷) |
| POST | `/api/equipment/unequip` | `UnequipRequest` | `EquipmentDto` (해제 후 스냅샷) |
| GET | `/api/market/tickers` | - | `MarketTickerDto[]` (전 종목 시세 요약: 최우선 매수/매도 호가·최근 체결가/시각·활성 주문 수 + 벤더 참고가(`vendorBid`/`vendorAsk` = base_value±스프레드, 실거래 아님). 마켓 카드 목록용) |
| GET | `/api/leaderboard` | - | `LeaderboardDto` (최다 순자산(지갑+보유 아이템 기준가) + 최다 생환(탈출) 상위 순위) |
| GET | `/api/market/{templateId}/book` | - | `OrderBookSnapshotDto` (호가창) |
| GET | `/api/market/{templateId}/trades?page=&size=` | - | `PagedResult<TradeDto>` (체결 내역) |
| POST | `/api/orders` | `PlaceOrderRequest` | `PlaceOrderResult` (잔여 주문 + 즉시 체결분) |
| GET | `/api/orders` | - | `OrderDto[]` (내 주문) |
| GET | `/api/orders/{id}` | - | `OrderDto` |
| DELETE | `/api/orders/{id}` | - | `OrderDto` (취소, 에스크로 환불) |
| GET | `/api/raid` | - | `RaidSessionDto?` (**ACTIVE 세션만 반환, 없으면 `null`**) |
| GET | `/api/raid/history?page=&size=` | - | `PagedResult<RaidHistoryEntryDto>` (해결된 EXTRACTED/DIED 세션, 최신순) |
| GET | `/api/raid/zones` | - | `ZoneInfoDto[]` (존별 출격 수수료·loot당 사망확률 상승률 — 출격 화면 배당 표시용) |
| POST | `/api/raid/start` | `StartRaidRequest?`(zone, 기본 Med) | `RaidSessionDto` (스태시 밖 전부를 위험으로 잠금, ACTIVE. 존이 드롭 등급·사망확률 결정. **존별 출격 수수료 차감**(캡 싱크) — 잔액 부족 시 `InsufficientFunds`) |
| POST | `/api/raid/loot` | - | `LootResultDto` (서버 드롭: 존 rarity 가중치로 무엇을·얼마나 결정. `{dropped, session}`) |
| POST | `/api/raid/extract` | - | `RaidSessionDto` (탈출 시도 → 마감/누적 사망확률로 EXTRACTED 또는 DIED 판정) |
| POST | `/api/raid/die` | - | `RaidSessionDto` (위험 아이템 소실, DIED) |

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

## 그리드 스태시 규칙 (컨테이너: STASH / LOADOUT / CONTAINER)

- **컨테이너 종류**: 플레이어당 고정 그리드 2종 + 장착된 백팩/리그의 중첩 그리드(가변 개수).
  - `STASH` = 안전 보관소 **10×12**. 소유 아이템의 기본 보관 위치.
  - `LOADOUT` = 레이드에 들고 나가는 칸 **6×8**. 비어서 시작하며 이동(반입/반출)으로만 채워진다.
  - `CONTAINER` = 장착된 백팩/리그의 **내부(중첩) 그리드**. 크기는 그 컨테이너 인스턴스의
    template(`container_w × container_h`, 예: 백팩 5×5·리그 4×3). 특정 컨테이너 인스턴스를 가리키므로
    이동 요청·배치에 `ContainerInstanceId`(장착된 백팩/리그의 인스턴스 id)가 함께 필요하다.
  - 크기는 `StashDto.GridW/GridH`, 어느 컨테이너인지는 `StashDto.Container`(`Stash`\|`Loadout`\|`Container`).
  - 각 배치(`StashPlacementDto`)도 `Container`와(중첩이면) `ContainerInstanceId`를 갖는다. 열거값은 PascalCase로 직렬화.
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
  - **중첩 컨테이너(백팩/리그) 반입·반출·재배치**: `From/ToContainer=Container`로 두고,
    `From/ToContainerInstanceId`에 그 컨테이너(장착된 백팩/리그) 인스턴스 id를 지정한다. 경계는 그
    컨테이너의 `container_w × container_h`로 검증하고, 겹침은 **같은 컨테이너 인스턴스** 안의 다른
    배치만 대상으로 한다. 총 소유량은 여전히 보존된다(중첩 배치도 조직화일 뿐).

## 장비 (`/api/equipment/*`)

- **슬롯 5종**(`EquipSlot`, PascalCase): `Helmet` / `Armor` / `Weapon` / `Backpack` / `Rig`.
  슬롯마다 정확히 한 인스턴스만 장착(`player_equipment`). `GUN` 카테고리는 `Weapon` 슬롯.
- **장착(`POST /api/equipment/equip`, `EquipRequest{Slot, InstanceId}`)**: 서버 권위 검증 —
  인스턴스 소유 + `template.equip_slot == Slot` + 슬롯 미점유. 불일치·점유·미소유 시 각각 명확한
  코드로 거부한다(슬롯 불일치/점유 → `SlotMismatch` **400**, 미소유 → `InstanceNotOwned`, 없음 → `InstanceNotFound` **404**).
  장착된 인스턴스는 그리드에서 빠져(인형 위로 이동) 자동 배치 대상에서 제외된다.
- **해제(`POST /api/equipment/unequip`, `UnequipRequest{Slot}`)**: 슬롯을 비운다. 해제 아이템(및
  백팩/리그였다면 그 내용물)은 **소유** 상태로 남아 다음 `GET /api/stash`에서 STASH로 자동 회수된다(유실 없음).
- **`GET /api/equipment` → `EquipmentDto`**: `Slots`(슬롯→`EquippedItemDto{Slot,InstanceId,TemplateId}`) +
  `Containers`(장착된 백팩/리그의 중첩 그리드 `NestedContainerDto{ContainerInstanceId, TemplateId, Slot,
  GridW, GridH, Placements}`). 중첩 그리드에 아이템을 넣고 빼는 것은 위의 `/api/stash/move`(`Container` 참조)로 한다.
- **동시성**: 장비 조작은 스태시와 **같은 grain(키=playerId)** 에서 처리되어 이동·정합화와 직렬화된다
  (장착이 그리드 배치를 제거하고 정합화가 다시 놓지 않도록 하는 것이 원자적).

## 익스트랙션 레이드 (`/api/raid/*`)

생존형(extraction) 게임의 시그니처 루프를 **서비스 계층 세션 상태기계 + 원자적 정산**으로
모델링한다. 게임플레이 틱/전투는 범위 밖 — 이 백엔드는 게임 서버가 호출하는 서비스이며
`extract`/`die`는 명시적 엔드포인트다(실제 통합에서는 게임 서버가 결과를 알린다).

- **루프**: 플레이어가 **LOADOUT**을 채운다(기존 `POST /api/stash/move`) → **StartRaid** 로 로드아웃을
  "위험(at-risk)"으로 잠근다 → **Extract**(생존, 전량 **원위치로 복원**) 또는 **Die**(위험 아이템 소실).
- **상태기계**: `(없음) --start--> ACTIVE --extract--> EXTRACTED` / `ACTIVE --die--> DIED`.
  `RaidSessionDto.Status`(`Active`\|`Extracted`\|`Died`, PascalCase 직렬화).
- **플레이어당 ACTIVE 세션 1개**: 진행 중 다시 `start`하면 `RaidActive`(**409**). DB의 부분 유니크
  인덱스(`WHERE status='ACTIVE'`)가 최종 강제. `extract`/`die`/`loot`에 ACTIVE 세션이 없으면 `RaidNotFound`(**404**).
- **`GET /api/raid`**: **ACTIVE 세션만** 스냅샷으로 반환하고, 진행 중 레이드가 없으면 `null`이다
  (계약: `null` = 진행 중 레이드 없음). 해결된(EXTRACTED/DIED) 세션은 반환하지 않는다 — 결과 화면은
  `extract`/`die` 응답으로 표시한다.
- **위험(at-risk) 범위**: 로드아웃(LOADOUT) 뿐 아니라 **장착 슬롯 전부**(헬멧/방어구/무기/백팩/리그)와
  **장착된 백팩/리그의 중첩 그리드 내용물**까지 모두 위험이다. 즉 인형 위에 걸친 것과 그 백팩 안의
  것도 StartRaid로 잠기고, Die 시 함께 소실된다. 장착 슬롯은 StartRaid에서 비워진다(생존 시 원위치로 복원).
- **StartRaid(원자적, 매도 에스크로와 동일한 자산 잠금 재사용)**: 위험 스택은 `inventory_stack`에서
  차감, 위험 유니크(로드아웃/장착/중첩 내용물)는 `item_instance.owner_player_id = NULL`. 로드아웃·중첩
  배치와 장착 슬롯을 비우고 위험 스냅샷 `raid_session_item`(source=`BROUGHT`)에 기록한다. 이때 각 반입
  아이템의 **원위치를 함께 스냅샷**한다(`origin_container` = `STASH`/`LOADOUT`/`CONTAINER`/`EQUIP`,
  `origin_container_instance_id`(중첩 백팩·리그), `origin_slot`(장착 슬롯), `origin_x`/`origin_y`(그리드 칸))
  — 생존 시 정확히 그 자리로 되돌리기 위함이다. 위험 아이템은 인벤에서 사라지므로 **레이드 중 판매/이동/배치가
  자동 거부**된다(기존 에스크로 검사가 그대로 거른다).
- **Loot = 서버 드롭테이블(scavenge)**: 클라이언트는 아이템·수량을 정하지 못한다(무한 인플레 차단).
  서버가 세션 **존(zone)의 rarity 가중치**로 등급을 롤하고 그 등급의 `item_template` 중 랜덤 1종을 뽑아
  `LOOTED` 위험 아이템으로 추가한다. 수량은 스택이면 `1..max_stack`(상한 초과 원천 불가), 유니크는
  `item_instance`를 `owner=NULL`·`origin='RAID'`로 materialize(소유는 Extract 시 부여). 응답 `LootResultDto`는
  이번 드롭(`dropped`)과 갱신 세션(`session`)을 함께 준다. loot마다 **존별 사망확률**(Low +8% / Med +12% /
  High +20% /loot)이 오른다. 마감 초과로 loot하면 탈출 실패=사망 정산(`dropped=null`, `session.status=Died`).
- **Extract = 보존 + 원위치 복원**: 반입(`BROUGHT`) + 획득(`LOOTED`) 전량을 소유로 복귀(스택 가산 /
  유니크 owner 복원)한 뒤, 물리 위치를 **한 트랜잭션 안에서** 복원한다 — STASH로 자동 덤프하지 않는다.
  - **반입(BROUGHT)**: StartRaid에서 스냅샷한 `origin_*`으로 정확히 되돌린다 — `EQUIP`은 `player_equipment`
    슬롯으로, `LOADOUT`/`CONTAINER`(백팩·리그 내부)/`STASH`는 `stash_placement`의 원래 칸으로 재삽입.
    즉 생존하면 장비·로드아웃 배치가 레이드 직전 그대로 유지된다.
  - **획득(LOOTED)**: 원위치가 없으므로 **반입 공간에 first-fit 배치**한다. 우선순위는
    **① 장착된 백팩/리그의 중첩 그리드(슬롯 순) → ② LOADOUT → ③ STASH 오버플로**. 스택은 같은 물리
    컨테이너에 동일 템플릿 칸이 있으면 그 칸에 수량을 합산한다. 어느 곳에도 자리가 없으면 미배치로
    남고(소유는 유지) 다음 `GET /api/stash`에서 STASH로 정합화된다. 총량은 항상 보존.
- **Die = at-risk(위험)만 소실**: 위험 아이템 전량 소각(스택 미복귀 / 유니크 tombstone: `owner=NULL`,
  `origin='RAID_LOST'`). **STASH(안전)는 무관**. 소실 자체는 `item_ledger`에 별도 항목을 남기지 않는다
  (반입분은 `RAID_BROUGHT`에서 이미 debit돼 재차감이 이중이고, 전리품은 사전 credit이 없어 유령 음수가 되므로 — 회계 대칭화).
  손실 감사는 `raid_session`(status=DIED) + `raid_session_item` 스냅샷이 보유한다.
- **아이템 원장(`item_ledger`, append-only)**: 세션이 소유량에 준 순변화를 프로버넌스로 기록한다 —
  `RAID_BROUGHT`(반입, -), `RAID_EXTRACT`(회수, +), `RAID_LOOT`(획득 materialize, +). (`RAID_LOSS`는
  enum에 남아 있으나 위 대칭화로 사망 정산에서 더 이상 기록하지 않는다.)
  `wallet_ledger`의 감사 패턴을 아이템에 적용한 것(잔고 컬럼 없는 이동 로그, `ref_id`=세션 id).
- **읽기 엔드포인트(프론트 전적/원장 화면)**:
  - `GET /api/raid/history?page=&size=` → 해결된(EXTRACTED/DIED) 과거 세션을 최신순으로 페이지네이션.
    각 항목(`RaidHistoryEntryDto`)은 `id`/`status`/`startedAt`/`resolvedAt`과 그 세션의 아이템 스냅샷
    (`source`=BROUGHT/LOOTED + `quantity`)을 포함한다. ACTIVE 세션은 제외(진행 중은 `GET /api/raid`).
  - `GET /api/inventory/ledger?page=&size=` → `item_ledger`를 최신순 페이지네이션(`ItemLedgerEntryDto`:
    `reason`(RAID_BROUGHT/RAID_EXTRACT/RAID_LOOT/RAID_LOSS/ADMIN_GRANT), `templateId`, `instanceId?`,
    `deltaQty`, `createdAt`).

## 멱등성 (`POST /api/orders`)

재시도/중복 제출(네트워크 재전송, 더블클릭)로 주문이 두 번 등록되는 것을 막는다.

- 요청에 `Idempotency-Key: <임의 문자열>` 헤더를 붙인다(선택). 헤더가 없으면 기존 동작 그대로.
- 키는 **플레이어별**로 유일해야 한다. 슬롯은 `(player_id, key)`로 관리한다.
- **저장소는 Redis**(`IIdempotencyStore` → `RedisIdempotencyStore`). 키는 `idem:{playerId}:{key}`,
  TTL은 `Idempotency:TtlMinutes`(기본 60분). Redis 미구성(단일 인스턴스 개발) 시에는 무저장
  `NullIdempotencyStore`로 폴백해 헤더를 사실상 무시한다(중복 방어 없음).
- 처리 흐름:
  1. 헤더가 있으면 슬롯을 원자적으로 선점(`SET idem:{playerId}:{key} INFLIGHT NX EX ttl`).
  2. 선점 성공(원본): 주문을 등록하고 직렬화된 `ApiResponse<PlaceOrderResult>` JSON을 슬롯에 저장 후 반환.
  3. 선점 실패(중복, 저장된 응답 존재): 저장된 응답을 **그대로** 반환한다(주문 재등록 없음).
  4. 원본이 아직 처리 중(값이 `INFLIGHT` 마커)이면 `409` + `ApiResponse`(`IdempotencyInProgress`).
  5. 원본이 실패하면 슬롯을 삭제(`DEL`)해 같은 키로 재시도 가능.

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
`OrderNotOwned` · `OrderAlreadyClosed` · `StackableMismatch` · `SlotMismatch`(장비 슬롯 불일치/점유) ·
`PlacementInvalid` · `RateLimited`(429) · `IdempotencyInProgress`(409) · `RaidActive`(409) · `RaidNotFound`(404)

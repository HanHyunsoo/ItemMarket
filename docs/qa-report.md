# QA 리포트 — Wasteland Exchange

> 대규모 리팩터(주머니·가변 스태시·다중 스택·레이드 at-risk 재설계·아이템 149종/스프라이트 다각화) 직후
> 실시한 QA 패스 결과. **기능 QA**(정합성·버그, 코드 리뷰 + 라이브 검증)와 **fun QA**(플레이테스터/디자이너
> 관점, 라이브 플레이) 두 트랙으로 진행했다. 이 문서는 **이슈 수집용**이며, 수정은 후속 작업으로 남긴다.
> 심각도·상태(확인/이론/의도됨)를 함께 표기했으니 우선순위 판단에 사용할 것.

작성 시점 기준 테스트/빌드는 전부 green(`dotnet test` 95, `web build`/`lint`). 아래는 그 위에서 발견한
개선 여지다.

---

## 0. 우선순위 요약 (권장 수정 순서)

| 순위 | 항목 | 트랙 | 심각도 | 성격 |
|---|---|---|---|---|
| 1 | 레이드에 리스크가 없음(무료·무한 전리품, 사망은 수동 클릭만) | fun | ★★★ | 설계 |
| 2 | 경제가 무한 팽창(무료 전리품 → 무한 병뚜껑) | fun | ★★★ | 설계 |
| 3 | 사망 시 `item_ledger` 이중 차감(반입분) — 원장 정합성 | 기능 | ★★ | 버그(확인) |
| 4 | 마켓 카드가 시세가 아닌 고정 `base_value` 표시 | fun | ★★ | 설계/UX |
| 5 | `AddLoot`/`StartRaid` 응답 `StartedAt` 시계 불일치 | 기능 | ★★ | 버그(확인) |
| 6 | 첫 세션 온보딩 부재("스프레드시트에 던져짐") | fun | ★★ | UX |
| 7 | 멱등성 헤더가 무시됨(이 배포는 Redis 미구성 → 중복 주문 생성) | 기능 | ★★ | 버그(확인) |
| 8 | OpenAPI가 enum을 정수로 선언(런타임은 문자열) — 생성 클라이언트 역직렬화 실패 | 기능 | ★★ | 계약(확인) |
| 9 | `GET /api/stash/container` → 500(400이어야) | 기능 | ★ | 버그(확인) |
| 10 | 계약 문서 불일치(Quantity 풀 의미·AddLoot.Kind·GET /api/raid 주석) | 기능 | ★ | 문서 |
| 11 | 나머지 견고성/엣지(fee_bps 상한, 이중환불 창, 레이트리밋 임계 등) | 기능 | ★ | 견고성 |
| 12 | 폴리시(중복 언어 네비, Stash 명칭 충돌 등) | fun | ★ | UX |

> **의도된 동작(버그 아님, 문서만 손보면 됨)**: `GET /api/raid`가 ACTIVE-only(해결 후 null) — 이전에
> 의도적으로 바꾼 계약. 인터페이스 주석이 낡았을 뿐. / 가격밴드 ON 시 밴드 격리 매칭 — 문서화된
> 트레이드오프이며 기본값 OFF.

---

## A. 기능 QA (정합성 · 버그)

방법: 영역별로 코드를 end-to-end 정독(매칭·지갑 / 레이드 at-risk / 스태시·그리드·다중 스택) + 일부 라이브
재현. 심각도: ★★★ Critical / ★★ High·Medium / ★ Low.

### A-1. 레이드 (`MarketRepository.cs`)

**BUG A (★★, 확인) — 사망 시 반입분 원장 이중 차감.**
`item_ledger`는 잔고형 원장(부호 있는 delta 합 = 보유량)이다. 반입 아이템은 **출격 때 한 번**
(`RaidBrought -qty`, 실제 `inventory_stack` 차감 동반) 차감되고, **사망 때 또 한 번**(`RaidLoss -qty`,
이번엔 인벤 변화 없음) 차감된다.
- 재현: X 100개 지급 → 원장 100. 출격 40(`RaidBrought -40`, 보유 60) → 사망(`RaidLoss -40`). 원장
  합 20인데 실제 보유 60. **원장이 손실을 반입량만큼 과대 표기.** (실제 아이템 손실은 정확 — 원장
  정합성만 문제.) 탈출 경로는 `-qty` 후 `+qty`로 상쇄되어 정상.
- 부수: 전리품(LOOTED)을 얻고 사망하면 사전 양(+) 항목 없이 `RaidLoss`만 남아 원장에 유령 음수.
- 위치: 출격 `MarketRepository.cs:1111`(스택)/`:1148`(유니크), 사망 `:1307`/`:1315`.
- 방향(참고): 사망 시 반입분은 원장 재기입을 생략(이미 출격 때 debit)하고, 전리품 사망분만 별도 처리.

**BUG B (★★, 확인) — `AddLoot`/`StartRaid` 응답의 `StartedAt`가 앱 시계값.**
`AddLootAsync`가 `LoadRaidDtoAsync(..., DateTimeOffset.UtcNow, ...)`로 DTO를 만들어(`:1230`), 매 loot
호출 응답의 `StartedAt`가 그 순간 시각이 된다(세션 실제 시작이 아님). `GET /api/raid`(DB `started_at`,
`:1013`)·history와 불일치. `StartRaidAsync`도 동일 패턴(`:1159`, 시작 시점이라 영향 경미). 해결 시
`resolvedAt`도 앱 시계(`:1331`) vs DB `now()`(`:1333`) 미세 불일치.
- 방향: `LoadRaidDtoAsync`가 `started_at`/`resolved_at`을 DB에서 읽도록.

**BUG C (★, 문서) — `GET /api/raid` 인터페이스 주석 불일치(코드는 의도된 동작).**
`IRaidSessionGrain.cs:16`·`RaidEndpoints.cs:18` 주석은 "ACTIVE 우선, 없으면 최근 세션"이라 하지만
`GetRaidSnapshotAsync`는 `status='ACTIVE'`만 반환하고 없으면 null(`:1012-1017`). **코드가 맞다**(이전에
의도적으로 ACTIVE-only로 확정). → 인터페이스/엔드포인트 주석을 코드에 맞게 수정.

**잠재 위험(현재 무해) — 다중 스택 반입 시 DELETE 광범위.** 출격의 브라 스택 placement DELETE가 x/y
필터 없이 `(container, template, cid)` 전체를 지운다(`:1112`). 지금은 `stackItems`를 사전에 메모리로
읽어 스냅샷하므로 안전하지만, 루프 중 재조회로 바뀌면 같은 컨테이너의 다른 칸 스택이 유실될 수 있음.

**계약 불일치 — `AddLootRequest.Kind`가 무시됨.** 서버는 `template.stackable`로 분기하고 요청의
`Kind`를 안 본다(`:1200-1202`). 문서(`RaidDtos.cs:61-66`)는 Kind로 분기한다고 설명 → 문서/구현 불일치.

**BUG D (★, 확인) — loot 시점에 `max_stack` 상한이 없음.** `AddLootAsync`는 수량을 평평한 `1..1_000_000`
범위로만 검증(`:1205`)하고, 단일 loot 호출로 100만짜리 스택을 만들 수 있다. `max_stack` 분할은 **탈출
배치 때(`RestoreExtractedPlacementsAsync`, `:1464-1469`)만** 적용된다. 정합성 자체는 유지되나(탈출 시
분할·귀속), loot 진행 중 스냅샷 수량이 상한을 초과해 다중-스택 불변식과 어긋난다. (fun QA의 "무료·무한
전리품" 문제와 결이 같음 — 애초에 loot 소스에 게이트가 없다는 점.) → loot 수량도 `max_stack` 기준
검증/분할.

**확인된 정상 동작:** 장비만 착용해도 출격 성공 / at-risk 전무 시 `RaidNothingToDeploy`(400) / 이중
출격 차단(사전체크+부분유니크+단일활성화) / 탈출 시 반입=원위치 복원·전리품 first-fit(nested→pockets→
stash), 안 들어가면 unplaced로 남고 손실 없음 / 사망 시 스태시 불가침 / 재수화 중복배치 없음 / 백팩
벗기(레이드 밖) 시 내용물 스태시 회수(손실 없음).

### A-2. 주문 매칭 · 지갑 (`OrderBookEngine.cs`, `MarketRepository.cs`)

**FINDING A (★★, 의도된 트레이드오프·기본 OFF) — 가격밴드 ON 시 교차-밴드 매칭 실패 + 비최우선 체결.**
`PriceBandSize>0`이면 주문이 자기 밴드(`unitPrice/bandSize`)만 본다(`OrderBookGrain.cs:105`,
`MarketRepository.cs:706`).
- 재현(band 100): ask 150(band1) 대기, bid 250(band2) → band2 그레인은 band1을 못 봐 **교차인데 미체결**.
  또한 ask 210(band2)/150(band1)에 buy 250 → 210에 체결(최우선 150 두고 과지불).
- 이는 밴드 샤딩의 **명시적 트레이드오프**(문서화됨)이고 기본 `PriceBandSize:0`이라 OFF. 활성 시 가격-
  시간 우선 계약이 깨진다는 점만 인지.

**FINDING C (★, 견고성) — `fee_bps` 상한 없음.** 어드민 config가 `>10000`이면 `fee>gross`라 판매자 지갑이
음수가 될 수 있음(`SettleFillAsync:933`에 비음수 가드 없음). 정합성(소각==차감)은 유지되나 상태 무효.
어드민 게이트라 severity 제한. → 진입 시 `[0, 10000]` 클램프.

**FINDING D (★, 희귀) — 에스크로 후 주문 INSERT '커밋되었으나 예외' 시 이중 환불 창.** 에스크로 커밋 →
별도 트랜잭션 INSERT가 DB엔 커밋됐는데 호출이 예외를 던지면, catch가 에스크로를 환불하는데 주문 행은
OPEN·`escrow_caps>0`로 남아 이후 체결 시 이미 환불된 캡으로 지급 → 캡 생성 가능. 트리거 매우 어려움.
→ 멱등 정산/보상 재조정 or 에스크로+INSERT 동일 트랜잭션.

**FINDING E (★, minor) — 코디네이터가 가격 검증 전에 밴드 라우팅.** 음수/범위밖 `UnitPrice`도 밴드 그레인을
띄운 뒤 거절(`OrderBookGrain.cs:69`). 자산 영향 없음, 낭비 활성화.

**확인된 정상 동작(의심 해소):** fee 정수 절삭이 정합성 **안 깨뜨림**(같은 fee값이 판매자 차감액=소각액,
합 0 — 증명 포함) / 동시성 이중판매 없음(밴드 OFF는 비reentrant 직렬화, ON은 주문이 단일 밴드에 귀속) /
데드락 락 순서 일관(wallet→order, player_id 오름차순) / 취소 환불 정확(부분체결·차익 포함) / 자전거래 스킵
정확 / 체결가=메이커가·테이커 차익 환불 정확 / 모든 DTO 생성자 필드 순서 일치(불일치 없음).

### A-3. 스태시 · 그리드 · 다중 스택 (`StashGeometry.cs`, `StashGrain.cs`, `MarketRepository.cs`)

**BUG 1 (★, 이론/잠재) — 스택형 다중 셀 footprint 무시(하드코딩 1×1).** 모든 기하 경로가 스택을 1×1로
가정(`StashGrain.cs:88,229,275,309,370`). 현재 시드의 스택형(FOOD/MEDICAL/AMMO)은 전부 1×1이라 **재현
불가(이론적)**. 향후 grid>1×1 스택형을 추가하면 경계/겹침 검사가 좌상단 칸만 보아 오배치 발생. → 스택도
`Ctx.Footprints` 참조하도록.

**BUG 2 (★, 확인) — `GET /api/stash/container` → 500.** `"container"`가 `GridContainer.Container`로 파싱되고
(`StashEndpoints.cs:35`), `GetStash`가 `InstanceId=null`로 `DimsOf`→`NestedDims[null!.Value]` 접근해
`InvalidOperationException`(500). → `ParseContainer`가 `Container`를 거절하거나 `GetStash`에서 검증(400).

**BUG 3 (★, minor) — 중첩 컨테이너 그리드를 직접 읽는 HTTP 라우트 없음.** `IStashGrain.GetContainer(Guid)`는
있으나 라우트 미노출. 스태시/주머니만 읽을 수 있고 백팩/리그 내부는 **장비 엔드포인트(`GET /api/equipment`
→ `NestedContainerDto`)로만** 조회 가능. 이동은 되는데 개별 읽기는 우회 필요.

**BUG 4 (★, 확인·경미) — 음수 `Quantity`가 빈 풀 분기에서 무해한 no-op으로 통과.** 메인 경로는 `qty<1`
거절하지만 `poolRows.Count==0` 분기는 하한 검증이 없어 음수 요청이 조용히 성공(no-op)(`:459-468`).
아이템 영향 없음이나 잘못된 요청을 `ValidationError`로 못 잡음.

**계약 불일치 — `MoveStashItemRequest.Quantity` 의미.** 문서는 "원본 **스택**의 전체 수량"이라지만 구현은
FROM 컨테이너의 **같은 템플릿 전 셀 합(풀)**을 대상으로 함(`:406,439,473`). 다중 스택(예: 같은 탄약 60+10)
에서 `Quantity=null`이면 70 이동(스택 하나 60 아님). → 문서/구현 정렬(계약 텍스트 수정 권장).

**확인된 정상 동작:** 경계/겹침(AABB) 정확 / 병합·오버플로 정합성(풀에서 정확히 `actualMove`만 이동, 초과분
잔존) / 자기-셀 이동 no-op(손실 없음) / `qty>pool`·`qty<1` 메인 경로 거절 / 장착 인스턴스 이동 차단·
자동배치 제외.

**주의(설계 인지):** 그레인 사전검증과 리포 변이가 별도 커넥션/트랜잭션이라 **Orleans 단일 활성화에
안전성 의존**(TOCTOU 잠재). 현재 계약상 OK이나 reentrant 전환/리포 외부 호출 시 위험.

### A-4. 인프라 · 미들웨어 · 계약 (API 전역)

> 최종 취합 패스에서 라이브 검증(psql 불변식 + 실제 요청)으로 추가 확인한 항목. 이 배포는 **Redis
> 미구성** 상태였음(멱등성·SignalR 백플레인이 무저장 폴백으로 동작) — 다중 인스턴스/프로덕션과 다름.

**M2 (★★, 확인) — `Idempotency-Key`가 무시되어 중복 주문 생성.** Redis 미구성이라 `NullIdempotencyStore`가
바인딩(`Program.cs:76-79`)되고 `TryClaimAsync`가 항상 `Claimed` 반환(중복 미탐, `IdempotencyStore.cs:84-92`).
재현: 동일 `Idempotency-Key`로 `POST /api/orders` 2회 → 서로 다른 주문 2건 생성. 개발용 폴백으로 문서화돼
있으나, 앱은 헤더를 광고하면서 보호는 0. → 프로덕션은 Redis 필수(무저장 폴백일 땐 헤더를 거부하거나 경고).

**M3 (★★, 확인) — OpenAPI enum 계약 불일치(API 전역).** 4개 enum(`OrderSide`/`GridContainer`/
`StashEntryKind`/`EquipSlot`)이 `swagger.json`엔 `type: integer`로 선언되지만 런타임은 PascalCase 문자열
(`"Sell"`,`"Stash"`,`"Weapon"`,`"Active"` …)로 직렬화/반환. 입력은 int·문자열 둘 다 관대하게 받지만 응답은
항상 문자열 → **스펙 기반 생성 클라이언트가 모든 응답 역직렬화 실패**. Swashbuckle이
`JsonStringEnumConverter`를 반영하지 못함. → 스키마 필터로 enum을 string+`enum` 값 목록으로 노출.

**L8 (★, 확인) — 레이트리밋이 사실상 안 걸림.** `PermitLimit=1000`/플레이어/10초(`RateLimiting.cs:23-24`,
`appsettings.json:27`). 429 경로 자체는 정상(표준 봉투+Retry-After)이나 임계가 높아 실질 남용 방어로는
의미 약함. → 운영 임계 재설정.

**철회(오탐 공식 취소) — "extract 아이템 중복"은 버그 아님.** loot 스택이 `inventory_stack`과
`stash_placement` 양쪽에 기록되지만, 전자=소유 진실·후자=그리드 위치이며 `StashGrain.ReconcileAsync`가
다음 스태시 읽기에서 잉여 placement를 정리. 라이브 증명: invstack copy 판매로 invstack→0 후에도 raw
`stash_placement`엔 100 남았으나 `GET /api/inventory`=0, `GET /api/stash`가 그리드를 0으로 재조정. 모든
지출 경로(판매·출격)는 `inventory_stack`에서 차감 → 그리드 사본은 독립적으로 소비 불가. 소유 중복 없음,
정합성 온전.

**라이브 정합성 불변식(psql) 통과:** 음수 잔액/에스크로 없음, remaining=0인 OPEN 주문 없음, 인스턴스 중복
등재 없음, max_stack 초과 스택 없음, 한 인스턴스가 2곳에 없음, 발행 총량 == 지갑+에스크로+소각(정확).
전 과정(테스트 전·중·후) 유지.

---

## B. fun QA (플레이테스트 · 디자인)

방법: 라이브 앱을 직접 구동(출격→loot 폼으로 아이템 무료 생성→탈출) + 각 화면 코드 확인.

### 총평
- **강점(포트폴리오급):** "황무지 암시장 터미널" 무드가 확실히 산다(앰버/러스트, 모노 타입, `// MARKET`
  슬러그, 도그태그 스위처). **호가창 화면이 백미**(BID/Δ/ASK 스프레드·뎁스 바·수수료 칼럼·라이브 배지·
  주문 티켓) — 그 자체로 "진짜 경제" 어필. 지갑 원장(복식부기)·기록(탈출/사망·반입vs획득)·그리드+장비
  인형+주머니/리그/백팩(서버 검증, 149종 스프라이트)도 명확하고 테마 일관.
- **약점:** 앱 전체가 익스트랙션 테마인데 **레이드 루프에 긴장이 없고**, 그 경제가 **무한 팽창**. 장르
  약속이 UI 연출로만 존재하고 게임으로는 부재 — "인상적 백엔드 데모"와 "재미"의 격차.

### 이슈(재미 임팩트 순)
1. **레이드에 리스크 제로(핵심).** loot 폼에서 아무 아이템이나 999개 무료·즉시 추가 → 탈출하면 전부 보유.
   **사망은 DIE 버튼 수동 클릭에만 발생.** 타이머·랜덤 위협·생존 판정·손실이 없어 "지금 탈출 vs 더 파밍"의
   핵심 결정이 없음(밀어붙일 downside 부재). 폼 라벨도 "Loot **Sim**". → (a) 탈출 강제 카운트다운
   (단독으로도 루프가 살아남) (b) loot마다 사망 확률 상승 (c) 존별 서버 드롭테이블.
2. **경제가 무한 파괴 가능.** 무료 무한 전리품 → 레전더리(로켓발사기 base 15,000) 999개 생성·탈출·투매 →
   무한 캡. 소각/사망손실이 무료 소스를 못 이기고 사망은 발생도 안 함. 희소성이 없어 가격이 무의미.
   → #1로 리스크 게이트 후, 캡 싱크에 **목적** 부여(스태시 행 업그레이드[스키마 이미 플레이어별·확장대비],
   내구도/수리, 보험, 출격 수수료).
3. **마켓 랜딩이 시세가 아닌 고정 숫자.** 카드가 `base_value`(고정 상수)만 표시, 최근체결/최우선호가/
   스프레드/거래량·유동성 표시 없음(테두리는 희귀도). 매번 동일해 보이고 훌륭한 호가창이 한 클릭 아래 묻힘.
   → 카드에 최근체결 or 최우선 bid/ask+스프레드, 유동성 점, "활동순 정렬".
4. **첫 세션: 스프레드시트에 던져짐.** `/`가 149개 통조림으로 바로 리다이렉트. 캡·루프·첫 할 일 설명 없음.
   → 경량 첫 방문 오버레이 or 상단 한 줄 루프 설명.
5. **네비 언어 혼용이 미완성처럼 보임.** Market·Stash·장비·출격·기록·Wallet·Orders 혼재. 섹션 타이틀은
   이미 "STASH · 창고"로 잘함 → 네비에도 동일 이중어 패턴 적용 or 한 언어 통일.
6. **"Stash"가 두 의미.** 네비 Stash → 평면 목록(InventoryView), 그런데 장비 화면 패널이 "STASH · 창고
   12×60". 클릭한 Stash가 그 STASH로 안 감. → 네비명 변경(예: "Inventory · 보관") or 병합.

### 개선 아이디어 (임팩트 vs 노력)
| # | 아이디어 | 재미 상승 이유 | 노력 |
|---|---|---|---|
| 1 | **레이드 타이머**(시간 내 탈출 못하면 소실) | 탈출-vs-푸시를 진짜 긴장 결정으로 | 하~중 |
| 2 | **마켓 카드에 실시간 시세**(최근체결/최우선호가+유동성 점) | 죽은 첫 화면을 살아있는 시장으로 | 하~중 |
| 3 | **서버 드롭테이블(존/난이도별)** — 무료 드롭다운 대체 | 리스크/보상 티어 + 파밍 목표, 무한생성 차단 | 중~상 |
| 4 | **loot마다 사망 확률 상승(그리드 미터)** | "한 상자만 더?"의 도박 긴장 | 중 |
| 5 | **목적 있는 캡 싱크**(스태시 업글·내구도/수리·보험) | 캡에 목적 → 파밍·거래 이유, 경제 루프 폐합 | 중 |
| 6 | **첫 방문 루프 설명/온보딩 스트립** | 첫인상 개선 | 하 |
| 7 | **이중어 네비 + Stash/Gear 명칭 충돌 해소** | 저비용 폴리시, 미완성감 제거 | 하 |
| 8 | **희귀 전리품 추격 + 리더보드**(최다 캡/최고 탈출) | 롱테일 + 사회적 목표(경제에 판돈 생긴 뒤) | 중 |
| 9 | **출격 매니페스트에 총 at-risk 가치(캡) 표시** | "내가 뭘 거는가" 가독성, 사망 판돈 강조 | 하 |

**한 줄 결론:** 프레젠테이션과 거래 백엔드는 포트폴리오급이고 실제로 즐겁다. 레이드 루프는 지금
익스트랙션 슈터 옷을 입은 상태머신 데모 — 가장 레버리지 큰 투자처. #1·#2는 작고 임팩트 큰 변경으로
"게임처럼 보임 → 게임처럼 느껴짐"으로 전환하고, #3~5가 잘 만든 경제에 존재 이유를 준다.

---

## 부록 — QA 방법·범위

- **기능 QA:** 영역별 코드 정독(매칭·지갑 / 레이드 / 스태시·다중 스택) + 라이브 재현. 심각도·상태(확인/
  이론/의도됨) 표기. 정합성 불변식(fee 보존·이중판매·데드락 순서)은 코드 증명으로 소거.
- **fun QA:** 라이브 앱 직접 플레이(무료 loot로 경제 취약점 실증) + 화면 코드 확인. 주관적 설계·UX 신호.
- 이 문서의 findings는 **미수정 상태**로, 후속 작업에서 우선순위에 따라 처리한다.

---

## 수정 백로그 (체크리스트)

> 발견 항목을 실제 작업 단위로 정리. 위에서부터 대략 우선순위순. 고치면 `[x]`로 체크.
> (레이블은 A/B 섹션의 finding id와 대응.)

### 기능 — 정합성·계약·견고성
- [x] **M4** 사망 시 `item_ledger` 대칭화 — 사망 정산에서 `RaidLoss` insert 제거. 반입분은 출격
      `RaidBrought`(−)로 이미 손실이 회계돼 재차감=이중차감이었고, 전리품은 사전 크레딧이 없어
      `RaidLoss`(−)만 남으면 유령 음수였다. 물리 tombstone(유니크 `owner=NULL,origin=RAID_LOST`)은
      유지, 손실 감사는 `raid_session`(DIED)+`raid_session_item`이 보유. 불변식(세션 `ref_id` ledger
      합 == 소유량 순변화) 회귀 테스트 추가. (`MarketRepository.cs:1302~`, `RaidTests.cs`)
- [x] **M2** 멱등성 — 프로덕션에서 Redis 미구성 시 부팅 fail-fast. 무저장 폴백(`NullIdempotencyStore`,
      `IsDurable=false`)에서 `Idempotency-Key`가 오면 조용히 무시하지 않고 `503 IdempotencyUnavailable`로
      거부 + 경고 로그. 헤더 없는 일반 주문은 무영향. (`Program.cs`, `IdempotencyStore.cs`, `ApiResults.cs`,
      회귀 테스트 `HardeningTests.Idempotency_key_is_rejected_when_store_is_not_durable`)
- [x] **M3** OpenAPI enum — `StringEnumSchemaFilter`로 enum을 `type:string` + 이름 목록으로 노출
      (런타임 `JsonStringEnumConverter` 계약과 일치). Swashbuckle이 minimal API JSON 옵션을 자동
      반영하지 않아 정수로 표기하던 것을 교정. 회귀 테스트
      `OpenApiTests.Openapi_exposes_enums_as_string_with_values` 추가. (`SwaggerSetup.cs`)
- [x] **M1/BUG2** `GET /api/stash/container` → 500(NRE) 수정. 중첩 컨테이너는 인스턴스 id가 필수라 이
      라우트로 조회 불가 — `ParseContainer`가 `Container`를 400으로 거부(안내 메시지) + `GetStash`
      진입부 방어 가드로 이중화. 중첩 그리드는 `GET /api/equipment`의 `containers[]`로 노출됨.
      회귀 테스트 `StashTests.Get_stash_by_container_rejects_nested_container_with_400` 추가.
      (`StashEndpoints.cs`, `StashGrain.cs`)
- [x] **B(raid) StartedAt/ResolvedAt** — `LoadRaidDtoAsync`가 `started_at`/`resolved_at`을 DB에서 읽는다
      (파라미터 제거). AddLoot가 출격 시각 대신 loot 호출 시각을 반환하던 버그를 근본 제거하고,
      StartRaid/Resolve의 앱시각↔DB `now()` 불일치도 없앴다. 회귀 테스트
      `RaidTests.Raid_timestamps_come_from_db_and_startedat_is_stable_across_loot` 추가.
      (`MarketRepository.cs`)
- [x] **BUG D** loot 수량 상한 — `AddLootAsync`가 `max_stack` 기준으로 검증(초과 시 `ValidationError`).
      한 번의 픽업은 한 스택 상한을 넘을 수 없다(무한 픽업 차단). Extract 시점 인벤 분할 배치는 유지.
      회귀 테스트 `RaidTests.Loot_quantity_over_max_stack_is_rejected` 추가. (`MarketRepository.cs`)
- [x] **L2/BUG4** 음수·0 Quantity 검증 — `MoveStackAsync` 진입부에 하한(≥1) 공통 가드 추가. 빈 풀
      분기가 음수·0을 조용히 no-op 성공으로 흘려보내던 비일관 제거. 회귀 테스트
      `StashTests.Stack_move_with_non_positive_quantity_is_rejected` 추가. (`MarketRepository.cs`)
- [x] **L7** `fee_bps` `[0,10000]` 클램프 — `GetFeeBpsAsync`가 `Math.Clamp`. 설정 경로가 없어 읽는
      지점이 유일 방어(음수=돈 발행, 초과=체결액 초과 수수료 차단). 회귀 테스트
      `MarketFlowTests.Fee_bps_is_clamped_to_valid_range` 추가. (`MarketRepository.cs`)
- [x] **L8** 레이트리밋 임계 재설정 — 기본값 1000→600/10s(과관대한 100 req/s → 60 req/s). 정상 UI엔
      충분·봇 스팸 제약·부하 벤치(per-player 실측 ~22 req/s@c64) 헤드룸 유지. 프로덕션은 더 타이트하게,
      부하 측정은 크게 오버라이드하도록 문서화(perf-report 재현에 `RateLimiting__Orders__PermitLimit`
      추가). 설정 주입은 `RateLimitTests`(3/60s)가 이미 증명. (`RateLimiting.cs`, `appsettings.json`)
- [x] **L9(a)** 커밋-후-예외 이중환불 창 — 보상 전 `OrderExistsAsync`로 멱등 재조정. INSERT가 예외를
      던졌어도 실제 커밋됐으면(주문 살아있음) 보상 환불을 건너뛴다(취소 때 재환불 → 이중환불 방지).
      조회 실패 시엔 보상 시도(환불 누락 < 이중환불 위험). 회귀 테스트
      `HardeningTests.Order_exists_probe_reflects_persistence` 추가. (`OrderBookEngine.cs`, `MarketRepository.cs`)
- [ ] **BUG3**(선택) 중첩 컨테이너 그리드 직접 읽기 HTTP 라우트 노출. (`IStashGrain.GetContainer`)
- [ ] **L1**(향후) 다중셀 스택 footprint — grid>1×1 스택형을 추가할 때 `Ctx.Footprints` 참조로 전환.
- [ ] **문서 정렬** — `MoveStashItemRequest.Quantity`(풀 의미), `AddLootRequest.Kind`(무시됨),
      `GET /api/raid`(ACTIVE-only, 코드 유지·주석 수정) 계약 텍스트를 구현에 맞춤.
- [ ] **L9(b)/FINDING A**(선택) 가격밴드 ON 교차-밴드 매칭 — 문서화된 트레이드오프. 필요 시 크로스밴드
      코디네이션 도입.

### fun — 설계·UX (재미 임팩트순)
- [ ] **#1** 레이드 타이머(시간 내 미탈출 시 소실) — 탈출-vs-푸시 긴장. `[하~중]`
- [ ] **#2** 마켓 카드 실시간 시세(최근체결/최우선호가+유동성 점, 활동순 정렬). `[하~중]`
- [ ] **#3** 서버 드롭테이블(존/난이도별) — 무료 loot 폼 대체, 무한생성 차단. `[중~상]`
- [ ] **#4** loot마다 사망 확률 상승(그리드 미터). `[중]`
- [ ] **#5** 목적 있는 캡 싱크(스태시 행 업그레이드·내구도/수리·보험·출격 수수료). `[중]`
- [ ] **#6** 첫 방문 온보딩(루프 설명 스트립/오버레이). `[하]`
- [ ] **#7** 이중어 네비 통일 + Stash/Gear 명칭 충돌 해소. `[하]`
- [ ] **#9** 출격 매니페스트에 총 at-risk 가치(캡) 표시. `[하]`
- [ ] **#8** 희귀 전리품 추격 + 리더보드(경제에 판돈 생긴 뒤). `[중]`

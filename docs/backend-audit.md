# 백엔드 감사 보고서 (Backend Audit)

대상: `src/ItemMarket.Grains`(매칭 엔진·정산·리포지토리), `src/ItemMarket.Api`(인증·엔드포인트),
`db/ddl.sql`, `docs/api-contract.md` 대비 계약 충실도.
상태 표기: **[FIXED]** = 이번 감사에서 수정 완료(회귀 테스트 포함), **[DEFERRED]** = 후속 작업.

---

## Critical

### C1. `UnitPrice × Quantity` long 오버플로 → 음수 에스크로 → 병뚜껑 무한 생성 **[FIXED]**
- **무엇**: `OrderBookGrain.PlaceOrder`가 상한 없는 `req.UnitPrice * req.Quantity`(unchecked long 곱)를
  에스크로 금액으로 사용했다.
- **왜 문제**: 곱이 long을 넘으면 음수로 래핑된다. `TryEscrowCapsAsync`의 `balance < amount` 검사는
  음수 amount에 대해 항상 통과하고, `balance - (음수) = balance + |amount|` → **지갑 잔액이 증가**한다.
  인증만 있으면 누구나 호출 가능한 실질적 화폐 발행 취약점.
- **시나리오**: `POST /api/orders` body `{side:"Buy", unitPrice: 4611686018427387903, quantity: 4}` →
  에스크로 -4가 "성공"하며 잔액 +4. 반복 호출로 무한 증식.
- **수정**: `OrderBookGrain`에 상한 상수(`MaxUnitPrice=1e12`, `MaxQuantity=1e6`, `MaxNotional=1e15`)와
  Int128 총액 검증 추가. 수수료 계산도 `MarketRepository.CalcFee`(Int128)로 단일화(grain의 DTO용 fee와
  정산 tx의 fee가 항상 일치). 회귀 테스트:
  `AuditRegressionTests.Overflowing_price_times_quantity_is_rejected_and_wallet_untouched`.

### C2. 에스크로 커밋 후 주문 INSERT 실패 시 자산 영구 잠김 **[FIXED]**
- **무엇**: `PlaceOrder`의 흐름은 (1) 에스크로 tx 커밋 → (2) 주문 INSERT tx 커밋 → (3) 체결별 정산 tx.
  (1)과 (2) 사이에서 실패하면(커넥션 순단, 제약 위반 등) 병뚜껑/아이템은 잠겼는데 주문 행이 없다.
- **왜 문제**: 잠긴 자산을 되돌릴 유일한 경로는 주문 취소인데, 주문이 존재하지 않으므로 **복구 불가 자산 증발**.
  스택형 매도라면 인벤 수량, 유니크라면 인스턴스(owner=NULL 고아), 매수라면 병뚜껑이 사라진다.
- **수정**: INSERT 실패 시 보상(compensation) — `Refund`/`ReturnStack`/`ReturnInstance`로 원복 후 rethrow.
  보상마저 실패하면 `LogCritical`(원장의 ORDER_ESCROW 행 ref=orderId로 수동 복구 가능).
  (기존에 dead code였던 `IWalletGrain.Refund`/`ReturnStack`/`ReturnInstance`가 이 경로에서 사용된다.)

### C3. 정산 실패 시 인메모리 호가창-DB 영구 드리프트 **[FIXED]**
- **무엇**: `MatchAsync`가 `SettleFillAsync` 호출 **전에** `incoming.Remaining`/`maker.Remaining`을 선차감했다.
- **왜 문제**: 정산 tx가 실패(예: 낙관적 가드 충돌, DB 순단)하면 예외는 전파되지만 인메모리 잔량은 이미
  줄어든 상태로 남는다. 이후 이 grain의 모든 매칭은 DB 기대값(`remaining_quantity = @before`)과 어긋나
  낙관적 가드에 걸려 **해당 템플릿 시장 전체가 사실상 마비**된다(grain 재활성화 전까지).
- **수정**: 체결 후 잔량을 "계산만" 해서 정산 tx에 넘기고, **커밋 성공 후에만** 인메모리에 반영.
  정산 실패 시 `DeactivateOnIdle()` 후 rethrow → 다음 호출에서 DB(소스오브트루스)로부터 재수화.
  이미 커밋된 이전 fills는 유효하며 재수화에 자연 반영된다.

---

## High

### H1. 자전거래(self-trade) 허용 **[FIXED — 정책 결정: 금지(skip)]**
- **무엇**: 플레이어가 자신의 잔존 매도와 교차하는 매수를 내면 자기 자신과 체결됐다.
- **왜 문제**: 자산 복제는 없지만(수수료만 소각) **체결 내역·시세 조작(wash trading)** 이 가능하다.
  거래 내역이 UI의 시세 참고인 만큼 가짜 거래량/가격 신호를 만들 수 있다.
- **결정**: 매칭 시 본인 주문은 건너뛴다(skip). 거부(reject)가 아닌 skip을 택한 이유: 본인 주문 너머의
  다른 메이커와는 정상 체결되어야 하고, 잔존 주문 취소를 강제하지 않기 위함. 부작용으로 본인 매수/매도가
  교차한 채 호가창에 공존(crossed book)할 수 있으나 게임 마켓에서 무해하다.
- **수정**: `MatchAsync` 후보 루프에서 `maker.PlayerId == incoming.PlayerId → continue`.
  회귀 테스트: `Self_trade_is_skipped_not_matched` (다른 플레이어와는 체결되는 것까지 검증).
  `docs/api-contract.md` 매칭 규칙에 명시.

### H2. 예상 밖 예외가 ApiResponse 봉투 없이 원시 500으로 유출 **[FIXED]**
- **무엇**: `Exec`가 `DomainException`만 잡았다. `NpgsqlException`, Orleans 예외 등은 ASP.NET 기본 500.
- **왜 문제**: 프론트는 모든 응답을 `ApiResponse<T>`로 파싱한다(계약 문서 첫 줄). 봉투 없는 500은
  프론트 파싱 실패 + 내부 예외 정보 노출 가능성.
- **수정**: `Exec`에 catch-all 추가 — 로그 후 `ErrorCode.Unknown` 봉투로 500 반환.

### H3. 어드민 지급/조정 입력 미검증 → DB 제약 위반 500·오염 데이터 **[FIXED]**
- **무엇**: `grant/stack` 음수·0 수량(CHECK 위반 or 재고 차감 악용), 유니크 템플릿에 stack 지급,
  스택형 템플릿에 instance 지급(판매 불가 유령 인스턴스 생성), 없는 플레이어 지급(FK 위반),
  `wallet/adjust`의 `balance + delta` checked 미적용(오버플로).
- **왜 문제**: 어드민 전용이라 악용 반경은 좁지만, 실수 한 번으로 원시 500(H2와 결합 시 봉투 깨짐) 또는
  카탈로그 불변식(stackable ↔ 인스턴스 존재)이 깨진 데이터가 영구히 남는다.
- **수정**: `MarketRepository.ValidateGrantTargetAsync`(플레이어 존재 → `PlayerNotFound`,
  템플릿 존재 → `TemplateNotFound`, stackable 일치 → `StackableMismatch`), 수량 1..1,000,000,
  durability ≥ 0, AdminAdjust `checked` 가드. 회귀 테스트:
  `Admin_grant_validation_returns_domain_errors_not_500`.

### H4. `CancelOrder`가 주문의 템플릿 소속을 검증하지 않음 **[FIXED]**
- **무엇**: grain이 `order.TemplateId == TemplateId`를 확인하지 않았다. API는 올바르게 라우팅하지만,
  다른 호출 경로(향후 코드, 직접 grain 호출)가 잘못된 grain으로 취소를 보내면 DB에서는 취소되는데
  **실제 소유 grain의 인메모리 호가창에는 주문이 남는다** → 취소된 주문과 매칭 시도 → 낙관적 가드
  충돌 연쇄(C3와 같은 마비 양상).
- **수정**: 템플릿 불일치 시 `OrderNotFound` 거부(방어적 가드, 1줄).

---

## Medium

### M1. `DomainException.Code`가 실로 경계(원격) 직렬화에서 보존된다는 보장 없음 **[FIXED]**
- **무엇**: co-host 단일 실로(현 개발/테스트 구성)에서는 grain 예외가 in-proc으로 전파되어 `Code`가
  보존됐지만, `run-cluster.sh`의 2-실로(adonet) 구성에서 grain이 **다른 실로**에 활성화되면 Orleans 예외
  직렬화가 커스텀 프로퍼티 `Code`를 보존하지 못해 `ErrorCode.Unknown`/500으로 강등될 수 있었다.
- **원인**: `DomainException`이 Orleans 코덱 없이 JSON 폴백(`ItemMarket.*`) 대상이었는데, JSON은 무인자
  생성자·set 접근자가 없는 `Exception`을 역직렬화할 수 없어 실로 경계에서 재구성에 실패.
- **수정**: `DomainException`에 `[GenerateSerializer]` + `[Id(0)] Code` 부여(`DomainException.cs`), JSON
  직렬화기 대상에서 `Exception` 파생 타입 제외(`OrleansHosting.cs`)해 생성 코덱이 `Code`를 실로 경계 너머로
  보존하게 함. 회귀 테스트 `CrashRecoveryTests.DomainException_roundtrips_through_orleans_serializer_preserving_code`
  가 Orleans `Serializer`로 직접 라운드트립해 `Code`·`Message` 보존을 고정(멀티실로 없이 직렬화 계약 검증).

### M2. 어드민 `status` 필터가 숫자 문자열을 통과시킴 **[FIXED]**
- `Enum.TryParse("7")`은 정의되지 않은 값으로도 true → `ToDb` 기본값 "OPEN"으로 오필터링.
  `Enum.IsDefined` 가드 추가(1줄, Program.cs).

### M3. `TradePayment` 원장 사유가 실제로는 미사용 **[DEFERRED — 문서화]**
- 매수자 지출은 주문 시점 `ORDER_ESCROW`(상한가 전액 -)와 체결 시점 차익 `ORDER_REFUND`(+)의 조합으로만
  기록되고, 체결별 `TRADE_PAYMENT` 행은 쓰이지 않는다. **원장 합계는 잔액과 정합**하므로 버그는 아니지만,
  "체결 1건 = 매수자 원장 1행"을 기대하는 감사 도구는 헷갈릴 수 있다. 회계 모델을 계약 주석대로
  유지(에스크로 정산 방식)하고 여기 문서화한다. 원하면 정산 tx에서 escrow 해제/지불을 쌍으로 기록하도록 개선 가능.

### M4. 이중 활성화(double activation) 엣지의 에스크로 비멱등 **[DEFERRED — 부분 완화됨]**
- 정산은 `remaining_quantity = @before` 낙관적 가드로 보호되지만, 네트워크 분단 등으로 같은 템플릿
  grain이 순간적으로 2개 활성화되면 에스크로(잔액 차감)는 양쪽에서 각각 성공할 수 있다(멱등 키 없음).
  단일 노드 개발 구성에서는 발생 불가. 제안: `wallet_ledger`에 `(reason, ref_id)` 부분 유니크 제약으로
  주문당 에스크로 1회 강제.

### M5. `_feeBps`·`_stackable`이 활성화 시점 캐시 **[DEFERRED]**
- `market_config.fee_bps` 변경이 살아있는 grain에 반영되지 않는다(비활성화 전까지).
  운영에서 수수료 변경 시 재배포/grain 재활성화 필요. 제안: 정산 시점 재조회 또는 TTL 캐시.

### M6. 재수화 시 가격-시간 우선의 타이브레이크가 타임스탬프뿐 **[DEFERRED]**
- 같은 `created_at`(밀리초 단위 충돌)인 주문들의 상대 순서가 재활성화 후 비결정적일 수 있다.
  제안: `market_order`에 시퀀스 컬럼(bigserial) 추가 후 `(price, seq)` 정렬.

### M7. 레이트리밋/요청 크기 제한 부재 **[DEFERRED — future work]**
- `/api/auth/login`(비밀번호 없는 개발 스코프)과 주문 API에 레이트리밋이 없다. 스팸 주문으로 호가창
  메모리/DB를 부풀릴 수 있다(주문당 자산 잠금이 자연 억제책이긴 함). 제안: ASP.NET `AddRateLimiter`
  (IP/플레이어 기준), 프록시 뒤 배포 시 요청 바디 크기 제한.

---

## Low

### L1. 개발용 JWT 시크릿/무자격 로그인 커밋됨 **[문서화됨 — 의도된 개발 스코프]**
- `appsettings.json`의 `Auth:Secret`은 개발용. 운영 배포 시 환경변수/시크릿 매니저로 교체하고
  로그인에 자격증명 검증을 추가해야 한다(계약 문서에 개발 스코프로 명시되어 있음).

### L2. SQL 중복 **[DEFERRED]**
- `market_order` 컬럼 목록 SELECT가 리포지토리에 5회 반복. 상수/뷰로 정리 여지. 동작 문제 없음.

### L3. `Enums.To*`가 알 수 없는 DB 문자열을 기본값으로 삼킴 **[DEFERRED]**
- 예: 알 수 없는 status → `Open`. 데이터 오염을 조기에 드러내려면 throw가 낫다.

### L4. `wallet_ledger` 조회 정렬(`id DESC`)과 인덱스(`created_at DESC`) 불일치 **[DEFERRED]**
- 행이 커지면 정렬 비용 발생. `(player_id, id DESC)` 인덱스 또는 정렬 컬럼 통일 권장.
  현재 데이터 규모에서는 무해.

### L5. `GET /api/orders`(내 주문) 무페이징 **[DEFERRED — 계약대로]**
- 계약이 `OrderDto[]`(배열)라 유지. 주문 이력이 쌓이면 페이징 도입 필요(계약 변경 수반).

### L6. 존재하지 않는 템플릿의 `/book` 조회가 빈 호가창 200 반환 **[의도 유지]**
- 404가 더 엄밀하나 프론트가 빈 스냅샷을 무해하게 렌더링하므로 유지.

---

## 계약 충실도(요약)

`docs/api-contract.md`의 전 엔드포인트가 존재하고 응답 봉투/열거형 문자열 직렬화/PagedResult(1-based,
`TotalCount`)가 일치함을 확인. 오류 코드 → HTTP 상태 매핑(401/403/404/400)도 계약과 부합.
수정 사항 중 계약 표면에 닿는 것은 없음(자전거래 금지 규칙을 매칭 규칙 문단에 추가 문서화).

## 정산 원자성(요약)

- 체결 1건 = `SettleFillAsync` 단일 tx (trade 기록 + 판매대금 + 수수료 소각 + 차익 환불 + 아이템 이전 +
  양쪽 주문 갱신). 모든 지갑 변동은 같은 tx에서 `wallet_ledger`에 delta/balance_after와 함께 기록됨. ✅
- 주문 갱신에 낙관적 가드(`remaining_quantity = @before AND status IN (...)`) 존재 — DB가 최종 심판. ✅
- "DB 커밋 성공 + 인메모리 실패" 방향은 이제 불가능(커밋 후에만 인메모리 반영), "인메모리만 변경 + DB 실패"
  방향은 비활성화→재수화로 복구(C3 수정). ✅
- 에스크로/주문 INSERT는 별도 tx이나 실패 시 보상으로 원복(C2 수정). 보상 실패 잔여 위험은 Critical 로그 +
  원장 추적으로 수동 복구 가능. ⚠️ (완전한 해결은 에스크로+INSERT 단일 tx화 — 후속 과제)

---

## 이후(부하테스트·기능 확장 중) 추가로 발견·수정

초기 감사 이후 부하 테스트와 기능 확장(그리드 인벤토리·타르코프식 장비 슬롯·익스트랙션 레이드)을 진행하며 잡은 결함들.
각 항목 **문제 → 수정** 한 줄 요약(상세 근거·수치는 `docs/perf-report.md`, 계약은 `docs/api-contract.md`).

- **교차-grain 지갑 락 순서 데드락(40P01)** — 서로 다른 아이템 grain의 동시 정산이 같은 지갑 행을 다른
  순서로 잠가 Postgres 데드락 → 정산 tx 시작에 지갑 행을 player_id 오름차순으로 일관 선점(락 순서화).
  p99 973→175ms, 데드락 0. (이후 락 획득이 플랜에 의존하지 않도록 C# 정렬 + 개별 `FOR UPDATE` 문장으로 하드닝.)
- **그리드 유니크 제약 버그** — 스택용 `(player, template)` 유니크 제약이 인스턴스 행에도 적용돼 같은
  무기 2정 배치 시 `duplicate key` → **STACK 전용 부분 유니크 인덱스**(`WHERE kind='STACK'`, 컨테이너
  포함)로 교정.
- **raid 정산 롤백 중첩 버그** — StartRaid/Extract/Die 트랜잭션의 예외 처리에서 롤백이 중첩 호출되며
  이미 롤백된 tx를 다시 되돌리려 함 → 단일 롤백 경로로 정리(`catch (PostgresException UniqueViolation)`은
  RaidActive 도메인 에러로 매핑, 그 외는 롤백 후 rethrow).
- **`GET /api/raid` 계약 정정** — "ACTIVE 우선, 없으면 최근 세션 반환"이 결과 화면 재조회 시 해결된
  세션을 되돌려 UI가 혼란 → **ACTIVE 세션만 반환, 없으면 `null`**로 계약 축소(결과는 extract/die 응답으로
  표시). `docs/api-contract.md` 반영.

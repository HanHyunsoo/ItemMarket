-- ============================================================================
--  아이템 거래소 (Item Market) - 생존자 암시장(Black Market) 스키마
--  아포칼립스 익스트랙션 슈터 테마 / 특정 게임과 무관한 샘플 프로젝트
--  Postgres 16 / docker-entrypoint-initdb.d 로 최초 1회 자동 실행
--
--  설계 원칙
--   - 금액(병뚜껑 CAP)은 전부 BIGINT 정수. 부동소수점 미사용.
--   - 수수료는 basis point(bps) 정수. 5% = 500bps.
--   - 하이브리드 아이템: FOOD/MEDICAL/AMMO = 스택형(inventory_stack),
--                        MELEE/GUN        = 유니크 인스턴스(item_instance).
--   - 매도/매수 주문은 에스크로(자산 잠금)로 dupe/이중판매를 원천 차단.
--   - wallet_ledger는 append-only. 모든 병뚜껑 이동을 추적(감사/RMT 탐지).
-- ============================================================================

BEGIN;

-- ----------------------------------------------------------------------------
-- 플레이어 / 지갑
-- ----------------------------------------------------------------------------
CREATE TABLE player (
    id           UUID PRIMARY KEY,
    display_name TEXT NOT NULL,
    -- 스태시 크기: 가로는 12로 고정(코드 상수), 세로(행 수)만 플레이어별 가변 → 향후 업그레이드 대비.
    stash_rows   INT NOT NULL DEFAULT 60 CHECK (stash_rows BETWEEN 1 AND 500),
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE wallet (
    player_id UUID PRIMARY KEY REFERENCES player(id),
    balance   BIGINT NOT NULL DEFAULT 0 CHECK (balance >= 0)
);

CREATE TABLE wallet_ledger (
    id            BIGSERIAL PRIMARY KEY,
    player_id     UUID NOT NULL REFERENCES player(id),
    delta         BIGINT NOT NULL,
    balance_after BIGINT NOT NULL,
    reason        TEXT   NOT NULL,   -- ORDER_ESCROW/ORDER_REFUND/TRADE_PAYMENT/TRADE_PROCEEDS/FEE/ADMIN_ADJUST
    ref_id        UUID,              -- 관련 주문/체결 id
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_wallet_ledger_player ON wallet_ledger(player_id, created_at DESC);

-- ----------------------------------------------------------------------------
-- 아이템 마스터(카탈로그) - 시드로 고정
-- ----------------------------------------------------------------------------
CREATE TABLE item_template (
    id             INT     PRIMARY KEY,
    code           TEXT    NOT NULL UNIQUE,
    name           TEXT    NOT NULL,
    category       TEXT    NOT NULL,   -- FOOD/MEDICAL/MELEE/GUN/AMMO
    rarity         TEXT    NOT NULL,   -- COMMON/UNCOMMON/RARE/EPIC/LEGENDARY
    stackable      BOOLEAN NOT NULL,
    max_durability INT,                -- 유니크(무기)만. 스택형은 NULL
    icon           TEXT    NOT NULL,   -- 픽셀 스프라이트 키 (assets/items/<icon>.svg)
    base_value     BIGINT  NOT NULL,   -- 참고 시세(병뚜껑). 시드/어드민 가이드용
    grid_w         INT     NOT NULL DEFAULT 1 CHECK (grid_w BETWEEN 1 AND 6),  -- 스태시 footprint 폭(칸)
    grid_h         INT     NOT NULL DEFAULT 1 CHECK (grid_h BETWEEN 1 AND 6),  -- 스태시 footprint 높이(칸)
    -- max_stack: 한 칸(스택)에 쌓을 수 있는 최대 수량. 아래 시드에서 카테고리별로 채운다
    --   (AMMO 60 / FOOD 10 / MEDICAL 5, 유니크·기본 1). 초과분은 새 스택으로 분리된다.
    max_stack      INT     NOT NULL DEFAULT 1 CHECK (max_stack >= 1),
    -- ---- 장비(equipment) / 중첩 컨테이너(nested-grid) 확장 ----------------------
    -- equip_slot: 장착 슬롯(HELMET/ARMOR/WEAPON/BACKPACK/RIG). NULL이면 장착 불가.
    --   슬롯별로 정확히 한 인스턴스만 장착(player_equipment). GUN 카테고리는 WEAPON 슬롯.
    equip_slot     TEXT,
    -- is_container: 내부 그리드를 제공하는 아이템(백팩/리그). true면 container_w×container_h가
    --   그 인스턴스의 중첩 그리드 크기. 스택/유니크를 그 안에 배치할 수 있다(stash_placement.container_instance_id).
    is_container   BOOLEAN NOT NULL DEFAULT false,
    container_w    INT,
    container_h    INT,
    CHECK (stackable = (max_durability IS NULL)),
    CHECK (is_container = (container_w IS NOT NULL AND container_h IS NOT NULL))
);

-- ----------------------------------------------------------------------------
-- 인벤토리
--   스택형: (player, template) 당 수량 한 줄
--   유니크: 인스턴스 한 줄. owner_player_id = NULL 이면 매도 주문 에스크로 상태
-- ----------------------------------------------------------------------------
CREATE TABLE inventory_stack (
    player_id   UUID NOT NULL REFERENCES player(id),
    template_id INT  NOT NULL REFERENCES item_template(id),
    quantity    INT  NOT NULL CHECK (quantity >= 0),
    PRIMARY KEY (player_id, template_id)
);

CREATE TABLE item_instance (
    id              UUID PRIMARY KEY,
    template_id     INT  NOT NULL REFERENCES item_template(id),
    owner_player_id UUID REFERENCES player(id),   -- NULL = 에스크로/이동중/레이드 소실(tombstone)
    durability      INT,
    attachments     JSONB NOT NULL DEFAULT '[]'::jsonb,
    origin          TEXT NOT NULL DEFAULT 'SEED',  -- 프로버넌스: SEED/ADMIN_GRANT/RAID/RAID_LOST 등
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_item_instance_owner ON item_instance(owner_player_id);

-- ----------------------------------------------------------------------------
-- 스태시 배치(그리드 인벤토리)
--   플레이어별 컨테이너(STASH 가로12·세로=player.stash_rows / POCKETS 4×1 / 백팩·리그 중첩) 위 배치.
--   (x,y)=좌상단 칸, footprint는 템플릿의 grid_w×grid_h.
--   스택형은 한 칸(1×1) + 그 칸에 담긴 quantity(≤max_stack). 같은 템플릿이 여러 칸에 나뉠 수 있다(다중 스택),
--   유니크는 인스턴스별로 배치(정확히 한 컨테이너의 한 칸).
--   컨테이너는 조직화용일 뿐 — 소유 수량의 소스오브트루스는 inventory_stack/item_instance다.
--   서버 권위 검증: 경계 밖·겹침 금지(애플리케이션 계층에서 판정).
-- ----------------------------------------------------------------------------
CREATE TABLE stash_placement (
    player_id   UUID NOT NULL REFERENCES player(id),
    container   TEXT NOT NULL DEFAULT 'STASH',         -- STASH / POCKETS / CONTAINER(백팩·리그 내부 그리드)
    kind        TEXT NOT NULL,                         -- STACK / INSTANCE
    template_id INT  NOT NULL REFERENCES item_template(id),
    instance_id UUID REFERENCES item_instance(id),     -- INSTANCE일 때만
    -- container='CONTAINER'일 때, 이 배치가 놓인 중첩 그리드를 제공하는 컨테이너 인스턴스(장착된 백팩/리그).
    -- STASH/POCKETS 배치는 NULL. 같은 스택 템플릿을 여러 물리 컨테이너·여러 칸에 나눠 담을 수 있다(다중 스택).
    container_instance_id UUID REFERENCES item_instance(id),
    x           INT  NOT NULL CHECK (x >= 0),
    y           INT  NOT NULL CHECK (y >= 0),
    quantity    INT  NOT NULL DEFAULT 1 CHECK (quantity >= 1),  -- 이 컨테이너에 담긴 스택 수량(유니크는 1)
    -- 유니크 아이템은 인스턴스 단위로 유일(같은 템플릿 무기를 여러 자루 소유 가능).
    -- 인스턴스는 정확히 한 컨테이너+한 칸에만 존재하므로 container를 포함하지 않는 전역 유일.
    CONSTRAINT uq_stash_instance UNIQUE (instance_id),
    CHECK ((kind = 'INSTANCE') = (instance_id IS NOT NULL)),
    -- 중첩 컨테이너 배치만 container_instance_id를 갖는다(STASH/POCKETS은 NULL).
    CHECK ((container = 'CONTAINER') = (container_instance_id IS NOT NULL))
);
CREATE INDEX idx_stash_player ON stash_placement(player_id);
-- 셀 유일성: 한 물리 컨테이너의 한 칸(좌상단 (x,y))에는 배치가 최대 1개. 스택·인스턴스 모두 적용.
-- 물리 컨테이너 = (container, container_instance_id). NULL은 센티넬 UUID로 접어 STASH/POCKETS도 구분되게 한다.
-- 다중 스택 지원: 같은 스택 템플릿을 여러 칸·여러 컨테이너에 나눠 놓을 수 있다(각 스택은 앱에서 max_stack 상한).
-- footprint(1×1 초과) 겹침은 그리드 기하 엔진이 앱 레벨에서 강제(DB는 좌상단 칸 충돌만 방지).
CREATE UNIQUE INDEX uq_stash_cell ON stash_placement(
    player_id, container,
    COALESCE(container_instance_id, '00000000-0000-0000-0000-000000000000'::uuid),
    x, y
);

-- ----------------------------------------------------------------------------
-- 플레이어 장비(equipment) — 슬롯 → 인스턴스(단일 아이템) 매핑
--   슬롯(HELMET/ARMOR/WEAPON/BACKPACK/RIG)마다 정확히 한 인스턴스만 장착.
--   인스턴스는 template.equip_slot과 일치하는 슬롯에만 장착 가능(서버 권위 검증).
--   장착된 인스턴스는 소유(owner_player_id=player) 상태이나 스태시 그리드에는 배치되지 않는다
--   (인형(doll) 위에 있음). 장착된 백팩/리그는 내부 그리드(중첩 컨테이너)를 제공한다.
-- ----------------------------------------------------------------------------
CREATE TABLE player_equipment (
    player_id   UUID NOT NULL REFERENCES player(id),
    slot        TEXT NOT NULL,   -- HELMET / ARMOR / WEAPON / BACKPACK / RIG
    instance_id UUID NOT NULL REFERENCES item_instance(id),
    PRIMARY KEY (player_id, slot),
    -- 한 인스턴스는 최대 한 슬롯에만 장착.
    CONSTRAINT uq_player_equipment_instance UNIQUE (instance_id)
);
CREATE INDEX idx_player_equipment_player ON player_equipment(player_id);

-- ----------------------------------------------------------------------------
-- 주문서(order book) & 체결(trade)
-- ----------------------------------------------------------------------------
CREATE TABLE market_order (
    id                 UUID PRIMARY KEY,
    player_id          UUID NOT NULL REFERENCES player(id),
    side               TEXT NOT NULL,   -- BUY / SELL
    template_id        INT  NOT NULL REFERENCES item_template(id),
    unit_price         BIGINT NOT NULL CHECK (unit_price > 0),
    quantity           INT NOT NULL CHECK (quantity > 0),
    remaining_quantity INT NOT NULL CHECK (remaining_quantity >= 0),
    instance_id        UUID REFERENCES item_instance(id),  -- 유니크 매도 시(수량 1)
    status             TEXT NOT NULL,   -- OPEN/PARTIALLY_FILLED/FILLED/CANCELLED
    escrow_caps        BIGINT NOT NULL DEFAULT 0,  -- 매수 주문 잔여 물량에 잠긴 병뚜껑
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);
-- 호가창 조회 및 매칭 스캔용. (template, side, status) 안에서 price 정렬.
CREATE INDEX idx_order_book ON market_order(template_id, side, status, unit_price);
CREATE INDEX idx_order_player ON market_order(player_id, status);

CREATE TABLE trade (
    id            UUID PRIMARY KEY,
    template_id   INT  NOT NULL REFERENCES item_template(id),
    buy_order_id  UUID NOT NULL REFERENCES market_order(id),
    sell_order_id UUID NOT NULL REFERENCES market_order(id),
    buyer_id      UUID NOT NULL REFERENCES player(id),
    seller_id     UUID NOT NULL REFERENCES player(id),
    unit_price    BIGINT NOT NULL,
    quantity      INT NOT NULL,
    instance_id   UUID REFERENCES item_instance(id),
    fee_amount    BIGINT NOT NULL DEFAULT 0,   -- 판매자 대금에서 차감·소각
    executed_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_trade_template_time ON trade(template_id, executed_at DESC);
CREATE INDEX idx_trade_buyer  ON trade(buyer_id, executed_at DESC);
CREATE INDEX idx_trade_seller ON trade(seller_id, executed_at DESC);

-- ----------------------------------------------------------------------------
-- 거래소 설정 (수수료율 등)
-- ----------------------------------------------------------------------------
CREATE TABLE market_config (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
INSERT INTO market_config(key, value) VALUES ('fee_bps', '500');  -- 5.00%

-- ----------------------------------------------------------------------------
-- 멱등성(POST /api/orders 재시도/중복 제출 방어)은 Redis로 이관되었다.
--   (player, Idempotency-Key) 슬롯을 Redis 키 idem:{playerId}:{key} 로 SET NX EX 청구하고,
--   원본 완료 시 직렬화된 ApiResponse<PlaceOrderResult> JSON으로 덮어쓴다.
--   Redis:ConnectionString 이 비어있으면(단일 인스턴스 개발) 멱등 헤더는 무시된다.
-- ----------------------------------------------------------------------------

-- ----------------------------------------------------------------------------
-- 리프레시 토큰 (JWT 갱신 + 로테이션)
--   짧은 액세스 토큰(기본 15분)과 함께 발급되는 긴 리프레시 토큰(기본 14일)을 저장한다.
--   보안: 원문(raw)이 아니라 SHA-256 해시만 저장한다. DB가 유출돼도 토큰 자체는 복원 불가.
--   로테이션: /api/auth/refresh 는 제시된 토큰을 revoked=true 로 폐기하고 새 쌍을 발급한다.
--   재사용 탐지: 이미 revoked 된 토큰이 다시 제시되면(탈취 정황) 해당 플레이어의 전체
--   토큰 체인을 폐기하고 401을 반환한다(애플리케이션 계층에서 판정).
-- ----------------------------------------------------------------------------
CREATE TABLE refresh_token (
    id         UUID        PRIMARY KEY,
    player_id  UUID        NOT NULL REFERENCES player(id),
    token_hash TEXT        NOT NULL UNIQUE,       -- SHA-256(raw token) hex. 원문 미저장.
    expires_at TIMESTAMPTZ NOT NULL,
    revoked    BOOLEAN     NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_refresh_token_player ON refresh_token(player_id);

-- ----------------------------------------------------------------------------
-- 익스트랙션 레이드 세션 (extraction/raid)
--   생존형(extraction) 게임의 시그니처 루프를 서비스 계층 상태기계 + 원자적 정산으로 모델링.
--   반입 대상(at-risk) = 스태시 밖 전부 = 착용 장비(헬멧/방어구/무기/백팩/리그) + 백팩·리그 내용물 + 주머니.
--     스태시(안전)는 절대 at-risk가 아니다. 착용 장비만 있어도(주머니 비어도) 출격 가능.
--   루프: 장비를 착용/주머니를 채운다 → StartRaid(스태시 밖 전부를 "위험(at-risk)"으로 잠금) →
--         Extract(생존 → 출격 시점 배치 그대로 제자리 복원 + 전리품 귀속) | Die(위험 아이템 전량 소실).
--   설계: 매도 에스크로와 동일한 "자산 잠금" 이동을 재사용한다.
--     - StartRaid: 반입 스택은 inventory_stack에서 차감, 유니크는 owner_player_id=NULL, 장착은 슬롯 해제.
--       (판매/이동 불가 상태가 되어 레이드 중 이중사용/dupe 원천 차단 — 기존 에스크로 검사가 거부).
--     - 위험 스냅샷은 raid_session_item(레이드 에스크로)에 원위치와 함께 보관.
--     - 플레이어당 ACTIVE 세션은 최대 1개(부분 유니크 인덱스로 강제).
-- ----------------------------------------------------------------------------
CREATE TABLE raid_session (
    id          UUID PRIMARY KEY,
    player_id   UUID NOT NULL REFERENCES player(id),
    status      TEXT NOT NULL,   -- ACTIVE / EXTRACTED / DIED
    started_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    resolved_at TIMESTAMPTZ      -- EXTRACTED/DIED 확정 시각(ACTIVE면 NULL)
);
-- 플레이어당 ACTIVE 세션 1개 강제(부분 유니크 인덱스). 두 번째 StartRaid는 여기서 충돌.
CREATE UNIQUE INDEX uq_raid_active ON raid_session(player_id) WHERE status = 'ACTIVE';
CREATE INDEX idx_raid_session_player ON raid_session(player_id, started_at DESC);

-- 위험(at-risk) 아이템 스냅샷 = "레이드 에스크로". BROUGHT(반입) + LOOTED(레이드 중 획득).
--   유니크 LOOTED는 Extract 시점에야 item_instance로 materialize되므로 instance_id는
--   item_instance를 FK 참조하지 않는다(반입 유니크는 이미 존재하는 인스턴스의 id를 담음).
CREATE TABLE raid_session_item (
    id          BIGSERIAL PRIMARY KEY,
    session_id  UUID NOT NULL REFERENCES raid_session(id),
    kind        TEXT NOT NULL,   -- STACK / INSTANCE
    template_id INT  NOT NULL REFERENCES item_template(id),
    instance_id UUID,            -- INSTANCE일 때만(반입=기존 id, 획득=예약 id → Extract 시 materialize)
    quantity    INT  NOT NULL DEFAULT 1 CHECK (quantity >= 1),  -- 유니크는 1
    source      TEXT NOT NULL,   -- BROUGHT / LOOTED
    -- ---- 원위치 복원 스냅샷(익스트랙션 시맨틱) --------------------------------
    -- StartRaid 시점에 반입(BROUGHT) 아이템이 있던 정확한 위치를 스냅샷한다. Extract(생존) 시
    -- 이 위치로 그대로 복원한다(스태시 자동 덤프가 아니라 장착 슬롯/백팩·리그 내부/주머니 원위치로).
    -- LOOTED(레이드 중 획득) 아이템은 원위치가 없어 전부 NULL이며, Extract 시 반입 공간
    -- (장착된 백팩·리그 중첩 그리드 → 주머니 → STASH 오버플로 순)에 first-fit으로 배치된다.
    origin_container           TEXT,   -- POCKETS / CONTAINER / EQUIP (BROUGHT만; LOOTED은 NULL). STASH는 at-risk가 아니라 등장 안 함
    origin_container_instance_id UUID, -- origin_container='CONTAINER'(중첩 백팩·리그 내부)일 때 그 컨테이너 인스턴스
    origin_slot                TEXT,   -- origin_container='EQUIP'일 때 장착 슬롯(HELMET/ARMOR/WEAPON/BACKPACK/RIG)
    origin_x                   INT,    -- 그리드 원위치(EQUIP은 NULL)
    origin_y                   INT,
    CHECK ((kind = 'INSTANCE') = (instance_id IS NOT NULL))
);
CREATE INDEX idx_raid_item_session ON raid_session_item(session_id);

-- 아이템 원장(append-only). wallet_ledger의 프로버넌스 아이디어를 아이템 이동에 적용.
--   레이드 흐름(RAID_BROUGHT/RAID_EXTRACT/RAID_LOOT/RAID_LOSS)을 최소 기록한다(감사/dupe 탐지).
--   delta_qty는 소유 인벤토리 기준 부호(반입/소실=-, 회수/획득=+). 잔고 컬럼은 없다(잔고가 아닌
--   이동 로그). ref_id는 관련 raid_session id.
CREATE TABLE item_ledger (
    id          BIGSERIAL PRIMARY KEY,
    player_id   UUID NOT NULL REFERENCES player(id),
    kind        TEXT NOT NULL,   -- STACK / INSTANCE
    template_id INT  NOT NULL REFERENCES item_template(id),
    instance_id UUID,
    delta_qty   INT  NOT NULL,
    reason      TEXT NOT NULL,   -- RAID_BROUGHT/RAID_EXTRACT/RAID_LOOT/RAID_LOSS/ADMIN_GRANT/TRADE...
    ref_id      UUID,            -- 관련 raid_session id 등
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_item_ledger_player ON item_ledger(player_id, created_at DESC);

-- ============================================================================
--  시드: 아이템 마스터 102종
--  category: FOOD/MEDICAL/MELEE/GUN/AMMO
--  icon    : food_can/food_water/food_snack / med_bandage/med_pills/med_kit
--            / melee_knife/melee_bat/melee_axe / gun_pistol/gun_shotgun/gun_rifle
--            / ammo_box/ammo_shell
-- ============================================================================
INSERT INTO item_template
    (id, code, name, category, rarity, stackable, max_durability, icon, base_value) VALUES
-- 먹을거 (FOOD) --------------------------------------------------------------
( 1,'canned_beans',   '통조림 콩',        'FOOD','COMMON',  true, NULL,'food_can',    8),
( 2,'canned_spam',    '스팸 통조림',      'FOOD','UNCOMMON',true, NULL,'food_can',   18),
( 3,'canned_tuna',    '참치 통조림',      'FOOD','UNCOMMON',true, NULL,'food_can',   15),
( 4,'canned_corn',    '옥수수 통조림',    'FOOD','COMMON',  true, NULL,'food_can',    9),
( 5,'canned_peaches', '복숭아 통조림',    'FOOD','COMMON',  true, NULL,'food_can',   10),
( 6,'canned_soup',    '수프 통조림',      'FOOD','COMMON',  true, NULL,'food_can',   11),
( 7,'crackers',       '크래커',           'FOOD','COMMON',  true, NULL,'food_snack',  5),
( 8,'beef_jerky',     '육포',             'FOOD','UNCOMMON',true, NULL,'food_snack', 20),
( 9,'energy_bar',     '에너지바',         'FOOD','UNCOMMON',true, NULL,'food_snack', 14),
(10,'chocolate',      '초콜릿바',         'FOOD','UNCOMMON',true, NULL,'food_snack', 12),
(11,'instant_noodle', '라면',             'FOOD','COMMON',  true, NULL,'food_snack',  7),
(12,'rice_bag',       '쌀 봉지',          'FOOD','UNCOMMON',true, NULL,'food_snack', 22),
(13,'dog_food',       '개 사료',          'FOOD','COMMON',  true, NULL,'food_can',    6),
(14,'cat_food',       '고양이 사료',      'FOOD','COMMON',  true, NULL,'food_can',    6),
(15,'water_bottle',   '생수',             'FOOD','COMMON',  true, NULL,'food_water',  6),
(16,'canteen',        '수통',             'FOOD','UNCOMMON',true, NULL,'food_water', 16),
(17,'soda_can',       '탄산음료',         'FOOD','COMMON',  true, NULL,'food_water',  5),
(18,'energy_drink',   '에너지 드링크',    'FOOD','UNCOMMON',true, NULL,'food_water', 13),
(19,'instant_coffee', '인스턴트 커피',    'FOOD','UNCOMMON',true, NULL,'food_snack', 15),
(20,'dried_fruit',    '건과일',           'FOOD','COMMON',  true, NULL,'food_snack',  8),
(21,'peanut_butter',  '땅콩버터',         'FOOD','UNCOMMON',true, NULL,'food_can',   17),
(22,'honey_jar',      '꿀단지',           'FOOD','RARE',    true, NULL,'food_can',   40),
(23,'pickled_veggies','절임 채소',        'FOOD','COMMON',  true, NULL,'food_can',    9),
(24,'hardtack',       '건빵',             'FOOD','COMMON',  true, NULL,'food_snack',  6),
(25,'mre_ration',     '전투식량(MRE)',    'FOOD','RARE',    true, NULL,'food_can',   45),
(26,'candy',          '사탕',             'FOOD','COMMON',  true, NULL,'food_snack',  4),
(27,'potato_chips',   '감자칩',           'FOOD','COMMON',  true, NULL,'food_snack',  6),
(28,'powdered_milk',  '분유',             'FOOD','UNCOMMON',true, NULL,'food_snack', 19),
(29,'salt',           '소금',             'FOOD','COMMON',  true, NULL,'food_snack',  5),
(30,'sugar',          '설탕',             'FOOD','COMMON',  true, NULL,'food_snack',  5),
-- 힐템 (MEDICAL) -------------------------------------------------------------
(31,'bandage',        '붕대',             'MEDICAL','COMMON',  true, NULL,'med_bandage', 12),
(32,'clean_bandage',  '소독 붕대',        'MEDICAL','UNCOMMON',true, NULL,'med_bandage', 22),
(33,'gauze',          '거즈',             'MEDICAL','COMMON',  true, NULL,'med_bandage', 10),
(34,'painkillers',    '진통제',           'MEDICAL','UNCOMMON',true, NULL,'med_pills',   25),
(35,'antibiotics',    '항생제',           'MEDICAL','RARE',    true, NULL,'med_pills',   60),
(36,'disinfectant',   '소독약',           'MEDICAL','UNCOMMON',true, NULL,'med_pills',   20),
(37,'splint',         '부목',             'MEDICAL','UNCOMMON',true, NULL,'med_bandage', 18),
(38,'tourniquet',     '지혈대',           'MEDICAL','RARE',    true, NULL,'med_bandage', 55),
(39,'medkit',         '구급상자',         'MEDICAL','RARE',    true, NULL,'med_kit',    120),
(40,'surgical_kit',   '수술 키트',        'MEDICAL','EPIC',    true, NULL,'med_kit',    300),
(41,'morphine',       '모르핀',           'MEDICAL','EPIC',    true, NULL,'med_kit',    280),
(42,'adrenaline',     '아드레날린 주사',  'MEDICAL','RARE',    true, NULL,'med_kit',     90),
(43,'antiseptic_wipes','소독 티슈',       'MEDICAL','COMMON',  true, NULL,'med_bandage',  8),
(44,'stitch_kit',     '봉합 키트',        'MEDICAL','UNCOMMON',true, NULL,'med_bandage', 35),
(45,'vitamins',       '비타민',           'MEDICAL','COMMON',  true, NULL,'med_pills',   14),
(46,'antidote',       '해독제',           'MEDICAL','RARE',    true, NULL,'med_pills',   70),
(47,'blood_bag',      '수혈팩',           'MEDICAL','EPIC',    true, NULL,'med_kit',    200),
(48,'ice_pack',       '얼음팩',           'MEDICAL','COMMON',  true, NULL,'med_kit',      9),
(49,'burn_cream',     '화상 연고',        'MEDICAL','UNCOMMON',true, NULL,'med_bandage', 28),
(50,'eye_drops',      '안약',             'MEDICAL','COMMON',  true, NULL,'med_pills',   11),
(51,'fever_reducer',  '해열제',           'MEDICAL','UNCOMMON',true, NULL,'med_pills',   24),
(52,'iodine',         '요오드팅크',       'MEDICAL','UNCOMMON',true, NULL,'med_pills',   21),
-- 근접무기 (MELEE) - 유니크 인스턴스 ----------------------------------------
(53,'kitchen_knife',  '식칼',             'MELEE','COMMON',  false, 40,'melee_knife',  30),
(54,'combat_knife',   '전투용 나이프',    'MELEE','UNCOMMON',false, 90,'melee_knife', 120),
(55,'machete',        '마체테',           'MELEE','UNCOMMON',false,110,'melee_knife', 150),
(56,'baseball_bat',   '야구방망이',       'MELEE','COMMON',  false, 70,'melee_bat',    45),
(57,'nail_bat',       '못 박힌 방망이',   'MELEE','UNCOMMON',false, 60,'melee_bat',    90),
(58,'crowbar',        '쇠지렛대',         'MELEE','UNCOMMON',false,200,'melee_bat',   110),
(59,'fire_axe',       '소방도끼',         'MELEE','RARE',    false,150,'melee_axe',   220),
(60,'hatchet',        '손도끼',           'MELEE','UNCOMMON',false,100,'melee_axe',    80),
(61,'sledgehammer',   '대형 해머',        'MELEE','RARE',    false,180,'melee_bat',   200),
(62,'pipe_wrench',    '파이프 렌치',      'MELEE','COMMON',  false,160,'melee_bat',    55),
(63,'katana',         '카타나',           'MELEE','EPIC',    false,120,'melee_knife', 600),
(64,'machinist_hammer','쇠망치',          'MELEE','COMMON',  false,140,'melee_bat',    40),
(65,'shovel',         '삽',               'MELEE','COMMON',  false,120,'melee_axe',    50),
(66,'pitchfork',      '쇠스랑',           'MELEE','UNCOMMON',false, 90,'melee_bat',    70),
(67,'chainsaw',       '전기톱',           'MELEE','EPIC',    false, 80,'melee_axe',   700),
(68,'cleaver',        '정육도',           'MELEE','UNCOMMON',false, 70,'melee_knife',  85),
(69,'police_baton',   '삼단봉',           'MELEE','UNCOMMON',false,130,'melee_bat',    95),
(70,'spiked_mace',    '철퇴',             'MELEE','RARE',    false,110,'melee_bat',   260),
(71,'wooden_spear',   '나무 창',          'MELEE','COMMON',  false, 50,'melee_bat',    35),
(72,'scythe',         '낫',               'MELEE','RARE',    false,100,'melee_knife', 240),
(73,'brass_knuckles', '너클',             'MELEE','UNCOMMON',false,150,'melee_knife', 100),
-- 총 (GUN) - 유니크 인스턴스, 아포칼립스라 대체로 RARE 이상 ------------------
(74,'makarov_pistol', '마카로프 권총',    'GUN','RARE',     false,300,'gun_pistol',   700),
(75,'glock_pistol',   '글록 권총',        'GUN','RARE',     false,350,'gun_pistol',   950),
(76,'revolver',       '리볼버',           'GUN','RARE',     false,400,'gun_pistol',   800),
(77,'desert_eagle',   '데저트 이글',      'GUN','EPIC',     false,300,'gun_pistol',  2200),
(78,'sawed_shotgun',  '소드오프 샷건',    'GUN','RARE',     false,250,'gun_shotgun', 1100),
(79,'pump_shotgun',   '펌프 샷건',        'GUN','EPIC',     false,350,'gun_shotgun', 1800),
(80,'double_shotgun', '더블배럴 샷건',    'GUN','RARE',     false,280,'gun_shotgun', 1300),
(81,'uzi_smg',        'UZI 기관단총',     'GUN','EPIC',     false,300,'gun_rifle',   2600),
(82,'mp5_smg',        'MP5 기관단총',     'GUN','EPIC',     false,400,'gun_rifle',   3200),
(83,'ak47_rifle',     'AK-47 소총',       'GUN','EPIC',     false,500,'gun_rifle',   4500),
(84,'m4_rifle',       'M4 소총',          'GUN','LEGENDARY',false,500,'gun_rifle',   5500),
(85,'hunting_rifle',  '사냥용 소총',      'GUN','RARE',     false,450,'gun_rifle',   1600),
(86,'sniper_rifle',   '저격소총',         'GUN','LEGENDARY',false,400,'gun_rifle',   8000),
(87,'lever_rifle',    '레버액션 소총',    'GUN','RARE',     false,380,'gun_rifle',   1400),
(88,'flare_gun',      '조명탄 발사기',    'GUN','UNCOMMON', false,120,'gun_pistol',   300),
(89,'nail_gun',       '네일건',           'GUN','UNCOMMON', false,200,'gun_pistol',   250),
(90,'crossbow',       '석궁',             'GUN','RARE',     false,250,'gun_rifle',    900),
(91,'compound_bow',   '컴파운드 보우',    'GUN','RARE',     false,220,'gun_rifle',    850),
(92,'grenade_launcher','유탄발사기',      'GUN','LEGENDARY',false,300,'gun_rifle',  12000),
-- 탄약 (AMMO) - 스택형 ------------------------------------------------------
(93,'ammo_9mm',       '9mm 탄약',         'AMMO','COMMON',  true, NULL,'ammo_box',     4),
(94,'ammo_45acp',     '.45 ACP 탄약',     'AMMO','UNCOMMON',true, NULL,'ammo_box',     6),
(95,'ammo_762',       '7.62mm 탄약',      'AMMO','UNCOMMON',true, NULL,'ammo_box',     8),
(96,'ammo_556',       '5.56mm 탄약',      'AMMO','UNCOMMON',true, NULL,'ammo_box',     8),
(97,'ammo_12gauge',   '12게이지 산탄',    'AMMO','UNCOMMON',true, NULL,'ammo_shell',   7),
(98,'ammo_308',       '.308 탄약',        'AMMO','RARE',    true, NULL,'ammo_box',    12),
(99,'ammo_357',       '.357 매그넘 탄약', 'AMMO','UNCOMMON',true, NULL,'ammo_box',     9),
(100,'ammo_bolt',     '석궁 볼트',        'AMMO','COMMON',  true, NULL,'ammo_shell',   5),
(101,'ammo_arrow',    '화살',             'AMMO','COMMON',  true, NULL,'ammo_shell',   4),
(102,'ammo_flare',    '조명탄',           'AMMO','UNCOMMON',true, NULL,'ammo_shell',  10);

-- ---- 신규 스택/유니크 아이템(107+) : FOOD/MEDICAL/MELEE/GUN/AMMO 라운드아웃 -------------
-- 기존 id 1~106 은 그대로 두고 append 만 한다(통합테스트/시드 인벤토리가 특정 id에 의존).
INSERT INTO item_template
    (id, code, name, category, rarity, stackable, max_durability, icon, base_value) VALUES
-- 먹을거 (FOOD) 107-112
(107,'jam_jar',       '잼 병',           'FOOD','COMMON',  true, NULL,'jam_jar',       9),
(108,'protein_powder','단백질 파우더',   'FOOD','UNCOMMON',true, NULL,'protein_powder',24),
(109,'apple',         '사과',            'FOOD','COMMON',  true, NULL,'apple',         5),
(110,'mushrooms',     '버섯',            'FOOD','UNCOMMON',true, NULL,'mushrooms',    14),
(111,'canned_ham',    '햄 통조림',       'FOOD','UNCOMMON',true, NULL,'canned_ham',   19),
(112,'field_ration',  '야전식량',        'FOOD','RARE',    true, NULL,'field_ration', 42),
-- 힐템 (MEDICAL) 113-117
(113,'stimulant_syringe','자극제 주사기','MEDICAL','RARE',    true, NULL,'stimulant_syringe',85),
(114,'inhaler',       '흡입기',          'MEDICAL','UNCOMMON',true, NULL,'inhaler',      30),
(115,'antiseptic_spray','소독 스프레이', 'MEDICAL','UNCOMMON',true, NULL,'antiseptic_spray',22),
(116,'saline_bag',    '수액팩',          'MEDICAL','RARE',    true, NULL,'saline_bag',  120),
(117,'medical_gel',   '의료용 젤',       'MEDICAL','UNCOMMON',true, NULL,'medical_gel',  27),
-- 근접무기 (MELEE) 118-122 - 유니크 인스턴스
(118,'combat_axe',    '전투 도끼',       'MELEE','RARE',    false,160,'combat_axe',   240),
(119,'war_hammer',    '워해머',          'MELEE','RARE',    false,190,'war_hammer',   230),
(120,'tactical_tomahawk','전술 토마호크','MELEE','UNCOMMON',false,120,'tactical_tomahawk',130),
(121,'steel_pipe',    '강철 파이프',     'MELEE','COMMON',  false,150,'steel_pipe',    45),
(122,'long_spear',    '장창',            'MELEE','UNCOMMON',false, 80,'long_spear',    95),
-- 총 (GUN) 123-127 - 유니크 인스턴스
(123,'magnum_revolver','매그넘 리볼버',  'GUN','EPIC',     false,350,'magnum_revolver',2400),
(124,'vector_smg',    '벡터 기관단총',   'GUN','EPIC',     false,320,'vector_smg',   3000),
(125,'tactical_shotgun','전술 샷건',     'GUN','EPIC',     false,360,'tactical_shotgun',2000),
(126,'marksman_rifle','지정사수 소총',   'GUN','LEGENDARY',false,420,'marksman_rifle',6500),
(127,'rocket_launcher','로켓 발사기',    'GUN','LEGENDARY',false,300,'rocket_launcher',15000),
-- 탄약 (AMMO) 128-132 - 스택형
(128,'ammo_38special','.38 스페셜 탄약', 'AMMO','COMMON',  true, NULL,'ammo_38special', 5),
(129,'ammo_44magnum', '.44 매그넘 탄약', 'AMMO','RARE',    true, NULL,'ammo_44magnum', 13),
(130,'ammo_slug',     '산탄 슬러그',     'AMMO','UNCOMMON',true, NULL,'ammo_slug',     10),
(131,'ammo_762x54',   '7.62x54R 탄약',   'AMMO','RARE',    true, NULL,'ammo_762x54',   12),
(132,'ammo_50cal',    '.50 구경 탄약',   'AMMO','EPIC',    true, NULL,'ammo_50cal',    30);

-- 장비/컨테이너 (GEAR) - 유니크 인스턴스. 백팩/리그는 내부 그리드(중첩 컨테이너) 보유 ---------
INSERT INTO item_template
    (id, code, name, category, rarity, stackable, max_durability, icon, base_value,
     grid_w, grid_h, equip_slot, is_container, container_w, container_h) VALUES
(103,'combat_helmet','전투 헬멧',   'GEAR','RARE',    false,120,'equip_helmet',   300, 2, 2,'HELMET',   false, NULL, NULL),
(104,'body_armor',   '방탄 조끼',   'GEAR','EPIC',    false,240,'equip_armor',    900, 2, 3,'ARMOR',    false, NULL, NULL),
(105,'tactical_rig', '전술 리그',   'GEAR','RARE',    false,100,'equip_rig',      450, 2, 2,'RIG',      true,     4,    3),
(106,'backpack',     '배낭',        'GEAR','UNCOMMON',false,100,'equip_backpack', 350, 3, 3,'BACKPACK', true,     5,    5),
-- ---- 신규 장비 패밀리(133-149) : 헬멧/방어구 티어 + 리그/백팩 내부그리드 크기 ----------
-- 헬멧(HELMET) 133-137 : 경량 → 중장 티어. 색조로 티어를 구분한다.
(133,'light_helmet',    '경량 헬멧',   'GEAR','UNCOMMON',false, 70,'light_helmet',    180, 2, 2,'HELMET', false, NULL, NULL),
(134,'tactical_helmet', '전술 헬멧',   'GEAR','RARE',    false,110,'tactical_helmet', 320, 2, 2,'HELMET', false, NULL, NULL),
(135,'heavy_helmet',    '중장 헬멧',   'GEAR','EPIC',    false,180,'heavy_helmet',    650, 2, 2,'HELMET', false, NULL, NULL),
(136,'ballistic_helmet','방탄 헬멧',   'GEAR','EPIC',    false,200,'ballistic_helmet',720, 2, 2,'HELMET', false, NULL, NULL),
(137,'riot_helmet',     '방폭 헬멧',   'GEAR','RARE',    false,150,'riot_helmet',     400, 2, 2,'HELMET', false, NULL, NULL),
-- 방어구(ARMOR) 138-141 : 케블라/플레이트/중장/방폭.
(138,'kevlar_vest',     '케블라 조끼', 'GEAR','RARE',    false,160,'kevlar_vest',     500, 2, 3,'ARMOR',  false, NULL, NULL),
(139,'plate_carrier',   '플레이트 캐리어','GEAR','EPIC', false,260,'plate_carrier',  1100, 2, 3,'ARMOR',  false, NULL, NULL),
(140,'heavy_armor',     '중장갑 방어구','GEAR','LEGENDARY',false,360,'heavy_armor',   1800, 2, 3,'ARMOR',  false, NULL, NULL),
(141,'riot_armor',      '방폭 방어구', 'GEAR','RARE',    false,220,'riot_armor',      600, 2, 3,'ARMOR',  false, NULL, NULL),
-- 체스트 리그(RIG) 142-145 : 내부 그리드 크기 3×2 → 4×4.
(142,'light_rig',   '경량 리그',   'GEAR','UNCOMMON',false, 60,'light_rig',   200, 2, 2,'RIG', true, 3, 2),
(143,'scout_rig',   '정찰 리그',   'GEAR','RARE',    false, 90,'scout_rig',   360, 2, 2,'RIG', true, 3, 3),
(144,'assault_rig', '돌격 리그',   'GEAR','RARE',    false,120,'assault_rig', 560, 2, 3,'RIG', true, 4, 3),
(145,'heavy_rig',   '중장 리그',   'GEAR','EPIC',    false,150,'heavy_rig',   820, 2, 3,'RIG', true, 4, 4),
-- 백팩(BACKPACK) 146-149 : 소형 3×3 → 대형 5×5 + 더플백.
(146,'small_backpack', '소형 배낭', 'GEAR','COMMON',  false, 80,'small_backpack', 220, 2, 2,'BACKPACK', true, 3, 3),
(147,'medium_backpack','중형 배낭', 'GEAR','UNCOMMON',false,110,'medium_backpack',420, 3, 3,'BACKPACK', true, 4, 4),
(148,'large_backpack', '대형 배낭', 'GEAR','RARE',    false,140,'large_backpack', 780, 3, 4,'BACKPACK', true, 5, 5),
(149,'duffel_bag',     '더플백',    'GEAR','UNCOMMON',false,120,'duffel_bag',     500, 3, 3,'BACKPACK', true, 5, 5);

-- 기존 총(GUN) 카탈로그를 WEAPON 슬롯 장착 가능으로 매핑.
UPDATE item_template SET equip_slot = 'WEAPON' WHERE category = 'GUN';

-- ---- 스태시 footprint(grid_w×grid_h) --------------------------------------
-- 스택형(FOOD/MEDICAL/AMMO)은 기본 1×1. 유니크 무기만 크기를 준다.
UPDATE item_template SET grid_w = 1, grid_h = 2
  WHERE code IN ('kitchen_knife','combat_knife','cleaver','brass_knuckles','hatchet','machinist_hammer',
                 'tactical_tomahawk');
UPDATE item_template SET grid_w = 1, grid_h = 3
  WHERE code IN ('machete','baseball_bat','nail_bat','crowbar','fire_axe','pipe_wrench','katana',
                 'shovel','pitchfork','police_baton','spiked_mace','wooden_spear','scythe',
                 'combat_axe','steel_pipe','long_spear');
UPDATE item_template SET grid_w = 2, grid_h = 3
  WHERE code IN ('sledgehammer','chainsaw','war_hammer');
-- 총: 권총류 2×2, 소총/샷건류 4×2
UPDATE item_template SET grid_w = 2, grid_h = 2
  WHERE code IN ('makarov_pistol','glock_pistol','revolver','desert_eagle','flare_gun','nail_gun',
                 'magnum_revolver');
UPDATE item_template SET grid_w = 4, grid_h = 2
  WHERE code IN ('sawed_shotgun','pump_shotgun','double_shotgun','uzi_smg','mp5_smg','ak47_rifle',
                 'm4_rifle','hunting_rifle','sniper_rifle','lever_rifle','crossbow','compound_bow','grenade_launcher',
                 'vector_smg','tactical_shotgun','marksman_rifle','rocket_launcher');

-- ---- max_stack (한 칸에 쌓을 수 있는 최대 수량) : 카테고리 기본값 -----------------
-- 유니크(MELEE/GUN/GEAR)는 기본 1. 스택형만 카테고리별 상한을 준다. 초과분은 새 스택으로 분리.
UPDATE item_template SET max_stack = 60 WHERE category = 'AMMO';
UPDATE item_template SET max_stack = 10 WHERE category = 'FOOD';
UPDATE item_template SET max_stack = 5  WHERE category = 'MEDICAL';

-- ---- 스프라이트 매핑: 아이템마다 전용 스프라이트 1장(icon = code) ------------------
-- tools/gen-sprites.mjs 가 code.svg 를 생성한다. 위 시드의 공유 icon 값은 여기서 무효화된다.
UPDATE item_template SET icon = code;

-- ============================================================================
--  시드: 개발용 플레이어 3명 + 지갑 + 초기 인벤토리
-- ============================================================================
INSERT INTO player(id, display_name) VALUES
  ('11111111-1111-1111-1111-111111111111', 'Survivor_Alpha'),
  ('22222222-2222-2222-2222-222222222222', 'Survivor_Bravo'),
  ('33333333-3333-3333-3333-333333333333', 'Trader_Charlie'),
  -- 레이드(익스트랙션) 데모/테스트 전용. 시작 인벤토리는 비어 있음(테스트가 필요분을 지급).
  ('44444444-4444-4444-4444-444444444444', 'Raider_Delta'),
  ('55555555-5555-5555-5555-555555555555', 'Raider_Echo'),
  ('66666666-6666-6666-6666-666666666666', 'Raider_Foxtrot'),
  -- 장비(equipment)/중첩 컨테이너 데모/테스트 전용. 시작 인벤토리는 비어 있음.
  ('77777777-7777-7777-7777-777777777777', 'Gearhead_Golf'),
  ('88888888-8888-8888-8888-888888888888', 'Gearhead_Hotel');

INSERT INTO wallet(player_id, balance) VALUES
  ('11111111-1111-1111-1111-111111111111', 10000),
  ('22222222-2222-2222-2222-222222222222', 10000),
  ('33333333-3333-3333-3333-333333333333', 50000),
  ('44444444-4444-4444-4444-444444444444', 10000),
  ('55555555-5555-5555-5555-555555555555', 10000),
  ('66666666-6666-6666-6666-666666666666', 10000),
  ('77777777-7777-7777-7777-777777777777', 10000),
  ('88888888-8888-8888-8888-888888888888', 10000);

-- Alpha: 스택형 재고(먹을거/힐템/탄약)
INSERT INTO inventory_stack(player_id, template_id, quantity) VALUES
  ('11111111-1111-1111-1111-111111111111',  1, 20),   -- 통조림 콩
  ('11111111-1111-1111-1111-111111111111', 31, 15),   -- 붕대
  ('11111111-1111-1111-1111-111111111111', 95, 120),  -- 7.62mm 탄약
  ('22222222-2222-2222-2222-222222222222', 15, 30),   -- 생수
  ('22222222-2222-2222-2222-222222222222', 93, 200);  -- 9mm 탄약

-- Bravo: 유니크 무기 인스턴스(내구도/부착물)
INSERT INTO item_instance(id, template_id, owner_player_id, durability, attachments) VALUES
  ('aaaa1111-0000-0000-0000-000000000001', 83, '22222222-2222-2222-2222-222222222222', 460, '["red_dot","extended_mag"]'::jsonb),  -- AK-47
  ('aaaa1111-0000-0000-0000-000000000002', 63, '22222222-2222-2222-2222-222222222222', 118, '[]'::jsonb),                            -- 카타나
  ('aaaa1111-0000-0000-0000-000000000003', 75, '11111111-1111-1111-1111-111111111111', 350, '["suppressor"]'::jsonb);              -- 글록

COMMIT;

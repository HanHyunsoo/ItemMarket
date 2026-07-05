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
    CHECK (stackable = (max_durability IS NULL))
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
    owner_player_id UUID REFERENCES player(id),   -- NULL = 에스크로/이동중
    durability      INT,
    attachments     JSONB NOT NULL DEFAULT '[]'::jsonb,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_item_instance_owner ON item_instance(owner_player_id);

-- ----------------------------------------------------------------------------
-- 스태시 배치(그리드 인벤토리)
--   플레이어별 N×M 스태시 위 아이템 배치. (x,y)=좌상단 칸, footprint는 템플릿의 grid_w×grid_h.
--   스택형은 (player, template) 당 한 칸(1×1), 유니크는 인스턴스별로 배치.
--   서버 권위 검증: 경계 밖·겹침 금지(애플리케이션 계층에서 판정).
-- ----------------------------------------------------------------------------
CREATE TABLE stash_placement (
    player_id   UUID NOT NULL REFERENCES player(id),
    kind        TEXT NOT NULL,                         -- STACK / INSTANCE
    template_id INT  NOT NULL REFERENCES item_template(id),
    instance_id UUID REFERENCES item_instance(id),     -- INSTANCE일 때만
    x           INT  NOT NULL CHECK (x >= 0),
    y           INT  NOT NULL CHECK (y >= 0),
    -- 유니크 아이템은 인스턴스 단위로 유일(같은 템플릿 무기를 여러 자루 소유 가능).
    CONSTRAINT uq_stash_instance UNIQUE (instance_id),
    CHECK ((kind = 'INSTANCE') = (instance_id IS NOT NULL))
);
CREATE INDEX idx_stash_player ON stash_placement(player_id);
-- 스택형만 (player, template) 당 1개. 부분 유니크 인덱스라 INSTANCE 행에는 적용되지 않는다
-- (INSTANCE는 uq_stash_instance로 인스턴스별 유일 → 동일 템플릿 무기 다수 보유 허용).
CREATE UNIQUE INDEX uq_stash_stack ON stash_placement(player_id, template_id) WHERE kind = 'STACK';

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

-- ---- 스태시 footprint(grid_w×grid_h) --------------------------------------
-- 스택형(FOOD/MEDICAL/AMMO)은 기본 1×1. 유니크 무기만 크기를 준다.
UPDATE item_template SET grid_w = 1, grid_h = 2
  WHERE code IN ('kitchen_knife','combat_knife','cleaver','brass_knuckles','hatchet','machinist_hammer');
UPDATE item_template SET grid_w = 1, grid_h = 3
  WHERE code IN ('machete','baseball_bat','nail_bat','crowbar','fire_axe','pipe_wrench','katana',
                 'shovel','pitchfork','police_baton','spiked_mace','wooden_spear','scythe');
UPDATE item_template SET grid_w = 2, grid_h = 3
  WHERE code IN ('sledgehammer','chainsaw');
-- 총: 권총류 2×2, 소총/샷건류 4×2
UPDATE item_template SET grid_w = 2, grid_h = 2
  WHERE code IN ('makarov_pistol','glock_pistol','revolver','desert_eagle','flare_gun','nail_gun');
UPDATE item_template SET grid_w = 4, grid_h = 2
  WHERE code IN ('sawed_shotgun','pump_shotgun','double_shotgun','uzi_smg','mp5_smg','ak47_rifle',
                 'm4_rifle','hunting_rifle','sniper_rifle','lever_rifle','crossbow','compound_bow','grenade_launcher');

-- ============================================================================
--  시드: 개발용 플레이어 3명 + 지갑 + 초기 인벤토리
-- ============================================================================
INSERT INTO player(id, display_name) VALUES
  ('11111111-1111-1111-1111-111111111111', 'Survivor_Alpha'),
  ('22222222-2222-2222-2222-222222222222', 'Survivor_Bravo'),
  ('33333333-3333-3333-3333-333333333333', 'Trader_Charlie');

INSERT INTO wallet(player_id, balance) VALUES
  ('11111111-1111-1111-1111-111111111111', 10000),
  ('22222222-2222-2222-2222-222222222222', 10000),
  ('33333333-3333-3333-3333-333333333333', 50000);

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

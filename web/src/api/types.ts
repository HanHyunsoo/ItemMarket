// TypeScript mirror of ItemMarket.Contracts C# DTOs.
// JSON convention assumed: property names camelCase (ASP.NET Core Web default),
// enum values PascalCase strings (JsonStringEnumConverter default).

// ---- Enums (serialized as strings) ----
export type OrderSide = 'Buy' | 'Sell'
export type OrderStatus = 'Open' | 'PartiallyFilled' | 'Filled' | 'Cancelled'
// GEAR = equippable gear (helmet/armor/weapon/backpack/rig).
export type ItemCategory = 'Food' | 'Medical' | 'Melee' | 'Gun' | 'Ammo' | 'Gear'
export type ItemRarity = 'Common' | 'Uncommon' | 'Rare' | 'Epic' | 'Legendary'
export type WalletLedgerReason =
  'OrderEscrow' | 'OrderRefund' | 'TradePayment' | 'TradeProceeds' | 'Fee' | 'AdminAdjust'
// Item-ledger provenance (item movement log). Delta sign: brought/loss = −, extract/loot/grant = +.
export type ItemLedgerReason =
  'RaidBrought' | 'RaidExtract' | 'RaidLoot' | 'RaidLoss' | 'AdminGrant'

export type ErrorCode =
  | 'Unknown'
  | 'ValidationError'
  | 'Unauthorized'
  | 'Forbidden'
  | 'PlayerNotFound'
  | 'TemplateNotFound'
  | 'InstanceNotFound'
  | 'InstanceNotOwned'
  | 'InsufficientFunds'
  | 'InsufficientQuantity'
  | 'OrderNotFound'
  | 'OrderNotOwned'
  | 'OrderAlreadyClosed'
  | 'StackableMismatch'
  | 'PlacementInvalid'
  | 'RaidActive'
  | 'RaidNotFound'
  | 'RaidNothingToDeploy'
  | 'SlotMismatch'
  | 'RateLimited'
  | 'IdempotencyInProgress'
  | 'IdempotencyUnavailable'

// ---- Common envelope ----
export interface ApiError {
  code: ErrorCode
  message: string
  details?: string[] | null
}

export interface ApiResponse<T> {
  success: boolean
  data?: T | null
  error?: ApiError | null
}

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

// ---- Auth ----
export interface LoginRequest {
  playerId: string
}

export interface TokenResponse {
  accessToken: string
  tokenType: string // "Bearer"
  accessTokenExpiresIn: number // seconds until the access token expires
  refreshToken: string // raw refresh token (client-held); server stores only its hash
  playerId: string
  displayName: string
  roles: string[]
}

export interface RefreshRequest {
  refreshToken: string
}

// ---- Items / Catalog / Inventory ----
// Which equipment slot an instance occupies. Helmet/Armor/Weapon are single-item
// slots; Backpack/Rig are single-item slots that also provide a nested grid.
export type EquipSlot = 'Helmet' | 'Armor' | 'Weapon' | 'Backpack' | 'Rig'

export interface ItemTemplateDto {
  id: number
  code: string
  name: string
  category: ItemCategory
  rarity: ItemRarity
  stackable: boolean
  maxDurability: number | null
  icon: string
  baseValue: number
  // Grid footprint (defaults 1×1).
  gridW: number
  gridH: number
  // GEAR templates: the slot they equip into, whether they carry a nested grid,
  // and that grid's size.
  equipSlot?: EquipSlot | null
  isContainer: boolean
  containerW?: number | null
  containerH?: number | null
  // Max quantity per stack (category default: Ammo 60 / Food 10 / Medical 5, unique = 1).
  // A stackable template's quantity can exceed this — it just spans multiple stacks
  // (multiple grid placements), each capped at maxStack.
  maxStack: number
}

export interface ItemInstanceDto {
  id: string
  templateId: number
  durability: number | null
  attachments: string[]
  acquiredAt: string
}

export interface InventoryStackDto {
  templateId: number
  quantity: number
}

export interface InventoryDto {
  playerId: string
  stacks: InventoryStackDto[]
  instances: ItemInstanceDto[]
}

// ---- Stash (spatial grid inventory) ----
// A placement occupies a w×h footprint at top-left cell (x, y).
export type StashItemKind = 'Stack' | 'Instance'
// Which grid a placement lives in. STASH is 12 wide × variable height (StashDto.GridH,
// backed by player.stash_rows) and never at-risk in a raid. POCKETS is an innate 4×1
// container that's always present and travels with the player into a raid (like
// equipped gear). CONTAINER is the nested grid of an equipped backpack/rig (addressed
// by containerInstanceId). Serialized PascalCase; the stash GET route segment is the
// lowercase form (e.g. /api/stash/pockets).
export type GridContainer = 'Stash' | 'Pockets' | 'Container'

export interface StashPlacementDto {
  container: GridContainer
  kind: StashItemKind
  templateId: number
  instanceId?: string | null
  x: number
  y: number
  w: number
  h: number
  // Quantity placed in *this* cell. Stackable templates may have multiple placements
  // (multi-stack) across one or more containers — don't assume a template appears once.
  quantity: number
  // Set when container === 'Container': the backpack/rig instance this grid belongs to.
  containerInstanceId?: string | null
}

export interface StashDto {
  playerId: string
  container: GridContainer
  gridW: number
  gridH: number
  placements: StashPlacementDto[]
  unplaced: StashPlacementDto[]
}

export interface MoveStashItemRequest {
  kind: StashItemKind
  templateId?: number | null
  instanceId?: string | null
  x: number
  y: number
  fromContainer: GridContainer
  toContainer: GridContainer
  // Stacks may move a partial amount; omit/null moves the whole stack. Dropping onto
  // an existing same-template stack merges up to maxStack (overflow stays behind).
  // Instances always move whole.
  quantity?: number | null
  // Required when the corresponding side is a nested Container (backpack/rig):
  // the equipped container instance id whose grid the item moves out of / into.
  // Pockets is innate (no instance id needed) — omit for either side that's Pockets.
  fromContainerInstanceId?: string | null
  toContainerInstanceId?: string | null
}

// ---- Equipment (character doll + nested containers) ----
export interface EquippedItemDto {
  slot: EquipSlot
  instanceId: string
  templateId: number
}

// The nested grid provided by an equipped backpack/rig.
export interface NestedContainerDto {
  containerInstanceId: string
  templateId: number
  slot: EquipSlot
  gridW: number
  gridH: number
  placements: StashPlacementDto[]
}

export interface EquipmentDto {
  playerId: string
  slots: EquippedItemDto[]
  containers: NestedContainerDto[]
}

export interface EquipRequest {
  slot: EquipSlot
  instanceId: string
}

export interface UnequipRequest {
  slot: EquipSlot
}

// ---- Raid / Extraction ----
// A raid session: the player deploys with everything outside the Stash (equipped
// gear + pockets + nested backpack/rig contents — equipment alone is enough),
// optionally loots, then resolves by extracting (items return to Stash) or dying
// (brought + looted lost).
// Statuses/kinds/sources serialize PascalCase.
export type RaidStatus = 'Active' | 'Extracted' | 'Died'
export type RaidItemKind = 'Stack' | 'Instance'
export type RaidItemSource = 'Brought' | 'Looted'

export interface RaidSessionItemDto {
  kind: RaidItemKind
  templateId: number
  instanceId?: string | null
  quantity: number
  source: RaidItemSource
}

export interface RaidSessionDto {
  id: string
  playerId: string
  status: RaidStatus
  startedAt: string
  resolvedAt?: string | null
  items: RaidSessionItemDto[]
  deadlineAt?: string | null // ACTIVE 세션 출격 마감(초과 시 탈출 실패=사망). 해결된 세션은 null.
  deathChanceBps: number // 누적 사망확률(bps). extract 시 이 확률로 사망 롤. 표시는 min(10000).
}

// 스태시 행 확장 구매 결과: 새 행 수, 지불한 캡, 갱신 잔액.
export interface StashUpgradeResultDto {
  stashRows: number
  cost: number
  balance: number
}

// 리더보드 한 줄과 스냅샷(최다 캡 / 최다 생환).
export interface LeaderEntryDto {
  playerId: string
  displayName: string
  value: number
}
export interface LeaderboardDto {
  topCaps: LeaderEntryDto[]
  topExtractions: LeaderEntryDto[]
}

// 출격 존(리스크/보상 티어). 드롭 rarity 가중치와 loot당 사망확률 상승률을 함께 결정한다.
export type RaidZone = 'Low' | 'Med' | 'High'

export interface StartRaidRequest {
  zone: RaidZone
}

// 루팅 결과: 서버가 세션 존의 rarity 가중치로 무엇을·얼마나 드롭할지 결정한다. 이번 획득(dropped)과
// 갱신 세션(session)을 함께 받는다. 마감 초과로 루팅하면 dropped=null, session.status=Died.
export interface LootResultDto {
  dropped: RaidSessionItemDto | null
  session: RaidSessionDto
}

// A resolved raid (Extracted/Died) for the history/records view. Items carry the
// brought/looted snapshot with per-line quantity.
export interface RaidHistoryEntryDto {
  id: string
  status: RaidStatus
  startedAt: string
  resolvedAt?: string | null
  items: RaidSessionItemDto[]
}

// ---- Item ledger (append-only item movement log; no running balance) ----
export interface ItemLedgerEntryDto {
  id: number
  playerId: string
  kind: StashItemKind
  templateId: number
  instanceId?: string | null
  deltaQty: number
  reason: ItemLedgerReason
  refId?: string | null
  createdAt: string
}

// ---- Wallet ----
export interface WalletDto {
  playerId: string
  balance: number
}

export interface WalletLedgerEntryDto {
  id: number
  playerId: string
  delta: number
  balanceAfter: number
  reason: WalletLedgerReason
  refId: string | null
  createdAt: string
}

// ---- Orders / Book ----
export interface PlaceOrderRequest {
  side: OrderSide
  itemTemplateId: number
  unitPrice: number
  quantity: number
  instanceId?: string | null
}

export interface OrderDto {
  id: string
  playerId: string
  side: OrderSide
  itemTemplateId: number
  unitPrice: number
  quantity: number
  remainingQuantity: number
  status: OrderStatus
  instanceId: string | null
  createdAt: string
}

export interface OrderBookLevelDto {
  unitPrice: number
  quantity: number
  orderCount: number
}

export interface OrderBookSnapshotDto {
  itemTemplateId: number
  bids: OrderBookLevelDto[]
  asks: OrderBookLevelDto[]
}

// ---- Trades ----
export interface TradeDto {
  id: string
  itemTemplateId: number
  unitPrice: number
  quantity: number
  buyerId: string
  sellerId: string
  buyOrderId: string
  sellOrderId: string
  instanceId: string | null
  feeAmount: number
  executedAt: string
}

// 종목 하나의 시세 요약(마켓 카드용). 활동 없는 종목은 best/last가 null, openOrders=0.
export interface MarketTickerDto {
  templateId: number
  bestBid: number | null
  bestAsk: number | null
  lastPrice: number | null
  lastTradeAt: string | null
  openOrders: number
}

export interface PlaceOrderResult {
  order: OrderDto
  fills: TradeDto[]
}

// ---- Admin ----
export interface AdminAdjustWalletRequest {
  playerId: string
  delta: number
  reason: string
}

export interface AdminGrantStackRequest {
  playerId: string
  templateId: number
  quantity: number
}

export interface AdminGrantInstanceRequest {
  playerId: string
  templateId: number
  durability?: number | null
  attachments?: string[] | null
}

export interface AdminForceCancelOrderRequest {
  orderId: string
  reason: string
}

// ---- Seeded dev players (from db/ddl.sql) ----
export interface SeedPlayer {
  id: string
  displayName: string
}

export const SEED_PLAYERS: SeedPlayer[] = [
  { id: '11111111-1111-1111-1111-111111111111', displayName: 'Survivor_Alpha' },
  { id: '22222222-2222-2222-2222-222222222222', displayName: 'Survivor_Bravo' },
  { id: '33333333-3333-3333-3333-333333333333', displayName: 'Trader_Charlie' },
  // Raid + gear/nested-container demo players (start with empty inventory).
  { id: '44444444-4444-4444-4444-444444444444', displayName: 'Raider_Delta' },
  { id: '55555555-5555-5555-5555-555555555555', displayName: 'Raider_Echo' },
  { id: '66666666-6666-6666-6666-666666666666', displayName: 'Raider_Foxtrot' },
  { id: '77777777-7777-7777-7777-777777777777', displayName: 'Gearhead_Golf' },
  { id: '88888888-8888-8888-8888-888888888888', displayName: 'Gearhead_Hotel' },
]

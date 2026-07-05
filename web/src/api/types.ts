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
  | 'SlotMismatch'

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
// Which grid a placement lives in. STASH is 10×12, LOADOUT is 6×8, CONTAINER is
// the nested grid of an equipped backpack/rig (addressed by containerInstanceId).
// Serialized PascalCase; the stash GET route segment is the lowercase form.
export type GridContainer = 'Stash' | 'Loadout' | 'Container'

export interface StashPlacementDto {
  container: GridContainer
  kind: StashItemKind
  templateId: number
  instanceId?: string | null
  x: number
  y: number
  w: number
  h: number
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
  // Stacks may move a partial amount; omit/null moves the whole stack.
  // Instances always move whole.
  quantity?: number | null
  // Required when the corresponding side is a nested Container (backpack/rig):
  // the equipped container instance id whose grid the item moves out of / into.
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
// A raid session: the player deploys from their LOADOUT, optionally loots, then
// resolves by extracting (items return to Stash) or dying (brought + looted lost).
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
}

export interface AddLootRequest {
  templateId: number
  quantity: number
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
